using System;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using ACT_DiscordTriggers.Core.Update;
using Xunit;

namespace ACT_DiscordTriggers.Tests.Update {
  // Chains the two Core pieces over the real download code path: GithubReleaseClient parses a
  // release, DownloadAsync streams the (real) zip bytes to disk, and UpdatePackageInstaller
  // applies them. Uses a stub transport (no server/URL-ACL needed) but exercises the actual
  // HTTP read + zip extract + file swap together.
  public class UpdateChainTests : IDisposable {
    private readonly string root;

    public UpdateChainTests() {
      root = Path.Combine(Path.GetTempPath(), "actdt-chain-" + Guid.NewGuid().ToString("N"));
      Directory.CreateDirectory(root);
    }

    public void Dispose() {
      try { Directory.Delete(root, true); } catch { }
    }

    [Fact]
    public async Task Check_Download_Install_AppliesRelease() {
      // Build a release-shaped zip (wrapper folder → stripDirs=1).
      string src = Path.Combine(root, "src", "ACT_DiscordTriggers");
      Directory.CreateDirectory(Path.Combine(src, "libs"));
      File.WriteAllText(Path.Combine(src, "ACT_DiscordTriggers.dll"), "NEW-BOOT");
      File.WriteAllText(Path.Combine(src, "libs", "ACT_DiscordTriggers.Main.dll"), "NEW-MAIN");
      string zip = Path.Combine(root, "ACT_DiscordTriggers-v2.2.0.zip");
      ZipFile.CreateFromDirectory(Path.Combine(root, "src"), zip);
      byte[] zipBytes = File.ReadAllBytes(zip);

      string json = $@"{{
        ""tag_name"": ""v2.2.0"",
        ""name"": ""Release v2.2.0"",
        ""body"": ""notes"",
        ""html_url"": ""http://local/releases/v2.2.0"",
        ""assets"": [ {{ ""name"": ""ACT_DiscordTriggers-v2.2.0.zip"", ""browser_download_url"": ""http://local/dl.zip"" }} ]
      }}";

      var handler = new RoutingHandler(json, zipBytes);
      string dlPath = Path.Combine(root, "downloaded.zip");
      UpdateInfo info;
      using (var client = new GithubReleaseClient("owner/repo", handler, "http://local")) {
        info = await client.GetLatestAsync(new Version(2, 1, 2, 0));
        Assert.True(info.IsNewer);
        Assert.Equal("http://local/dl.zip", info.DownloadUrl);
        await client.DownloadAsync(info.DownloadUrl, dlPath);
      }
      Assert.Equal(zipBytes, File.ReadAllBytes(dlPath));

      // Seed an old install and apply the downloaded zip over it.
      string target = Path.Combine(root, "plugin");
      Directory.CreateDirectory(Path.Combine(target, "libs"));
      File.WriteAllText(Path.Combine(target, "ACT_DiscordTriggers.dll"), "OLD-BOOT");
      File.WriteAllText(Path.Combine(target, "libs", "ACT_DiscordTriggers.Main.dll"), "OLD-MAIN");

      var installer = new UpdatePackageInstaller();
      bool ok = installer.Install(dlPath, target, new UpdatePackageInstaller.Options { StripDirs = 1 });

      Assert.True(ok);
      Assert.Equal("NEW-BOOT", File.ReadAllText(Path.Combine(target, "ACT_DiscordTriggers.dll")));
      Assert.Equal("NEW-MAIN", File.ReadAllText(Path.Combine(target, "libs", "ACT_DiscordTriggers.Main.dll")));
    }

    // Routes the releases/latest path to JSON and any other GET to the zip bytes.
    private sealed class RoutingHandler : HttpMessageHandler {
      private readonly string json;
      private readonly byte[] zip;
      public RoutingHandler(string json, byte[] zip) { this.json = json; this.zip = zip; }
      protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
        var resp = new HttpResponseMessage(HttpStatusCode.OK);
        if (request.RequestUri.AbsoluteUri.EndsWith("/releases/latest")) {
          resp.Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        } else {
          var content = new ByteArrayContent(zip);
          content.Headers.ContentLength = zip.Length;
          resp.Content = content;
        }
        return Task.FromResult(resp);
      }
    }
  }
}
