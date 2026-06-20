using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Controls;
using Advanced_Combat_Tracker;
using ACT_DiscordTriggers.Core.Ipc;
using ACT_DiscordTriggers.Core.Settings;
using ACT_DiscordTriggers.Core.ViewModels;

namespace ACT_DiscordTriggers {
  // WPF view hosted inside ACT's WinForms TabPage via ElementHost (see DiscordTriggersPlugin).
  // Derives from the GAC WPF UserControl, so it's load-invariant-safe — it never derives from
  // a Costura-merged type and only references the Core ViewModel through DataContext. All
  // state/commands live in DiscordTriggersViewModel; the code-behind is lifecycle/ACT glue only.
  public partial class DiscordTriggersView : UserControl {
    private FormActMain.PlayTtsDelegate oldTTS;
    private FormActMain.PlaySoundDelegate oldSound;
    private DiscordTriggersViewModel vm;
    private DiscordClientService discordService;

    public DiscordTriggersView() {
      InitializeComponent();
    }

    #region Plugin lifecycle (driven by the DiscordTriggersPlugin host)
    // Called by the host after it has set the bridge path and initialised the diagnostics
    // log. View-level init only: ACT delegates, settings, bot wiring. Constructing the VM
    // here (on the UI thread, after ElementHost has installed the WPF dispatcher) lets it
    // capture the DispatcherSynchronizationContext used to marshal background callbacks.
    public void OnPluginInit(string configName) {
      // ACT delegates (restored on leave / deinit); save before any hook swap.
      oldTTS = ActGlobals.oFormActMain.PlayTtsMethod;
      oldSound = ActGlobals.oFormActMain.PlaySoundMethod;

      discordService = new DiscordClientService();
      string configDir = Path.Combine(ActGlobals.oFormActMain.AppDataFolder.FullName, "Config");
      var store = new SettingsStore(configDir, $"{configName}.config.xml", msg => vm?.Log(msg));
      vm = new DiscordTriggersViewModel(discordService, store);
      DataContext = vm;

      // The VM stays ACT-free: it raises these so the view swaps ACT's TTS/sound delegates
      // to route through Discord while joined.
      vm.JoinedChannel += OnJoinedChannel;
      vm.LeftChannel += OnLeftChannel;

      vm.Log("Diagnostics log: " + DiagnosticsLog.UnifiedPath);
      vm.Initialize();
    }

    public async Task OnPluginDeInitAsync() {
      ActGlobals.oFormActMain.PlayTtsMethod = oldTTS;
      ActGlobals.oFormActMain.PlaySoundMethod = oldSound;
      // Detach the static DiscordClient.BotReady/Log subscriptions synchronously, before
      // the async bridge shutdown below, so a deferred continuation can't leave a stale
      // handler bound to a disposed view across a plugin reload.
      discordService?.Dispose();
      if (vm != null) {
        vm.JoinedChannel -= OnJoinedChannel;
        vm.LeftChannel -= OnLeftChannel;
        // Guard each teardown step independently: a save failure (e.g. a locked settings
        // file) must not skip the bridge shutdown and orphan node.exe.
        try {
          vm.Save();
        } catch (Exception ex) {
          ActGlobals.oFormActMain.WriteExceptionLog(ex, "Error saving Discord plugin settings on exit.");
        }
        try {
          await vm.ShutdownAsync();
        } catch (Exception ex) {
          ActGlobals.oFormActMain.WriteExceptionLog(ex, "Error with DeInit of Discord Plugin.");
        }
      }
    }
    #endregion

    private void OnJoinedChannel() {
      ActGlobals.oFormActMain.PlayTtsMethod = vm.SpeakText;
      ActGlobals.oFormActMain.PlaySoundMethod = vm.SpeakSoundFile;
    }

    private void OnLeftChannel() {
      ActGlobals.oFormActMain.PlayTtsMethod = oldTTS;
      ActGlobals.oFormActMain.PlaySoundMethod = oldSound;
    }
  }
}
