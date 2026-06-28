using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Windows.Forms.Integration;
using Advanced_Combat_Tracker;
using ACT_DiscordTriggers.Core.Ipc;
using ACT_DiscordTriggers.Core.Update;

namespace ACT_DiscordTriggers {
  // The real plugin implementation, byte-loaded from libs/ by the thin bootstrap
  // (DiscordTriggersPlugin). It owns everything the entry assembly used to: bridge
  // discovery, diagnostics, and hosting the WPF view inside ACT's WinForms TabPage via
  // an ElementHost. It is NOT an IActPluginV1 and is never reflection-scanned by ACT,
  // so it may freely reference WPF + the third-party deps shipped under libs/.
  //
  // The bootstrap forwards lifecycle calls here by reflection (InitPlugin /
  // DeInitPluginAsync), passing the plugin directory + config name it resolves from ACT.
  public class PluginImpl {
    private DiscordTriggersView view;
    private Label lblStatus;

    public void InitPlugin(TabPage pluginScreenSpace, Label pluginStatusText, string pluginDir, string configName) {
      lblStatus = pluginStatusText;

      // Sweep any *.old backups a prior auto-update moved aside (e.g. the bootstrap DLL,
      // only deletable now that the old process's handle is gone). Top-level + libs/ only,
      // so we never walk node_modules.
      try {
        UpdatePackageInstaller.SweepOldBackups(pluginDir, recursive: false);
        UpdatePackageInstaller.SweepOldBackups(Path.Combine(pluginDir, "libs"), recursive: false);
      } catch { }

      // Locate the out-of-process Discord bridge so DiscordClient knows where to spawn it.
      string bridgeDir = FindBridgeDir(pluginDir);
      DiscordClient.SetBridgePath(bridgeDir);

      // Always-on diagnostics: capture both plugin- and bridge-side logs into one
      // unified file. Initialised BEFORE the view loads settings so the one-time
      // config migration is captured (the log sink drops anything written before Init).
      try {
        DiagnosticsLog.Init(ActGlobals.oFormActMain.AppDataFolder.FullName, bridgeDir, AppInfo.PluginVersion());
      } catch { }

      // Create the WPF view and host it inside ACT's WinForms TabPage via ElementHost
      // (which boots the WPF dispatcher on the existing UI thread — no WPF Application).
      view = new DiscordTriggersView();
      pluginScreenSpace.Text = "Discord Triggers";
      pluginScreenSpace.Controls.Add(new ElementHost {
        Child = view,
        Dock = DockStyle.Fill,
      });

      view.OnPluginInit(configName, pluginDir);

      lblStatus.Text = "Plugin Started";
    }

    public async Task DeInitPluginAsync() {
      try {
        if (view != null)
          // ConfigureAwait(false): ACT calls DeInit on the UI thread and does not await
          // it, so it returns at the first suspension and ACT proceeds to destroy the
          // window handle. The teardown below is handle-independent, so resume off the
          // UI thread (see DiscordTriggersView.OnPluginDeInitAsync).
          await view.OnPluginDeInitAsync().ConfigureAwait(false);
      } catch (Exception ex) {
        // Dual-channel: our diagnostics file is primary, but it may not finish flushing
        // as the host tears the plugin down, so ACT's own exception log is the fallback.
        try { DiagnosticsLog.Append("DeInit error: " + ex, LogLevel.Error); DiagnosticsLog.Flush(); } catch { }
        try { ActGlobals.oFormActMain.WriteExceptionLog(ex, "Error de-initializing Discord plugin."); } catch { }
      } finally {
        // Flush + regenerate the unified diagnostics file one last time so it reflects
        // this whole session before ACT tears the plugin down.
        try { DiagnosticsLog.Shutdown(); } catch { }
        try { if (lblStatus != null) lblStatus.Text = "Plugin Exited"; } catch { }
      }
    }

    private static string FindBridgeDir(string pluginDir) {
      // The bridge ships as node.exe + bundle.js + node_modules/ next to the plugin
      // (alongside the bootstrap DLL); we return the directory and BridgeProcess derives
      // the file paths. Fall back to ACT's AppData plugin folder.
      try {
        if (!string.IsNullOrEmpty(pluginDir)
            && File.Exists(Path.Combine(pluginDir, "node.exe"))
            && File.Exists(Path.Combine(pluginDir, "bundle.js"))) {
          return pluginDir;
        }
      } catch { }
      return Path.Combine(ActGlobals.oFormActMain.AppDataFolder.FullName, "Plugins\\Discord");
    }
  }
}
