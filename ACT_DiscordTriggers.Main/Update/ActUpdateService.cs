using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Advanced_Combat_Tracker;
using ACT_DiscordTriggers.Core.Ipc;
using ACT_DiscordTriggers.Core.Update;

namespace ACT_DiscordTriggers {
  // The production IUpdateService: the ACT/WinForms half of the updater. It resolves the
  // running version, checks GitHub (Core's GithubReleaseClient), and on apply shows a
  // confirm dialog, downloads, stops the bridge, swaps files (Core's UpdatePackageInstaller),
  // and restarts ACT. Lives in Main (byte-loaded) so it may use ACT + WinForms freely.
  //
  // Test seams (env): ACT_DT_UPDATE_FEED (base URL, read by GithubReleaseClient),
  // ACT_DT_UPDATE_FAKE_CURRENT (pretend an older running version), ACT_DT_UPDATE_DRYRUN
  // (swap into place is planned + logged but skipped, and ACT is not restarted).
  public sealed class ActUpdateService : IUpdateService {
    private const string FakeCurrentEnv = "ACT_DT_UPDATE_FAKE_CURRENT";
    private const string DryRunEnv = "ACT_DT_UPDATE_DRYRUN";

    private readonly string pluginDir;
    private readonly Func<Task> stopBridgeAsync;
    private readonly Action<string, LogLevel> log;

    public ActUpdateService(string pluginDir, Func<Task> stopBridgeAsync, Action<string, LogLevel> log = null) {
      this.pluginDir = pluginDir;
      this.stopBridgeAsync = stopBridgeAsync;
      this.log = log ?? ((_, __) => { });
    }

    public async Task<UpdateInfo> CheckAsync(CancellationToken ct = default) {
      var current = ResolveCurrentVersion();
      using (var client = new GithubReleaseClient()) {
        var info = await client.GetLatestAsync(current, ct).ConfigureAwait(false);
        log($"Update check: running {current}, latest {info.TagName} ({(info.IsNewer ? "newer" : "up to date")}).", LogLevel.Info);
        return info;
      }
    }

    public async Task<bool> ApplyAsync(UpdateInfo info, IProgress<string> status = null, CancellationToken ct = default) {
      if (info == null || string.IsNullOrEmpty(info.DownloadUrl)) {
        Report(status, "No downloadable release asset; opening the release page instead.", LogLevel.Warn);
        OpenUrl(info?.HtmlUrl);
        return false;
      }

      // Dev guard: never clobber a working tree (mirrors OverlayPlugin's .git check).
      if (HasDotGitNearby()) {
        Report(status, "Skipping update: a .git working tree sits next to the plugin (developer install).", LogLevel.Warn);
        return false;
      }

      if (!Confirm(info)) {
        log("Update declined by user.", LogLevel.Info);
        return false;
      }

      bool dryRun = IsTrue(DryRunEnv);
      string work = Path.Combine(Path.GetTempPath(), "ACT_DiscordTriggers.update");
      string zip = Path.Combine(work, "release.zip");

      try {
        Report(status, $"Downloading {info.TagName}…", LogLevel.Info);
        using (var client = new GithubReleaseClient()) {
          var progress = new Progress<double>(p => status?.Report($"Downloading {info.TagName}… {p:P0}"));
          await client.DownloadAsync(info.DownloadUrl, zip, progress, ct).ConfigureAwait(false);
        }

        // Only stop the bridge for a real apply — it unlocks node.exe + node_modules/*.node
        // for the swap. A dry run writes nothing, so killing the bridge would just leave
        // audio output dead for a "nothing changed" preview.
        if (!dryRun) {
          Report(status, "Stopping the bridge…", LogLevel.Info);
          try { if (stopBridgeAsync != null) await stopBridgeAsync().ConfigureAwait(false); }
          catch (Exception ex) { log("Bridge stop before update failed (continuing): " + ex.Message, LogLevel.Warn); }
        }

        Report(status, dryRun ? "Applying update (DRY RUN)…" : "Applying update…", LogLevel.Info);
        var installer = new UpdatePackageInstaller(m => log(m, LogLevel.Info));
        bool ok = installer.Install(zip, pluginDir, new UpdatePackageInstaller.Options { StripDirs = 1, DryRun = dryRun }, ct: ct);
        if (!ok) {
          Report(status, "Update failed; your installation is unchanged.", LogLevel.Error);
          // On a real apply we already stopped the bridge, so audio stays down until ACT
          // restarts even though the files rolled back; ask the user to restart.
          ShowInfo(dryRun
            ? "The update could not be applied (dry run). See the diagnostics log for details."
            : "The update could not be applied. Your current version is unchanged. Please restart ACT to restore audio output. See the diagnostics log for details.");
          return false;
        }

        if (dryRun) {
          Report(status, "Dry run complete — no files changed, ACT not restarted.", LogLevel.Info);
          return true;
        }

        Report(status, "Update staged. Restarting ACT…", LogLevel.Info);
        if (!TryRestartAct($"ACT_DiscordTriggers is updating to {info.TagName}."))
          ShowInfo($"Update to {info.TagName} installed. Please restart ACT to finish.");
        return true;
      } catch (Exception ex) {
        Report(status, "Update error: " + ex.Message, LogLevel.Error);
        log("Update error: " + ex, LogLevel.Error);
        return false;
      } finally {
        try { if (Directory.Exists(work)) Directory.Delete(work, true); } catch { }
      }
    }

    private Version ResolveCurrentVersion() {
      try {
        var env = Environment.GetEnvironmentVariable(FakeCurrentEnv);
        if (!string.IsNullOrWhiteSpace(env) && Version.TryParse(env, out var v)) {
          log($"Using ACT_DT_UPDATE_FAKE_CURRENT={v} as the running version.", LogLevel.Warn);
          return v;
        }
      } catch { }
      return typeof(AppInfo).Assembly.GetName().Version ?? new Version(0, 0, 0);
    }

    private bool HasDotGitNearby() {
      try {
        // e.g. <repo>/release/ACT_DiscordTriggers/  → check a couple levels up for .git.
        var dir = new DirectoryInfo(pluginDir);
        for (int i = 0; i < 4 && dir != null; i++, dir = dir.Parent) {
          if (Directory.Exists(Path.Combine(dir.FullName, ".git"))) return true;
        }
      } catch { }
      return false;
    }

    private static bool IsTrue(string envVar) {
      try {
        var v = Environment.GetEnvironmentVariable(envVar);
        return !string.IsNullOrEmpty(v) && (v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase));
      } catch { return false; }
    }

    private void Report(IProgress<string> status, string msg, LogLevel level) {
      status?.Report(msg);
      log(msg, level);
    }

    // --- ACT/WinForms interop ------------------------------------------------------------

    private bool Confirm(UpdateInfo info) {
      var current = ResolveCurrentVersion();
      bool result = false;
      OnUi(() => result = UpdatePromptForm.Ask(info, current));
      return result;
    }

    private void ShowInfo(string message) {
      OnUi(() => MessageBox.Show(message, "ACT Discord Triggers", MessageBoxButtons.OK, MessageBoxIcon.Information));
    }

    private static bool TryRestartAct(string message) {
      try {
        var form = ActGlobals.oFormActMain;
        var method = form.GetType().GetMethod("RestartACT");
        if (method == null) return false;
        // (bool showRestartIgnore, string message)
        form.Invoke((Action)(() => method.Invoke(form, new object[] { true, message })));
        return true;
      } catch { return false; }
    }

    private static void OpenUrl(string url) {
      if (string.IsNullOrEmpty(url)) return;
      try { System.Diagnostics.Process.Start(url); } catch { }
    }

    private static void OnUi(Action action) {
      try {
        var form = ActGlobals.oFormActMain;
        if (form != null && form.InvokeRequired) form.Invoke(action);
        else action();
      } catch { action(); }
    }
  }
}
