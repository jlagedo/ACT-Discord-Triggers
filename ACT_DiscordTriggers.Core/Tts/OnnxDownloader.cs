using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace ACT_DiscordTriggers.Core.Tts {
  /// <summary>
  /// Downloads and installs ONNX voice packs straight from the curated catalog —
  /// no Discord connection needed (provisioning is C#-owned). Fetches
  /// <c>&lt;DownloadId&gt;.tar.bz2</c> from the k2-fsa <c>tts-models</c> release,
  /// extracts it with Windows' built-in <c>tar.exe</c> (bsdtar), and publishes it
  /// atomically into <c>&lt;ModelsDir&gt;/&lt;DownloadId&gt;/</c> so a present directory
  /// always means a complete install (matching <see cref="OnnxCatalog.IsInstalled"/>).
  ///
  /// Extraction uses <c>tar.exe</c> rather than a managed archive library on purpose:
  /// ACT loads every plugin into one shared process, and a sibling plugin (e.g.
  /// OverlayPlugin) bundles its own older copy of common managed deps, so a merged
  /// SharpCompress/SharpZipLib gets hijacked by the already-loaded version at bind time.
  /// <c>tar.exe</c> has no managed identity to collide on. It ships with Windows 10 1803+
  /// and Windows 11 (it is libarchive with bzip2 support); a missing <c>tar.exe</c> is
  /// reported as a clear error rather than a crash.
  /// </summary>
  public static class OnnxDownloader {
    private const string UrlTemplate =
      "https://github.com/k2-fsa/sherpa-onnx/releases/download/tts-models/{0}.tar.bz2";

    // One client per process (HttpClient is designed to be reused). The body is
    // streamed and governed by the CancellationToken, so the request timeout is
    // disabled — a slow 333 MB pack must not trip the default 100 s limit.
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient() {
      // net48 negotiates below TLS 1.2 by default; GitHub's release CDN requires >= 1.2.
      ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
      return new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>
    /// Download and install <paramref name="voice"/>'s pack into the resolved models
    /// directory. <paramref name="progress"/> reports 0..100 over the download phase and
    /// <paramref name="log"/> (optional) receives diagnostic milestones (URL, size,
    /// extraction, install path) for the Debug Log. Throws on failure (network, missing
    /// tar.exe, bad archive, cancellation) and leaves no half-installed directory behind.
    /// Re-downloading an installed voice replaces it.
    /// </summary>
    public static async Task DownloadAsync(
        OnnxVoiceInfo voice, string modelsDir,
        IProgress<double> progress, CancellationToken ct, Action<string> log = null) {
      if (voice == null) throw new ArgumentNullException(nameof(voice));
      var dir = OnnxCatalog.ResolveModelsDir(modelsDir);
      Directory.CreateDirectory(dir);

      var url = string.Format(UrlTemplate, voice.DownloadId);
      var final = Path.Combine(dir, voice.DownloadId);
      // Stage on the SAME volume as the final dir so the publishing move is an atomic
      // rename (Directory.Move across volumes throws). The leading dot + GUID keeps the
      // staging dir from ever colliding with a real <DownloadId> entry.
      var work = Path.Combine(dir, "." + voice.DownloadId + ".tmp-" + Guid.NewGuid().ToString("N"));
      var archive = work + ".tar.bz2";
      try {
        log?.Invoke("Fetching " + url);
        await DownloadToFileAsync(url, archive, progress, ct, log).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        log?.Invoke("Download complete; extracting " + voice.DownloadId + "…");
        InstallFromArchive(archive, voice.DownloadId, dir);
        log?.Invoke("Installed " + voice.DownloadId + " to " + final);
      } finally {
        TryDeleteFile(archive);
        TryDeleteDir(work);
      }
    }

    private static async Task DownloadToFileAsync(
        string url, string destFile, IProgress<double> progress, CancellationToken ct,
        Action<string> log) {
      using (var resp = await Http
          .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false)) {
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength;
        if (log != null) log("Size: " + (total.HasValue ? FormatSize(total.Value) : "unknown"));
        using (var body = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))
        using (var file = new FileStream(
            destFile, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true)) {
          var buffer = new byte[1 << 16];
          long read = 0;
          int n;
          double last = -1;
          while ((n = await body.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false)) > 0) {
            await file.WriteAsync(buffer, 0, n, ct).ConfigureAwait(false);
            read += n;
            if (progress != null && total.HasValue && total.Value > 0) {
              var pct = Math.Min(100.0, read * 100.0 / total.Value);
              if (pct - last >= 1) { progress.Report(pct); last = pct; }
            }
          }
          if (progress != null) progress.Report(100);
        }
      }
    }

    /// <summary>
    /// Extract <paramref name="archivePath"/> into a fresh staging dir, find the model
    /// root, and publish it atomically as <c>&lt;modelsDir&gt;/&lt;downloadId&gt;</c>.
    /// Separated from the network step so it is unit-testable against a fixture archive.
    /// </summary>
    internal static void InstallFromArchive(string archivePath, string downloadId, string modelsDir) {
      var work = Path.Combine(modelsDir, "." + downloadId + ".stage-" + Guid.NewGuid().ToString("N"));
      try {
        Directory.CreateDirectory(work);
        ExtractWithTar(archivePath, work);
        PublishModelRoot(work, downloadId, modelsDir);
      } finally {
        TryDeleteDir(work);
      }
    }

    /// <summary>
    /// Unpack <paramref name="archivePath"/> (a <c>.tar.bz2</c>) into
    /// <paramref name="destDir"/> using Windows' <c>tar.exe</c> (bsdtar auto-detects the
    /// bzip2 filter). Throws <see cref="FileNotFoundException"/> if tar.exe is absent
    /// (pre-1803 Windows) and <see cref="InvalidOperationException"/> on a non-zero exit.
    /// </summary>
    internal static void ExtractWithTar(string archivePath, string destDir) {
      var tar = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System), "tar.exe");
      if (!File.Exists(tar))
        throw new FileNotFoundException(
          "Windows 'tar.exe' was not found. It ships with Windows 10 1803+ and Windows 11 " +
          "and is required to unpack voice packs.", tar);

      var psi = new ProcessStartInfo {
        FileName = tar,
        // -x extract, -f <archive>, -C <dir> change to dir first. Quote both paths so a
        // models folder with spaces (e.g. under "C:\Users\First Last\") parses correctly.
        Arguments = "-x -f \"" + archivePath + "\" -C \"" + destDir + "\"",
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardError = true,
        RedirectStandardOutput = true,
      };

      using (var p = Process.Start(psi)) {
        // Drain both pipes async before waiting so a chatty stream can't deadlock the wait.
        var err = p.StandardError.ReadToEndAsync();
        var @out = p.StandardOutput.ReadToEndAsync();
        p.WaitForExit();
        if (p.ExitCode != 0) {
          var msg = (err.Result ?? "").Trim();
          throw new InvalidOperationException(
            "tar.exe failed (exit " + p.ExitCode + ") extracting " +
            Path.GetFileName(archivePath) + (msg.Length > 0 ? ": " + msg : "."));
        }
        _ = @out.Result;
      }
    }

    /// <summary>
    /// Move the model root out of <paramref name="stagingDir"/> to its final
    /// <c>&lt;modelsDir&gt;/&lt;downloadId&gt;</c> location with an atomic same-volume
    /// rename, replacing any existing install. Split out so the publish/replace logic is
    /// unit-testable without unpacking a real archive.
    /// </summary>
    internal static void PublishModelRoot(string stagingDir, string downloadId, string modelsDir) {
      var src = ResolveModelRoot(stagingDir, downloadId);
      var final = Path.Combine(modelsDir, downloadId);
      if (Directory.Exists(final)) Directory.Delete(final, recursive: true);
      Directory.Move(src, final);
    }

    // The k2-fsa packs wrap their files in a top-level <downloadId>/ folder, so the model
    // root is normally <staging>/<downloadId>. Fall back to a lone top-level subdirectory,
    // or the staging dir itself if the archive extracted its files flat.
    private static string ResolveModelRoot(string staging, string downloadId) {
      var named = Path.Combine(staging, downloadId);
      if (Directory.Exists(named)) return named;
      var subdirs = Directory.GetDirectories(staging);
      if (subdirs.Length == 1 && !Directory.EnumerateFiles(staging).Any()) return subdirs[0];
      return staging;
    }

    private static string FormatSize(long bytes) {
      if (bytes >= 1L << 20) return (bytes / (double)(1L << 20)).ToString("0.0") + " MB";
      if (bytes >= 1L << 10) return (bytes / (double)(1L << 10)).ToString("0.0") + " KB";
      return bytes + " B";
    }

    private static void TryDeleteFile(string path) {
      try { if (File.Exists(path)) File.Delete(path); } catch { /* best-effort cleanup */ }
    }

    private static void TryDeleteDir(string path) {
      try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { /* best-effort cleanup */ }
    }
  }
}
