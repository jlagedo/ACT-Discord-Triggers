using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ACT_DiscordTriggers.Core.Update {
  /// <summary>
  /// Checks the GitHub Releases API for the latest published (non-prerelease) release and
  /// downloads its archive asset. UI-agnostic and ACT-free so it unit-tests against a fake
  /// <see cref="HttpMessageHandler"/>; the feed base URL is overridable (constructor arg or
  /// the <c>ACT_DT_UPDATE_FEED</c> env var) so an integration test can point it at a local
  /// server. The <c>/releases/latest</c> endpoint already excludes prereleases.
  /// </summary>
  public sealed class GithubReleaseClient : IDisposable {
    public const string DefaultRepo = "jlagedo/ACT-Discord-Triggers";
    public const string DefaultBaseUrl = "https://api.github.com";
    public const string FeedEnvVar = "ACT_DT_UPDATE_FEED";

    private readonly HttpClient http;
    private readonly string baseUrl;
    private readonly string repo;

    static GithubReleaseClient() {
      // GitHub requires TLS 1.2+; net48 honours the system default but pin it defensively.
      try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
    }

    /// <param name="repo">owner/name slug; defaults to this project.</param>
    /// <param name="handler">Injected transport for tests; null uses a real handler.</param>
    /// <param name="baseUrl">Feed base; null falls back to the env var, then GitHub.</param>
    public GithubReleaseClient(string repo = null, HttpMessageHandler handler = null, string baseUrl = null) {
      this.repo = string.IsNullOrWhiteSpace(repo) ? DefaultRepo : repo.Trim();
      this.baseUrl = ResolveBaseUrl(baseUrl).TrimEnd('/');
      http = handler != null ? new HttpClient(handler) : new HttpClient();
      // GitHub rejects requests without a User-Agent.
      http.DefaultRequestHeaders.UserAgent.ParseAdd("ACT-DiscordTriggers-Updater");
      http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
      http.Timeout = TimeSpan.FromSeconds(30);
    }

    public static string ResolveBaseUrl(string explicitBaseUrl) {
      if (!string.IsNullOrWhiteSpace(explicitBaseUrl)) return explicitBaseUrl;
      try {
        var env = Environment.GetEnvironmentVariable(FeedEnvVar);
        if (!string.IsNullOrWhiteSpace(env)) return env;
      } catch { }
      return DefaultBaseUrl;
    }

    /// <summary>
    /// Fetch the latest release and compare it to <paramref name="current"/>. Throws on a
    /// network/parse failure (the caller decides how loud to be); never returns null.
    /// </summary>
    public async Task<UpdateInfo> GetLatestAsync(Version current, CancellationToken ct = default) {
      string url = $"{baseUrl}/repos/{repo}/releases/latest";
      string json;
      using (var resp = await http.GetAsync(url, ct).ConfigureAwait(false)) {
        resp.EnsureSuccessStatusCode();
        json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
      }
      return Parse(json, current);
    }

    /// <summary>Parse a <c>releases/latest</c> payload. Exposed for unit tests.</summary>
    public static UpdateInfo Parse(string json, Version current) {
      using (var doc = JsonDocument.Parse(json)) {
        var root = doc.RootElement;
        string tag = GetString(root, "tag_name");
        if (string.IsNullOrWhiteSpace(tag))
          throw new FormatException("Release JSON has no tag_name.");

        var version = ParseTag(tag);
        var info = new UpdateInfo {
          Version = version,
          TagName = tag,
          Name = GetString(root, "name") ?? tag,
          Notes = (GetString(root, "body") ?? string.Empty).Replace("\r\n", "\n").Trim(),
          HtmlUrl = GetString(root, "html_url"),
          DownloadUrl = PickAssetUrl(root),
          // A non-numeric tag (e.g. "nightly") yields no version, so it isn't an applicable
          // update — never newer — rather than a hard failure that dead-ends the check.
          IsNewer = version != null && Normalize(version) > Normalize(current),
        };
        return info;
      }
    }

    /// <summary>
    /// Strip a leading "v"/"V" and parse the rest as a <see cref="Version"/>; returns null
    /// for a tag that isn't a numeric version (the caller treats that as "not an update"
    /// instead of throwing).
    /// </summary>
    public static Version ParseTag(string tag) {
      string s = (tag ?? string.Empty).Trim();
      if (s.StartsWith("v", StringComparison.OrdinalIgnoreCase)) s = s.Substring(1);
      return Version.TryParse(s, out var v) ? v : null;
    }

    /// <summary>
    /// Reduce to Major.Minor.Build so a 3-part tag (2.2.0 → revision -1) compares correctly
    /// against a 4-part assembly version (2.2.0.0 → revision 0).
    /// </summary>
    public static Version Normalize(Version v) =>
      new Version(Math.Max(v.Major, 0), Math.Max(v.Minor, 0), Math.Max(v.Build, 0));

    // Prefer our wrapper-folder zip (name starts ACT_DiscordTriggers and ends .zip);
    // otherwise the first .zip asset.
    private static string PickAssetUrl(JsonElement root) {
      if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
        return null;
      string firstZip = null;
      foreach (var a in assets.EnumerateArray()) {
        string name = GetString(a, "name") ?? "";
        string dl = GetString(a, "browser_download_url");
        if (string.IsNullOrEmpty(dl) || !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;
        if (name.StartsWith("ACT_DiscordTriggers", StringComparison.OrdinalIgnoreCase)) return dl;
        if (firstZip == null) firstZip = dl;
      }
      return firstZip;
    }

    private static string GetString(JsonElement el, string prop) =>
      el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>
    /// Stream <paramref name="url"/> to <paramref name="destPath"/>, reporting 0..1 progress
    /// when the server sends a Content-Length.
    /// </summary>
    public async Task DownloadAsync(string url, string destPath, IProgress<double> progress = null, CancellationToken ct = default) {
      Directory.CreateDirectory(Path.GetDirectoryName(destPath));
      using (var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false)) {
        resp.EnsureSuccessStatusCode();
        long? total = resp.Content.Headers.ContentLength;
        using (var src = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))
        using (var dst = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, true)) {
          var buf = new byte[1 << 16];
          long read = 0;
          int n;
          while ((n = await src.ReadAsync(buf, 0, buf.Length, ct).ConfigureAwait(false)) > 0) {
            await dst.WriteAsync(buf, 0, n, ct).ConfigureAwait(false);
            read += n;
            if (total.HasValue && total.Value > 0)
              progress?.Report((double)read / total.Value);
          }
          progress?.Report(1.0);
        }
      }
    }

    public void Dispose() => http.Dispose();
  }
}
