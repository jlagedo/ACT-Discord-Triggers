using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ACT_DiscordTriggers.Core.Tts;
using Xunit;

namespace ACT_DiscordTriggers.Tests {
  // Covers OnnxDownloader's two testable seams:
  //  - PublishModelRoot: the resolve-root + atomic-replace logic (pure filesystem, no tar).
  //  - ExtractWithTar / InstallFromArchive: the real tar.exe round-trip (gated on tar.exe).
  // The real HTTP DownloadAsync is covered by the opt-in network test at the bottom.
  public class OnnxDownloaderTests {
    private sealed class TempDir : IDisposable {
      public string Path { get; }
      public TempDir() {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "act-dt-dl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
      }
      public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
    }

    private static string TarExe =>
      System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "tar.exe");
    private static bool TarAvailable => File.Exists(TarExe);

    private static bool HasStagingLeftover(string dir) =>
      Directory.GetDirectories(dir).Any(d => System.IO.Path.GetFileName(d).Contains(".stage-"));

    private static void Touch(string path, string content = "x") {
      Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
      File.WriteAllText(path, content);
    }

    // --- PublishModelRoot: resolve-root + atomic replace (no tar, no network) ----------

    [Fact]
    public void PublishModelRoot_NamedSubdir_PublishesUnderDownloadId() {
      using (var t = new TempDir()) {
        var voice = OnnxCatalog.ByFamily(OnnxCatalog.FamilyPiper).First();
        var id = voice.DownloadId;
        var staging = System.IO.Path.Combine(t.Path, ".stage");
        Touch(System.IO.Path.Combine(staging, id, "model.onnx"));      // <downloadId>/ wrapper
        Touch(System.IO.Path.Combine(staging, id, "tokens.txt"));

        OnnxDownloader.PublishModelRoot(staging, id, t.Path);

        Assert.True(OnnxCatalog.IsInstalled(voice, t.Path));
        Assert.True(File.Exists(System.IO.Path.Combine(t.Path, id, "model.onnx")));
        Assert.True(File.Exists(System.IO.Path.Combine(t.Path, id, "tokens.txt")));
      }
    }

    [Fact]
    public void PublishModelRoot_FlatStaging_PublishesUnderDownloadId() {
      using (var t = new TempDir()) {
        var voice = OnnxCatalog.ByFamily(OnnxCatalog.FamilyPiper).First();
        var id = voice.DownloadId;
        var staging = System.IO.Path.Combine(t.Path, ".stage");
        Touch(System.IO.Path.Combine(staging, "model.onnx"));          // no wrapper dir
        Touch(System.IO.Path.Combine(staging, "tokens.txt"));

        OnnxDownloader.PublishModelRoot(staging, id, t.Path);

        Assert.True(File.Exists(System.IO.Path.Combine(t.Path, id, "model.onnx")));
        Assert.True(OnnxCatalog.IsInstalled(voice, t.Path));
      }
    }

    [Fact]
    public void PublishModelRoot_ReplacesExistingInstall() {
      using (var t = new TempDir()) {
        var voice = OnnxCatalog.ByFamily(OnnxCatalog.FamilyPiper).First();
        var id = voice.DownloadId;
        var existing = System.IO.Path.Combine(t.Path, id);
        Touch(System.IO.Path.Combine(existing, "stale.txt"), "old");   // pre-existing install

        var staging = System.IO.Path.Combine(t.Path, ".stage");
        Touch(System.IO.Path.Combine(staging, id, "model.onnx"));

        OnnxDownloader.PublishModelRoot(staging, id, t.Path);

        Assert.True(File.Exists(System.IO.Path.Combine(existing, "model.onnx")));
        Assert.False(File.Exists(System.IO.Path.Combine(existing, "stale.txt")));
      }
    }

    // --- ExtractWithTar / InstallFromArchive: real tar.exe round-trip ------------------

    // Build a .tar.bz2 fixture with the same tar.exe we extract with: stage files under
    // src/<entryRoot>/... then `tar -cjf archive -C src <entryRoot>`.
    private static void MakeTarBz2(string archivePath, string srcDir, string entryRoot) {
      var psi = new ProcessStartInfo {
        FileName = TarExe,
        Arguments = "-c -j -f \"" + archivePath + "\" -C \"" + srcDir + "\" \"" + entryRoot + "\"",
        UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true,
      };
      using (var p = Process.Start(psi)) {
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new Exception("tar create failed: " + err);
      }
    }

    [Fact]
    public void InstallFromArchive_RealTarBz2_Installs_AndCleansStaging() {
      Assert.SkipUnless(TarAvailable, "tar.exe not present (Windows 10 1803+ / 11 required).");
      using (var t = new TempDir()) {
        var voice = OnnxCatalog.ByFamily(OnnxCatalog.FamilyPiper).First();
        var id = voice.DownloadId;

        // Fixture: src/<id>/{model.onnx,tokens.txt,sub/extra.bin} → <id>.tar.bz2
        var src = System.IO.Path.Combine(t.Path, "src");
        Touch(System.IO.Path.Combine(src, id, "model.onnx"));
        Touch(System.IO.Path.Combine(src, id, "tokens.txt"));
        Touch(System.IO.Path.Combine(src, id, "sub", "extra.bin"));   // nested entry
        var archive = System.IO.Path.Combine(t.Path, "pack.tar.bz2");
        MakeTarBz2(archive, src, id);

        var models = System.IO.Path.Combine(t.Path, "models");
        Directory.CreateDirectory(models);
        OnnxDownloader.InstallFromArchive(archive, id, models);

        Assert.True(OnnxCatalog.IsInstalled(voice, models));
        Assert.True(File.Exists(System.IO.Path.Combine(models, id, "model.onnx")));
        Assert.True(File.Exists(System.IO.Path.Combine(models, id, "sub", "extra.bin")));
        Assert.False(HasStagingLeftover(models));
      }
    }

    [Fact]
    public void InstallFromArchive_CorruptArchive_Throws_AndLeavesNoPartialInstall() {
      Assert.SkipUnless(TarAvailable, "tar.exe not present (Windows 10 1803+ / 11 required).");
      using (var t = new TempDir()) {
        var voice = OnnxCatalog.ByFamily(OnnxCatalog.FamilyPiper).First();
        var id = voice.DownloadId;
        var bad = System.IO.Path.Combine(t.Path, "bad.tar.bz2");
        File.WriteAllText(bad, "this is not a bzip2 archive");

        Assert.ThrowsAny<Exception>(() => OnnxDownloader.InstallFromArchive(bad, id, t.Path));

        Assert.False(Directory.Exists(System.IO.Path.Combine(t.Path, id)));
        Assert.False(HasStagingLeftover(t.Path));
      }
    }

    [Fact]
    public void ExtractWithTar_MissingTar_ThrowsClearError() {
      // We can't remove the real tar.exe, but a bogus archive path still proves the guard
      // path: when tar.exe IS present, extraction of a non-archive throws (covered above).
      // This asserts the FileNotFound guard wording is reachable in code form only when
      // tar is genuinely absent — so it is skipped where tar exists.
      Assert.SkipWhen(TarAvailable, "tar.exe is present; the missing-tar guard can't be exercised here.");
      using (var t = new TempDir()) {
        var ex = Assert.Throws<FileNotFoundException>(
          () => OnnxDownloader.ExtractWithTar(System.IO.Path.Combine(t.Path, "x.tar.bz2"), t.Path));
        Assert.Contains("tar.exe", ex.Message);
      }
    }

    // --- Network integration -----------------------------------------------------------
    // Opt-in (downloads a real ~60 MB pack from GitHub's tts-models release). Set
    // ACT_DT_NETWORK_TESTS=1 to run; CI skips it by default.
    private static bool NetworkTestsEnabled =>
      !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ACT_DT_NETWORK_TESTS"));

    // Synchronous IProgress so every reported value is captured deterministically inside
    // the download loop (Progress<T> would marshal asynchronously and race the assert).
    private sealed class SyncProgress : IProgress<double> {
      private readonly Action<double> cb;
      public SyncProgress(Action<double> cb) { this.cb = cb; }
      public void Report(double value) => cb(value);
    }

    [Fact]
    public async Task DownloadAsync_RealNetwork_Installs_WithMonotonicProgress_AndLog() {
      Assert.SkipUnless(NetworkTestsEnabled, "Set ACT_DT_NETWORK_TESTS=1 to run network integration tests.");
      using (var t = new TempDir()) {
        var voice = OnnxCatalog.Find("vits-piper-en_US-amy-medium");
        Assert.NotNull(voice);

        var progress = new List<double>();
        var logs = new List<string>();
        using (var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5)))
          await OnnxDownloader.DownloadAsync(
            voice, t.Path, new SyncProgress(progress.Add), cts.Token, logs.Add);

        // The whole tarball unpacked — not just a directory created. A Piper pack carries
        // the model, its sherpa config (.onnx.json), the token table, and the espeak-ng
        // data tree (hundreds of files); assert each really landed.
        Assert.True(OnnxCatalog.IsInstalled(voice, t.Path));
        var modelDir = System.IO.Path.Combine(t.Path, voice.DownloadId);
        var onnx = Directory.GetFiles(modelDir, "*.onnx");
        Assert.Single(onnx);
        Assert.True(new FileInfo(onnx[0]).Length > 1_000_000,
          "the .onnx is too small to be the real model — unpack looks truncated");
        Assert.NotEmpty(Directory.GetFiles(modelDir, "*.onnx.json"));
        Assert.True(File.Exists(System.IO.Path.Combine(modelDir, "tokens.txt")));
        var espeak = System.IO.Path.Combine(modelDir, "espeak-ng-data");
        Assert.True(Directory.Exists(espeak), "espeak-ng-data dir missing");
        Assert.True(Directory.GetFiles(espeak, "*", SearchOption.AllDirectories).Length > 100,
          "espeak-ng-data did not fully unpack");

        // Progress climbed from 0-ish to 100, never going backwards.
        Assert.NotEmpty(progress);
        Assert.Equal(100, progress[progress.Count - 1]);
        for (int i = 1; i < progress.Count; i++)
          Assert.True(progress[i] >= progress[i - 1], "progress went backwards");

        // Diagnostic milestones were logged.
        Assert.Contains(logs, m => m.StartsWith("Fetching "));
        Assert.Contains(logs, m => m.StartsWith("Size:"));
        Assert.Contains(logs, m => m.Contains("Installed " + voice.DownloadId));
      }
    }
  }
}
