using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
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
      // Guard the restore prologue: during ACT shutdown oFormActMain may already be
      // disposed, so these assignments can throw. An unguarded throw here would skip
      // the bridge teardown below (orphaning node.exe) and escape the async-void
      // DeInitPlugin as a crash dialog.
      try {
        ActGlobals.oFormActMain.PlayTtsMethod = oldTTS;
        ActGlobals.oFormActMain.PlaySoundMethod = oldSound;
      } catch (Exception ex) {
        DiagnosticsLog.Append("Error restoring ACT delegates on exit: " + ex);
        try { ActGlobals.oFormActMain.WriteExceptionLog(ex, "Error restoring ACT delegates on Discord plugin exit."); } catch { }
      }
      // Detach the static DiscordClient.BotReady/Log subscriptions synchronously, before
      // the async bridge shutdown below, so a deferred continuation can't leave a stale
      // handler bound to a disposed view across a plugin reload.
      try { discordService?.Dispose(); } catch (Exception ex) { DiagnosticsLog.Append("Error disposing Discord service on exit: " + ex); }
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
          // ConfigureAwait(false): see DeInitPlugin / ShutdownAsync — the host window
          // handle may be torn down by the time the bridge finishes, so the
          // continuation must not marshal back to the (dead) WinForms UI context.
          await vm.ShutdownAsync().ConfigureAwait(false);
        } catch (Exception ex) {
          ActGlobals.oFormActMain.WriteExceptionLog(ex, "Error with DeInit of Discord Plugin.");
        }
      }
    }
    #endregion

    #region ONNX voice picker (grouped, type-to-filter flyout)
    // The catalog is too long for a plain dropdown, so the picker is a flyout with a
    // search box over a locale-grouped list. The CollectionViewSource (XAML) does the
    // grouping; these handlers do the live filter + selection. Kept in the view (not the
    // Core VM) so Core stays free of any WPF/CollectionView dependency.

    // Context-aware match: every whitespace token must appear in locale/name/tier, so
    // "pt faber", "en high", or "amy" all narrow the list.
    private void OnnxVoiceFilter(object sender, FilterEventArgs e) {
      var query = VoiceSearchBox?.Text?.Trim();
      if (string.IsNullOrEmpty(query)) { e.Accepted = true; return; }
      var item = e.Item as OnnxVoiceItem;
      if (item == null) { e.Accepted = false; return; }
      var hay = (item.Locale + " " + item.Name + " " + item.Tier).ToLowerInvariant();
      foreach (var token in query.ToLowerInvariant().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)) {
        if (!hay.Contains(token)) { e.Accepted = false; return; }
      }
      e.Accepted = true;
    }

    private void OnVoiceSearchChanged(object sender, TextChangedEventArgs e) =>
      (FindResource("OnnxVoicesView") as CollectionViewSource)?.View?.Refresh();

    // Commit the pick and close the flyout. SelectedItem isn't bound (filtering would
    // null it out as items leave the view), so the selection is pushed here instead.
    private void OnVoiceListSelectionChanged(object sender, SelectionChangedEventArgs e) {
      if (e.AddedItems.Count == 0) return;
      var item = e.AddedItems[0] as OnnxVoiceItem;
      if (item != null && vm != null) {
        vm.SelectedOnnxVoice = item;
        VoicePickerButton.IsChecked = false;
      }
    }

    // Each open starts with a cleared search and a clean list selection so the next
    // click always raises SelectionChanged; focus the box so the user can type at once.
    private void OnVoicePickerOpened(object sender, RoutedEventArgs e) {
      VoiceSearchBox.Text = "";
      VoiceList.SelectedIndex = -1;
      VoiceSearchBox.Focus();
    }

    // Folder picking is a view concern (Core has no WinForms): open a WinForms folder
    // dialog parented to ACT's main window, seeded with the current path, and write the
    // chosen folder back to the VM.
    private void OnBrowseModelsFolder(object sender, RoutedEventArgs e) {
      using (var dlg = new System.Windows.Forms.FolderBrowserDialog()) {
        dlg.Description = "Choose where downloaded voices are stored";
        dlg.ShowNewFolderButton = true;
        var current = vm?.ModelsDir;
        if (!string.IsNullOrWhiteSpace(current) && Directory.Exists(current))
          dlg.SelectedPath = current;
        if (dlg.ShowDialog(ActGlobals.oFormActMain) == System.Windows.Forms.DialogResult.OK && vm != null)
          vm.ModelsDir = dlg.SelectedPath;
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
