using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using Advanced_Combat_Tracker;

namespace ACT_DiscordTriggers {
  // The thin bootstrap ACT loads (the only assembly ACT LoadFroms and GetTypes()-scans).
  // It defines exactly one type that touches a non-GAC base — itself, an IActPluginV1 —
  // so the reflection scan is trivially valid. It owns no plugin logic: it installs an
  // AssemblyResolver that byte-loads the real closure from libs/ (kept unlocked so the
  // auto-updater can overwrite it), then forwards lifecycle to PluginImpl in the
  // byte-loaded Main assembly.
  //
  // NoInlining on the lifecycle methods keeps the JIT from hoisting a body that touches a
  // libs/-resolved type into a frame that runs before the resolver is hooked.
  public class DiscordTriggersPlugin : IActPluginV1 {
    private const string MainAssemblyName = "ACT_DiscordTriggers.Main";
    private const string PluginImplTypeName = "ACT_DiscordTriggers.PluginImpl";

    private AssemblyResolver resolver;
    private object impl;
    private MethodInfo deinitMethod;
    private Label lblStatus;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public void InitPlugin(TabPage pluginScreenSpace, Label pluginStatusText) {
      lblStatus = pluginStatusText;

      string pluginDir = ResolvePluginDir();
      string appDataPluginDir = Path.Combine(ActGlobals.oFormActMain.AppDataFolder.FullName, "Plugins\\Discord");
      string configName = ResolveConfigName();

      // Install the byte-loading resolver BEFORE touching any libs/ type, then bring the
      // Main assembly (and, on demand, its closure) up through it.
      resolver = new AssemblyResolver(pluginDir, appDataPluginDir);
      resolver.Hook();

      var mainAsm = resolver.Load(MainAssemblyName);
      var implType = mainAsm.GetType(PluginImplTypeName, throwOnError: true);
      impl = Activator.CreateInstance(implType);
      deinitMethod = implType.GetMethod("DeInitPluginAsync");

      try {
        implType.GetMethod("InitPlugin")
          .Invoke(impl, new object[] { pluginScreenSpace, pluginStatusText, pluginDir, configName });
      } catch (TargetInvocationException tie) when (tie.InnerException != null) {
        // Surface the real failure to ACT, not the reflection wrapper.
        throw tie.InnerException;
      }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public async void DeInitPlugin() {
      // async void: ACT can't await this. Capture everything so an escaping exception
      // doesn't surface as an unreadable crash dialog while ACT is already exiting.
      try {
        if (impl != null && deinitMethod != null) {
          var task = deinitMethod.Invoke(impl, null) as Task;
          if (task != null) await task.ConfigureAwait(false);
        }
      } catch (Exception ex) {
        try { ActGlobals.oFormActMain.WriteExceptionLog(ex, "Error de-initializing Discord plugin (bootstrap)."); } catch { }
      } finally {
        try { resolver?.Unhook(); } catch { }
        try { if (lblStatus != null) lblStatus.Text = "Plugin Exited"; } catch { }
      }
    }

    // The plugin's install directory (where the bootstrap DLL + libs/ + node.exe live),
    // resolved from ACT's plugin list. Falls back to ACT's AppData plugin folder.
    private string ResolvePluginDir() {
      try {
        var plugin = ActGlobals.oFormActMain.PluginGetSelfData(this);
        if (plugin != null) {
          string dir = plugin.pluginFile.DirectoryName;
          if (!string.IsNullOrEmpty(dir)) return dir;
        }
      } catch { }
      return Path.Combine(ActGlobals.oFormActMain.AppDataFolder.FullName, "Plugins\\Discord");
    }

    // The plugin file's base name, used to name the config file.
    private string ResolveConfigName() {
      try {
        string pluginName = ActGlobals.oFormActMain.PluginGetSelfData(this).pluginFile.FullName;
        pluginName = Path.GetFileNameWithoutExtension(pluginName).Trim();
        if (pluginName.Length > 0) return pluginName;
      } catch { }
      return "ACT_DiscordTriggers";
    }
  }
}
