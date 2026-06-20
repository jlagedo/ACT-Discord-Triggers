using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows.Forms;
using System.Windows.Forms.Integration;
using Advanced_Combat_Tracker;
using ACT_DiscordTriggers.Core.Ipc;

namespace ACT_DiscordTriggers {
  // The IActPluginV1 that ACT loads. Owns plugin-identity and lifecycle concerns
  // (assembly resolution, bridge discovery, diagnostics) and hosts the view. The
  // view is intentionally swappable: when it becomes WPF, only the "create + add
  // view" lines below change (Controls.Add(view) -> an ElementHost wrapper).
  public class DiscordTriggersPlugin : IActPluginV1 {
    private DiscordTriggersView view;
    private Label lblStatus;

    // NoInlining across the lifecycle methods keeps the JIT from hoisting a body that
    // touches a Costura-merged Core type into anything that could run before the resolver
    // is attached. The constructor below is what actually guarantees ordering; this is the
    // belt-and-suspenders that mirrors the Hojoring entry-plugin pattern.
    [MethodImpl(MethodImplOptions.NoInlining)]
    public DiscordTriggersPlugin() {
      // Force Costura's resolver to attach now (its module initializer runs on first
      // managed execution; calling this makes it deterministic) and register our own
      // sibling-DLL fallback, both before the view and its Core dependencies load.
      CosturaUtility.Initialize();
      AppDomain.CurrentDomain.AssemblyResolve += CurrentDomain_AssemblyResolve;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public void InitPlugin(TabPage pluginScreenSpace, Label pluginStatusText) {
      lblStatus = pluginStatusText;

      //Get plugin name (used to name the config file)
      string configName = "ACT_DiscordTriggers";
      try {
        string pluginName = ActGlobals.oFormActMain.PluginGetSelfData(this).pluginFile.FullName;
        pluginName = Path.GetFileNameWithoutExtension(pluginName).Trim();
        if (pluginName.Length > 0)
          configName = pluginName;
      } catch (Exception) { }

      //Locate the out-of-process Discord bridge so DiscordClient knows where to spawn it
      string bridgeDir = FindBridgeDir();
      DiscordClient.SetBridgePath(bridgeDir);

      //Always-on diagnostics: capture both plugin- and bridge-side logs into one
      //unified file the user can simply email. Initialised BEFORE the view loads
      //settings so the one-time config migration is captured (the log sink drops
      //anything written before Init).
      try {
        DiagnosticsLog.Init(ActGlobals.oFormActMain.AppDataFolder.FullName, bridgeDir, PluginVersion());
      } catch { }

      //Create the WPF view and host it inside ACT's WinForms TabPage via ElementHost
      //(which boots the WPF dispatcher on the existing UI thread — no WPF Application).
      view = new DiscordTriggersView();
      pluginScreenSpace.Text = "Discord Triggers";
      pluginScreenSpace.Controls.Add(new ElementHost {
        Child = view,
        Dock = DockStyle.Fill,
      });

      view.OnPluginInit(configName);

      lblStatus.Text = "Plugin Started";
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public async void DeInitPlugin() {
      if (view != null)
        await view.OnPluginDeInitAsync();
      // Flush + regenerate the unified diagnostics file one last time so it reflects
      // this whole session before ACT tears the plugin down.
      try { DiagnosticsLog.Shutdown(); } catch { }
      AppDomain.CurrentDomain.AssemblyResolve -= CurrentDomain_AssemblyResolve;
      if (lblStatus != null)
        lblStatus.Text = "Plugin Exited";
    }

    internal static string PluginVersion() {
      try { return Assembly.GetExecutingAssembly().GetName().Version.ToString(); }
      catch { return "?"; }
    }

    // Version label for the Information tab, bound via {x:Static} from the view's XAML
    // so the version stays out of the view's code-behind.
    public static string VersionDisplay => "v" + PluginVersion();

    private string FindBridgeDir() {
      // The bridge ships as node.exe + bundle.js + node_modules/ next to the
      // plugin DLL. We return the directory; BridgeProcess derives the two
      // file paths from it.
      try {
        var plugin = ActGlobals.oFormActMain.PluginGetSelfData(this);
        if (plugin != null) {
          string dir = plugin.pluginFile.DirectoryName;
          if (File.Exists(Path.Combine(dir, "node.exe")) && File.Exists(Path.Combine(dir, "bundle.js"))) {
            return dir;
          }
        }
      } catch { }
      return Path.Combine(ActGlobals.oFormActMain.AppDataFolder.FullName, "Plugins\\Discord");
    }

    private Assembly CurrentDomain_AssemblyResolve(object sender, ResolveEventArgs args) {
      try {
        var asm = new AssemblyName(args.Name);
        var plugin = ActGlobals.oFormActMain.PluginGetSelfData(this);
        string file;
        if (plugin != null) {
          file = plugin.pluginFile.DirectoryName;
          file = Path.Combine(file, asm.Name + ".dll");
          if (File.Exists(file)) {
            return Assembly.LoadFile(file);
          }
        }
        file = Path.Combine(ActGlobals.oFormActMain.AppDataFolder.FullName, "Plugins\\Discord");
        file = Path.Combine(file, asm.Name + ".dll");
        if (File.Exists(file)) {
          return Assembly.LoadFrom(file);
        }
      } catch (Exception ex) {
        ActGlobals.oFormActMain.WriteExceptionLog(ex, "Error with loading an assembly for Discord Plugin.");
      }
      return null;
    }
  }
}
