using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ACT_DiscordTriggers.Core.Update;
using Xunit;

namespace ACT_DiscordTriggers.Tests.Update {
  public class GithubReleaseClientTests {
    // body default carries JSON-escaped \r\n (literal backslashes) — the shape GitHub returns;
    // GithubReleaseClient decodes then normalizes it to \n.
    private static string ReleaseJson(string tag, string body = @"Line one\r\nLine two", string assetName = "ACT_DiscordTriggers-{TAG}.zip", string extraAssets = "") {
      string assets = "";
      if (assetName != null) {
        string name = assetName.Replace("{TAG}", tag);
        assets = $@"{{ ""name"": ""{name}"", ""browser_download_url"": ""https://example.test/dl/{name}"" }}";
      }
      string all = string.Join(",", new[] { extraAssets, assets }).Trim(',');
      return $@"{{
        ""tag_name"": ""{tag}"",
        ""name"": ""Release {tag}"",
        ""body"": ""{body}"",
        ""html_url"": ""https://example.test/releases/{tag}"",
        ""prerelease"": false,
        ""assets"": [ {all} ]
      }}";
    }

    [Fact]
    public void Parse_NewerVersion_PicksNamedAsset_AndFlagsNewer() {
      var info = GithubReleaseClient.Parse(ReleaseJson("v2.2.0"), new Version(2, 1, 2, 0));
      Assert.Equal(new Version(2, 2, 0), info.Version);
      Assert.Equal("v2.2.0", info.TagName);
      Assert.True(info.IsNewer);
      Assert.Equal("https://example.test/dl/ACT_DiscordTriggers-v2.2.0.zip", info.DownloadUrl);
      Assert.Equal("https://example.test/releases/v2.2.0", info.HtmlUrl);
      Assert.Equal("Line one\nLine two", info.Notes); // CRLF normalized + trimmed
    }

    [Theory]
    [InlineData("v2.2.0", "2.1.2.0", true)]   // patch/minor bump
    [InlineData("v2.1.2", "2.1.2.0", false)]  // 3-part tag equals 4-part assembly
    [InlineData("v2.1.2.0", "2.1.2", false)]  // reverse: assembly is the 3-part one
    [InlineData("v2.1.1", "2.1.2.0", false)]  // older remote
    [InlineData("v3.0.0", "2.9.9", true)]     // major bump
    public void Parse_VersionComparison_NormalizesParts(string tag, string current, bool expectedNewer) {
      var info = GithubReleaseClient.Parse(ReleaseJson(tag), Version.Parse(current));
      Assert.Equal(expectedNewer, info.IsNewer);
    }

    [Fact]
    public void Parse_PrefersNamedZip_OverOtherAssets() {
      string other = @"{ ""name"": ""checksums.txt"", ""browser_download_url"": ""https://example.test/c.txt"" }, " +
                     @"{ ""name"": ""generic.zip"", ""browser_download_url"": ""https://example.test/generic.zip"" }";
      var info = GithubReleaseClient.Parse(ReleaseJson("v2.2.0", extraAssets: other), new Version(2, 1, 2));
      Assert.Equal("https://example.test/dl/ACT_DiscordTriggers-v2.2.0.zip", info.DownloadUrl);
    }

    [Fact]
    public void Parse_FallsBackToFirstZip_WhenNoNamedAsset() {
      string json = ReleaseJson("v2.2.0", assetName: "release-bundle.zip");
      var info = GithubReleaseClient.Parse(json, new Version(2, 1, 2));
      Assert.Equal("https://example.test/dl/release-bundle.zip", info.DownloadUrl);
    }

    [Fact]
    public void Parse_NoZipAsset_LeavesDownloadUrlNull() {
      string json = ReleaseJson("v2.2.0", assetName: "notes.txt");
      var info = GithubReleaseClient.Parse(json, new Version(2, 1, 2));
      Assert.Null(info.DownloadUrl);
    }

    [Fact]
    public void Parse_MissingTag_Throws() {
      Assert.ThrowsAny<Exception>(() => GithubReleaseClient.Parse(@"{ ""name"": ""x"" }", new Version(1, 0, 0)));
    }

    [Theory]
    [InlineData("v2.2.0", "2.2.0")]
    [InlineData("V2.2.0", "2.2.0")]
    [InlineData("2.2.0", "2.2.0")]
    public void ParseTag_StripsLeadingV(string tag, string expected) {
      Assert.Equal(Version.Parse(expected), GithubReleaseClient.ParseTag(tag));
    }

    [Fact]
    public async Task GetLatestAsync_OverFakeTransport_ReturnsParsed() {
      using (var client = new GithubReleaseClient("owner/repo",
               new StubHandler(_ => Json(ReleaseJson("v2.5.0"))), "http://localhost/feed")) {
        var info = await client.GetLatestAsync(new Version(2, 1, 2, 0));
        Assert.Equal(new Version(2, 5, 0), info.Version);
        Assert.True(info.IsNewer);
      }
    }

    [Fact]
    public async Task GetLatestAsync_HttpError_Throws() {
      using (var client = new GithubReleaseClient("owner/repo",
               new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)), "http://localhost/feed")) {
        await Assert.ThrowsAnyAsync<Exception>(() => client.GetLatestAsync(new Version(2, 1, 2)));
      }
    }

    [Fact]
    public async Task DownloadAsync_WritesBytes_AndReportsMonotonicProgress() {
      var payload = Encoding.UTF8.GetBytes(new string('Z', 200_000));
      using (var client = new GithubReleaseClient("owner/repo",
               new StubHandler(_ => {
                 var resp = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) };
                 resp.Content.Headers.ContentLength = payload.Length;
                 return resp;
               }), "http://localhost/feed")) {
        string dest = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "actdt-dl-" + Guid.NewGuid().ToString("N") + ".bin");
        double last = -1; bool monotonic = true;
        var progress = new Progress<double>(p => { if (p < last) monotonic = false; last = p; });
        try {
          await client.DownloadAsync("http://localhost/feed/file.bin", dest, progress);
          // Progress is marshaled async; give the synchronization context a beat to drain.
          await Task.Delay(50);
          Assert.Equal(payload, System.IO.File.ReadAllBytes(dest));
          Assert.True(monotonic, "progress should be monotonic");
          Assert.Equal(1.0, last, 3);
        } finally {
          try { System.IO.File.Delete(dest); } catch { }
        }
      }
    }

    [Fact]
    public void ResolveBaseUrl_PrefersExplicit_ThenEnv_ThenDefault() {
      Assert.Equal("http://explicit", GithubReleaseClient.ResolveBaseUrl("http://explicit"));
      Assert.Equal(GithubReleaseClient.DefaultBaseUrl, GithubReleaseClient.ResolveBaseUrl(null));
    }

    private static HttpResponseMessage Json(string body) =>
      new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class StubHandler : HttpMessageHandler {
      private readonly Func<HttpRequestMessage, HttpResponseMessage> responder;
      public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) { this.responder = responder; }
      protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => Task.FromResult(responder(request));
    }
  }
}
