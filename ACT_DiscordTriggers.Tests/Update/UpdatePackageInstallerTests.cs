using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using ACT_DiscordTriggers.Core.Update;
using Xunit;

namespace ACT_DiscordTriggers.Tests.Update {
  public class UpdatePackageInstallerTests : IDisposable {
    private readonly string root;

    public UpdatePackageInstallerTests() {
      root = Path.Combine(Path.GetTempPath(), "actdt-inst-" + Guid.NewGuid().ToString("N"));
      Directory.CreateDirectory(root);
    }

    public void Dispose() {
      try { Directory.Delete(root, recursive: true); } catch { }
    }

    private string Sub(string name) {
      string p = Path.Combine(root, name);
      Directory.CreateDirectory(p);
      return p;
    }

    private static void Write(string path, string content) {
      Directory.CreateDirectory(Path.GetDirectoryName(path));
      File.WriteAllText(path, content);
    }

    // Build a release-shaped zip whose single top-level folder is the wrapper (stripDirs=1).
    private string MakeReleaseZip(string wrapper = "ACT_DiscordTriggers") {
      string src = Sub("src");
      string w = Path.Combine(src, wrapper);
      Write(Path.Combine(w, "ACT_DiscordTriggers.dll"), "NEW-BOOT");
      Write(Path.Combine(w, "libs", "ACT_DiscordTriggers.Core.dll"), "NEW-CORE");
      Write(Path.Combine(w, "node_modules", "x", "y.node"), "NEW-NODE");
      Write(Path.Combine(w, "bundle.js"), "NEW-BUNDLE");
      string zip = Path.Combine(root, "release.zip");
      ZipFile.CreateFromDirectory(src, zip);
      return zip;
    }

    [Fact]
    public void Install_OverwritesExisting_AddsNew_LeavesUnrelated() {
      string zip = MakeReleaseZip();
      string target = Sub("target");
      Write(Path.Combine(target, "ACT_DiscordTriggers.dll"), "OLD-BOOT");
      Write(Path.Combine(target, "libs", "ACT_DiscordTriggers.Core.dll"), "OLD-CORE");
      Write(Path.Combine(target, "bundle.js"), "OLD-BUNDLE");
      Write(Path.Combine(target, "keep.txt"), "keep");

      var inst = new UpdatePackageInstaller();
      bool ok = inst.Install(zip, target, new UpdatePackageInstaller.Options { StripDirs = 1 });

      Assert.True(ok);
      Assert.Equal("NEW-BOOT", File.ReadAllText(Path.Combine(target, "ACT_DiscordTriggers.dll")));
      Assert.Equal("NEW-CORE", File.ReadAllText(Path.Combine(target, "libs", "ACT_DiscordTriggers.Core.dll")));
      Assert.Equal("NEW-NODE", File.ReadAllText(Path.Combine(target, "node_modules", "x", "y.node")));
      Assert.Equal("NEW-BUNDLE", File.ReadAllText(Path.Combine(target, "bundle.js")));
      Assert.Equal("keep", File.ReadAllText(Path.Combine(target, "keep.txt")));
      // Scratch + backups cleaned on success.
      Assert.False(Directory.Exists(Path.Combine(target, ".update-tmp")));
      Assert.Empty(Directory.EnumerateFiles(target, "*.old*", SearchOption.AllDirectories));
    }

    [Fact]
    public void Install_ReplacesLibsWholesale_DropsRemovedAssemblies() {
      string zip = MakeReleaseZip();
      string target = Sub("target");
      Write(Path.Combine(target, "libs", "ACT_DiscordTriggers.Core.dll"), "OLD-CORE");
      // A libs/ assembly the new release no longer ships — the wholesale folder swap must
      // drop it so the simple-name resolver can't byte-load the stale copy.
      Write(Path.Combine(target, "libs", "Renamed.Old.dll"), "STALE");

      var inst = new UpdatePackageInstaller();
      bool ok = inst.Install(zip, target, new UpdatePackageInstaller.Options { StripDirs = 1 });

      Assert.True(ok);
      Assert.Equal("NEW-CORE", File.ReadAllText(Path.Combine(target, "libs", "ACT_DiscordTriggers.Core.dll")));
      Assert.False(File.Exists(Path.Combine(target, "libs", "Renamed.Old.dll")));
      // The moved-aside libs.old backup is dropped on success — no .old file or folder left.
      Assert.Empty(Directory.EnumerateFiles(target, "*.old*", SearchOption.AllDirectories));
      Assert.Empty(Directory.EnumerateDirectories(target, "*.old*", SearchOption.AllDirectories));
    }

    [Fact]
    public void SweepOldBackups_RemovesBackupFolders() {
      string dir = Sub("sweep-dirs");
      Write(Path.Combine(dir, "libs.old", "Core.dll"), "x"); // a swapped-aside libs folder
      Write(Path.Combine(dir, "libs", "Core.dll"), "live");

      int removed = UpdatePackageInstaller.SweepOldBackups(dir);

      Assert.Equal(1, removed);
      Assert.False(Directory.Exists(Path.Combine(dir, "libs.old")));
      Assert.True(File.Exists(Path.Combine(dir, "libs", "Core.dll")));
    }

    [Fact]
    public void SweepOldBackups_LeavesNonBackupFilesEndingInOld() {
      string dir = Sub("sweep-narrow");
      Write(Path.Combine(dir, "ACT_DiscordTriggers.dll.old"), "x"); // real backup
      Write(Path.Combine(dir, "config.old.txt"), "user");           // not a backup suffix
      Write(Path.Combine(dir, "data.oldbackup"), "user");           // not a backup suffix

      int removed = UpdatePackageInstaller.SweepOldBackups(dir);

      Assert.Equal(1, removed);
      Assert.True(File.Exists(Path.Combine(dir, "config.old.txt")));
      Assert.True(File.Exists(Path.Combine(dir, "data.oldbackup")));
    }

    [Fact]
    public void Install_DryRun_WritesNothing() {
      string zip = MakeReleaseZip();
      string target = Sub("target");
      Write(Path.Combine(target, "ACT_DiscordTriggers.dll"), "OLD-BOOT");

      var inst = new UpdatePackageInstaller();
      bool ok = inst.Install(zip, target, new UpdatePackageInstaller.Options { StripDirs = 1, DryRun = true });

      Assert.True(ok);
      Assert.Equal("OLD-BOOT", File.ReadAllText(Path.Combine(target, "ACT_DiscordTriggers.dll")));
      Assert.False(File.Exists(Path.Combine(target, "libs", "ACT_DiscordTriggers.Core.dll")));
      Assert.False(Directory.Exists(Path.Combine(target, ".update-tmp")));
    }

    [Fact]
    public void Install_ZipSlipEntry_IsRejected_AndNothingEscapes() {
      // Hand-craft a zip with a traversal entry under the wrapper.
      string zip = Path.Combine(root, "evil.zip");
      using (var z = ZipFile.Open(zip, ZipArchiveMode.Create)) {
        var e = z.CreateEntry("ACT_DiscordTriggers/../../escape.txt");
        using (var s = new StreamWriter(e.Open())) s.Write("pwned");
      }
      string target = Sub("target");

      var inst = new UpdatePackageInstaller();
      bool ok = inst.Install(zip, target, new UpdatePackageInstaller.Options { StripDirs = 1 });

      Assert.False(ok);
      Assert.False(File.Exists(Path.Combine(root, "escape.txt")));
      Assert.False(File.Exists(Path.GetFullPath(Path.Combine(target, "..", "escape.txt"))));
    }

    [Fact]
    public void Install_FailureMidApply_RollsBack_NoCorruption() {
      // Two files; lock one so its move-aside fails after retries, forcing a rollback.
      string src = Sub("src2");
      string w = Path.Combine(src, "ACT_DiscordTriggers");
      Write(Path.Combine(w, "a.dll"), "NEW-A");
      Write(Path.Combine(w, "z.dll"), "NEW-Z");
      string zip = Path.Combine(root, "two.zip");
      ZipFile.CreateFromDirectory(src, zip);

      string target = Sub("target2");
      string a = Path.Combine(target, "a.dll");
      string zf = Path.Combine(target, "z.dll");
      Write(a, "OLD-A");
      Write(zf, "OLD-Z");

      var inst = new UpdatePackageInstaller();
      var opts = new UpdatePackageInstaller.Options { StripDirs = 1, Retries = 2, RetryDelayMs = 1 };

      // Hold z.dll with no sharing so neither overwrite nor move-aside can touch it.
      using (new FileStream(zf, FileMode.Open, FileAccess.Read, FileShare.None)) {
        bool ok = inst.Install(zip, target, opts);
        Assert.False(ok);
      }

      // Invariant after a failed apply: every original file is intact, nothing half-written,
      // no orphan backups left behind.
      Assert.Equal("OLD-A", File.ReadAllText(a));
      Assert.Equal("OLD-Z", File.ReadAllText(zf));
      Assert.Empty(Directory.EnumerateFiles(target, "*.old*", SearchOption.AllDirectories));
      Assert.False(Directory.Exists(Path.Combine(target, ".update-tmp")));
    }

    [Fact]
    public void SweepOldBackups_RemovesBackups_KeepsRealFiles() {
      string dir = Sub("sweep");
      Write(Path.Combine(dir, "ACT_DiscordTriggers.dll.old"), "x");
      Write(Path.Combine(dir, "libs", "Core.dll.old2"), "x");
      Write(Path.Combine(dir, "keep.dll"), "real");

      int removed = UpdatePackageInstaller.SweepOldBackups(dir);

      Assert.Equal(2, removed);
      Assert.True(File.Exists(Path.Combine(dir, "keep.dll")));
      Assert.Empty(Directory.EnumerateFiles(dir, "*.old*", SearchOption.AllDirectories));
    }
  }
}
