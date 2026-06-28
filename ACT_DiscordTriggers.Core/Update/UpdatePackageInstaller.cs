using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;

namespace ACT_DiscordTriggers.Core.Update {
  /// <summary>
  /// Applies a downloaded release archive over an existing install directory. Pure file
  /// operations — no ACT, no network, no process control — so it unit-tests against temp
  /// dirs. The caller is responsible for stopping anything that holds a native lock (the
  /// node bridge) before calling; this handles the rest:
  ///
  /// - The whole <c>libs/</c> folder is swapped wholesale (old moved aside, the release's
  ///   extracted fresh) so an assembly removed/renamed in the new release can't linger and
  ///   be byte-loaded by simple name. Its DLLs are byte-loaded (never file-locked), so the
  ///   folder is freely movable.
  /// - Other unlocked files (bundle.js, node_modules/ after the bridge is killed) are
  ///   overwritten in place.
  /// - A file locked share-delete (the bootstrap DLL, loaded by ACT) is moved aside to a
  ///   <c>.old</c> sibling and the new one written in its place; the stale <c>.old</c>
  ///   is deleted on the next launch (it stays mapped until the process exits).
  /// - On an unrecoverable failure mid-apply it rolls back the files + folders it moved.
  /// </summary>
  public sealed class UpdatePackageInstaller {
    public sealed class Options {
      /// <summary>Leading path segments to drop from each zip entry (1 = the wrapper folder).</summary>
      public int StripDirs { get; set; } = 1;

      /// <summary>Plan + log the apply but write nothing / restart nothing.</summary>
      public bool DryRun { get; set; }

      public int Retries { get; set; } = 10;
      public int RetryDelayMs { get; set; } = 300;
    }

    private readonly Action<string> log;

    public UpdatePackageInstaller(Action<string> log = null) {
      this.log = log ?? (_ => { });
    }

    /// <summary>
    /// Extract <paramref name="zipPath"/> and apply it over <paramref name="targetDir"/>.
    /// Returns true on success. <paramref name="workDir"/> is a scratch dir (defaults to a
    /// sibling of the target so moves stay on one volume); it is cleaned up on the way out.
    /// </summary>
    public bool Install(string zipPath, string targetDir, Options opts = null, string workDir = null, CancellationToken ct = default) {
      opts = opts ?? new Options();
      targetDir = Path.GetFullPath(targetDir);
      workDir = workDir ?? Path.Combine(targetDir, ".update-tmp");
      string contents = Path.Combine(workDir, "contents");
      // One move-aside ledger for both files and the libs/ folder; isDir picks the
      // file-vs-directory primitive at restore/cleanup time. Appended in apply order
      // (the folder swap first, then files), so reverse iteration undoes files first.
      var moves = new List<(string dest, string backup, bool isDir)>();

      try {
        CleanDir(workDir);
        Directory.CreateDirectory(contents);
        log($"Extracting {Path.GetFileName(zipPath)} -> {contents} (strip {opts.StripDirs})");
        Extract(zipPath, contents, opts.StripDirs, ct);

        // Clean-replace libs/ as a whole: move the existing folder aside so the release's
        // files extract into a fresh dir, dropping any assembly the new release no longer
        // ships. Only when the release actually brings a libs/ (never wipe without a
        // replacement). Move-aside keeps it rollback-safe + swept next launch.
        string libsDest = Path.Combine(targetDir, "libs");
        if (Directory.Exists(Path.Combine(contents, "libs")) && Directory.Exists(libsDest)) {
          if (opts.DryRun) {
            log("  would replace libs/ wholesale");
          } else {
            string backup = Retry(() => { string b = UniqueBackupName(libsDest); Directory.Move(libsDest, b); return b; },
                                  opts, $"move aside {libsDest}");
            moves.Add((libsDest, backup, isDir: true));
          }
        }

        var files = Directory.GetFiles(contents, "*", SearchOption.AllDirectories);
        log($"Applying {files.Length} files to {targetDir}" + (opts.DryRun ? " (DRY RUN)" : ""));

        foreach (var src in files) {
          ct.ThrowIfCancellationRequested();
          string rel = MakeRelative(contents, src);
          string dest = Path.Combine(targetDir, rel);
          if (opts.DryRun) { log($"  would apply {rel}"); continue; }
          ApplyOne(src, dest, opts, moves);
        }

        // Success: drop backups we can (the bootstrap DLL's stays locked → next-launch sweep).
        foreach (var m in moves) TryDeleteAny(m.backup, m.isDir);
        return true;
      } catch (Exception ex) {
        log($"Install FAILED: {ex.Message}");
        if (!opts.DryRun) RollBack(moves);
        return false;
      } finally {
        CleanDir(workDir);
      }
    }

    // Extract every entry, dropping `stripDirs` leading segments. Guards against zip-slip.
    private void Extract(string zipPath, string destRoot, int stripDirs, CancellationToken ct) {
      destRoot = Path.GetFullPath(destRoot);
      using (var zip = ZipFile.OpenRead(zipPath)) {
        foreach (var entry in zip.Entries) {
          ct.ThrowIfCancellationRequested();
          if (entry.Length == 0 && entry.FullName.EndsWith("/")) continue; // directory entry
          string rel = StripLeading(entry.FullName.Replace('\\', '/'), stripDirs);
          if (string.IsNullOrEmpty(rel)) continue;
          string outPath = Path.GetFullPath(Path.Combine(destRoot, rel));
          if (!outPath.StartsWith(destRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
              && !outPath.Equals(destRoot, StringComparison.OrdinalIgnoreCase))
            throw new IOException($"Zip entry escapes target: {entry.FullName}");
          Directory.CreateDirectory(Path.GetDirectoryName(outPath));
          entry.ExtractToFile(outPath, overwrite: true);
        }
      }
    }

    // Replace one file by always moving any existing target aside first (so rollback can
    // restore it), then writing the new one. Move-aside is the uniform primitive: it works
    // for unlocked files, for a share-delete lock (the ACT-loaded bootstrap DLL — renamable
    // while loaded), and for files freed by killing the bridge. Each step retries transient
    // locks. Records the move for rollback.
    private void ApplyOne(string src, string dest, Options opts, List<(string dest, string backup, bool isDir)> moves) {
      Directory.CreateDirectory(Path.GetDirectoryName(dest));
      if (File.Exists(dest)) {
        string backup = Retry(() => {
          string b = UniqueBackupName(dest);
          File.Move(dest, b);
          return b;
        }, opts, $"move aside {dest}");
        moves.Add((dest, backup, isDir: false));
      }
      Retry(() => { File.Copy(src, dest, overwrite: true); return true; }, opts, $"write {dest}");
    }

    private T Retry<T>(Func<T> action, Options opts, string what) {
      Exception last = null;
      for (int attempt = 0; attempt < opts.Retries; attempt++) {
        try { return action(); }
        catch (Exception ex) { last = ex; Thread.Sleep(opts.RetryDelayMs); }
      }
      throw new IOException($"Could not {what} after {opts.Retries} attempts.", last);
    }

    private void RollBack(List<(string dest, string backup, bool isDir)> moves) {
      log($"Rolling back {moves.Count} replaced items");
      // Reverse order so later moves undo first: files were applied after the libs/ folder
      // swap, so this undoes the files, then restores the moved-aside folder last.
      for (int i = moves.Count - 1; i >= 0; i--) {
        var (dest, backup, isDir) = moves[i];
        try {
          TryDeleteAny(dest, isDir);                       // remove the partially-applied new item
          if (Exists(backup, isDir)) MoveAny(backup, dest, isDir); // restore the original
        } catch (Exception ex) {
          log($"  rollback of {dest} failed: {ex.Message}");
        }
      }
    }

    // Unique ".old"/".oldN" sibling that collides with neither an existing file nor folder
    // (backups are taken for both).
    private static string UniqueBackupName(string dest) {
      for (int n = 0; ; n++) {
        string candidate = dest + ".old" + (n == 0 ? "" : n.ToString());
        if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
      }
    }

    // Matches exactly the backup suffix UniqueBackupName produces: ".old" or ".old" + digits.
    // Narrower than a "*.old*" glob, which would also match unrelated user files such as
    // "config.old.txt" or "data.oldbackup".
    private static readonly System.Text.RegularExpressions.Regex BackupSuffix =
      new System.Text.RegularExpressions.Regex(@"\.old[0-9]*$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// Delete leftover installer backups (<c>&lt;name&gt;.old</c> / <c>.oldN</c>, file or
    /// folder) under <paramref name="dir"/>. The plugin calls this on startup to sweep the
    /// prior update's moved-aside items once their old handles are gone (the bootstrap DLL,
    /// the swapped-aside libs/ folder, node_modules/*.node freed by an interrupted apply).
    /// Recursive by default. Returns how many were removed.
    /// </summary>
    public static int SweepOldBackups(string dir, bool recursive = true) {
      int removed = 0;
      try {
        if (!Directory.Exists(dir)) return 0;
        var opt = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        // One walk over files + folders; materialize first so deleting a matched folder
        // can't disturb the enumeration (a stale child entry is skipped by the Exists guards).
        foreach (var entry in Directory.EnumerateFileSystemEntries(dir, "*.old*", opt).ToList()) {
          if (!BackupSuffix.IsMatch(Path.GetFileName(entry))) continue;
          try {
            if (Directory.Exists(entry)) { Directory.Delete(entry, recursive: true); removed++; }
            else if (File.Exists(entry)) { File.Delete(entry); removed++; }
          } catch { /* still locked — try again next launch */ }
        }
      } catch { }
      return removed;
    }

    private static string StripLeading(string relPath, int stripDirs) {
      if (stripDirs <= 0) return relPath;
      var parts = relPath.Split('/');
      if (parts.Length <= stripDirs) return string.Empty;
      return string.Join("/", parts.Skip(stripDirs));
    }

    private static string MakeRelative(string root, string full) {
      string r = root.EndsWith(Path.DirectorySeparatorChar.ToString()) ? root : root + Path.DirectorySeparatorChar;
      return full.StartsWith(r, StringComparison.OrdinalIgnoreCase) ? full.Substring(r.Length) : full;
    }

    private static void CleanDir(string dir) {
      try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
    }

    // File-vs-directory primitives, picked by the move's isDir flag.
    private static bool Exists(string path, bool isDir) => isDir ? Directory.Exists(path) : File.Exists(path);

    private static void MoveAny(string from, string to, bool isDir) {
      if (isDir) Directory.Move(from, to); else File.Move(from, to);
    }

    private static void TryDeleteAny(string path, bool isDir) {
      try { if (isDir) { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } else if (File.Exists(path)) File.Delete(path); } catch { }
    }
  }
}
