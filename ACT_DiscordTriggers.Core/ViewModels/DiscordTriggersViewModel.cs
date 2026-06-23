using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ACT_DiscordTriggers.Core.Ipc;
using ACT_DiscordTriggers.Core.Settings;
using ACT_DiscordTriggers.Core.Tts;

namespace ACT_DiscordTriggers.Core.ViewModels {
  // UI-agnostic ViewModel: no WinForms, no ACT. Holds the settings-backed state,
  // commands, and Discord wiring the WinForms view used to own; the eventual WPF
  // view binds to the same instance. Constructed on the UI thread so it can capture
  // the SynchronizationContext used to marshal background callbacks.
  public partial class DiscordTriggersViewModel : ObservableObject {
    private readonly IDiscordService discord;
    private readonly SettingsStore store;
    private readonly SynchronizationContext sync;

    // Bot status rides in the config object and the textbox fires per keystroke,
    // hence a long debounce; fx/normalize/quality coalesce a slider-drag burst.
    private const int StatusDebounceMs = 10000;
    private const int ConfigDebounceMs = 400;
    private CancellationTokenSource statusPushCts;
    private CancellationTokenSource configPushCts;

    // Suppresses config/status pushes while loading settings into the properties.
    private bool suppressPush;

    public ObservableCollection<string> Servers { get; } = new ObservableCollection<string>();
    public ObservableCollection<string> Channels { get; } = new ObservableCollection<string>();
    public ObservableCollection<string> Voices { get; } = new ObservableCollection<string>();
    public ObservableCollection<OnnxVoiceItem> OnnxVoices { get; } = new ObservableCollection<OnnxVoiceItem>();
    public ObservableCollection<LogEntry> LogEntries { get; } = new ObservableCollection<LogEntry>();

    // Raised when audio output becomes active/inactive so the view can swap ACT's
    // PlayTtsMethod/PlaySoundMethod delegates (kept out of the VM to stay ACT-free).
    // "Active" = a joined Discord channel (bot mode) OR a running local device
    // (local mode), so the same delegate swap serves both output targets.
    public event Action OutputActivated;
    public event Action OutputDeactivated;

    public DiscordTriggersViewModel(IDiscordService discord, SettingsStore store) {
      this.discord = discord;
      this.store = store;
      this.sync = SynchronizationContext.Current;
      this.discord.BotReady += OnBotReady;
      this.discord.Log += OnLog;
      this.discord.Disconnected += OnDisconnected;
    }

    // --- Output mode ------------------------------------------------------------
    // "bot" streams audio to a Discord voice channel; "local" plays it on this PC's
    // default sound device (no token/channel/login). Paired bools let the Main-tab
    // choice-cards two-way bind without a converter. Changing the mode at runtime
    // tears down the active path and, for local, brings the bridge up immediately.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBotMode))]
    [NotifyPropertyChangedFor(nameof(IsLocalMode))]
    [NotifyPropertyChangedFor(nameof(CanConnect))]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    private string outputMode = "bot";   // "bot" | "local"
    // Load sets the mode under suppressPush; Initialize does the initial bring-up.
    partial void OnOutputModeChanged(string value) {
      if (!suppressPush) _ = ApplyOutputModeAsync();
    }
    public bool IsBotMode { get => OutputMode == "bot"; set { if (value) OutputMode = "bot"; } }
    public bool IsLocalMode { get => OutputMode == "local"; set { if (value) OutputMode = "local"; } }

    // Unified "audio output is live" flag — true once a Discord channel is joined or
    // the local device is running. Drives the Test gate and is flipped only through
    // ActivateOutput/DeactivateOutput so the ACT delegate swap fires once per change.
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestCommand))]
    private bool outputActive;

    // --- Settings-backed properties ---------------------------------------------
    // Source-generated (no side effects): the value lives in the generated field and
    // is mapped to/from PluginSettings at the load/save boundary.
    [ObservableProperty] private string botToken = "";
    [ObservableProperty] private bool autoConnect;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestCommand))]
    private string ttsVoice = "";

    // Source-generated, side effect lives in the partial On…Changed hook.
    [ObservableProperty] private string botStatus = "Playing with ACT Triggers";
    partial void OnBotStatusChanged(string value) => ScheduleStatusPush();

    // Manual where a clamp applies (the source generator writes the field before its
    // hook, so it can't enforce a clamp). SetClamped centralizes clamp + notify +
    // dependent-label + debounced push. TTS volume/speed are synthesized in-process,
    // so the bridge ignores them (push: false); the bridge-interpreted tunables push.
    private int ttsVolume = 10;
    public int TtsVolume {
      get => ttsVolume;
      set => SetClamped(ref ttsVolume, value, PluginSettings.TtsVolumeMin, PluginSettings.TtsVolumeMax, push: false);
    }

    private int ttsSpeed = 10;
    public int TtsSpeed {
      get => ttsSpeed;
      // Speed pushes for both engines (the bridge applies it for ONNX, ignores it for SAPI).
      set => SetClamped(ref ttsSpeed, value, PluginSettings.TtsSpeedMin, PluginSettings.TtsSpeedMax, push: true);
    }

    [ObservableProperty] private bool randomFx;
    partial void OnRandomFxChanged(bool value) => ScheduleConfigPush();

    private int fxChance = 25;
    public int FxChance {
      get => fxChance;
      set => SetClamped(ref fxChance, value, PluginSettings.FxChanceMin, PluginSettings.FxChanceMax, push: true, dependentLabel: nameof(FxChanceLabel));
    }

    [ObservableProperty] private bool normalize = true;
    partial void OnNormalizeChanged(bool value) => ScheduleConfigPush();

    private int normalizeTarget = PluginSettings.NormalizeTargetDefault;
    public int NormalizeTarget {
      get => normalizeTarget;
      set {
        if (SetClamped(ref normalizeTarget, value, PluginSettings.NormalizeTargetMin, PluginSettings.NormalizeTargetMax, push: true, dependentLabel: nameof(NormalizeTargetLabel)))
          OnPropertyChanged(nameof(IsNormalizeTargetCustom));
      }
    }

    // True when the target is off its recommended value — drives the "Recommended" reset chip's enabled state.
    public bool IsNormalizeTargetCustom => NormalizeTarget != PluginSettings.NormalizeTargetDefault;

    [RelayCommand]
    private void ResetNormalizeTarget() => NormalizeTarget = PluginSettings.NormalizeTargetDefault;

    private int audioQualityIndex = 1;
    public int AudioQualityIndex {
      get => audioQualityIndex;
      set => SetClamped(ref audioQualityIndex, value, PluginSettings.AudioQualityIndexMin, PluginSettings.AudioQualityIndexMax, push: true, dependentLabel: nameof(ShowHighQualityWarning));
    }

    // Master bus limiter (independent of Normalize). Enable toggle + ceiling tier.
    [ObservableProperty] private bool limiterEnabled = true;
    partial void OnLimiterEnabledChanged(bool value) => ScheduleConfigPush();

    private int limiterCeilingIndex = 1; // -1 dBTP
    public int LimiterCeilingIndex {
      get => limiterCeilingIndex;
      set => SetClamped(ref limiterCeilingIndex, value, PluginSettings.LimiterCeilingIndexMin, PluginSettings.LimiterCeilingIndexMax, push: true);
    }

    // Local-mode playback volume (0..100 %). Only meaningful when OutputMode is
    // "local"; the bridge applies it to the local-output mixer (bot mode ignores it).
    private int localOutputVolume = PluginSettings.LocalOutputVolumeDefault;
    public int LocalOutputVolume {
      get => localOutputVolume;
      set => SetClamped(ref localOutputVolume, value, PluginSettings.LocalOutputVolumeMin, PluginSettings.LocalOutputVolumeMax, push: true, dependentLabel: nameof(LocalOutputVolumeLabel));
    }

    // --- ONNX TTS (persisted in PluginSettings; bridge-relevant fields push) -----
    // Engine and Quality are single-value choices exposed as paired bools so the
    // choice-cards / segmented RadioButtons can two-way bind without a converter:
    // checking one sets the backing string, which re-raises both bools.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOnnx))]
    [NotifyPropertyChangedFor(nameof(IsSapi))]
    [NotifyPropertyChangedFor(nameof(ShowDownloadPrompt))]
    [NotifyPropertyChangedFor(nameof(ShowDownloadStrip))]
    [NotifyPropertyChangedFor(nameof(DownloadButtonVisible))]
    [NotifyCanExecuteChangedFor(nameof(TestCommand))]
    private string engine = "sapi";   // "sapi" | "onnx"
    partial void OnEngineChanged(string value) => ScheduleConfigPush();
    public bool IsOnnx { get => Engine == "onnx"; set { if (value) Engine = "onnx"; } }
    public bool IsSapi { get => Engine == "sapi"; set { if (value) Engine = "sapi"; } }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPiper))]
    [NotifyPropertyChangedFor(nameof(IsKokoro))]
    [NotifyPropertyChangedFor(nameof(QualityDescription))]
    private string onnxFamily = "piper";   // "piper" | "kokoro" (the Quality toggle)
    partial void OnOnnxFamilyChanged(string value) {
      RebuildOnnxVoices(value);   // re-selects, which refreshes the download row
      ScheduleConfigPush();
      // Load sets the family under suppressPush; Initialize logs the scan once instead.
      if (!suppressPush) Log("Quality set to " + value + " — " + InstallCountText() + ".");
    }
    public bool IsPiper { get => OnnxFamily == "piper"; set { if (value) OnnxFamily = "piper"; } }
    public bool IsKokoro { get => OnnxFamily == "kokoro"; set { if (value) OnnxFamily = "kokoro"; } }
    public string QualityDescription => IsKokoro
      ? "Kokoro — most realistic; one 333 MB pack, heavier on CPU."
      : "Piper — light on CPU, ~150 ms per callout.";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestCommand))]
    [NotifyPropertyChangedFor(nameof(SelectedVoiceInstalled))]
    [NotifyPropertyChangedFor(nameof(ShowDownloadPrompt))]
    [NotifyPropertyChangedFor(nameof(ShowDownloadStrip))]
    [NotifyPropertyChangedFor(nameof(DownloadButtonVisible))]
    [NotifyPropertyChangedFor(nameof(DownloadNoticeText))]
    [NotifyPropertyChangedFor(nameof(DownloadButtonText))]
    [NotifyPropertyChangedFor(nameof(DownloadStatusText))]
    private OnnxVoiceItem selectedOnnxVoice;
    partial void OnSelectedOnnxVoiceChanged(OnnxVoiceItem value) {
      DownloadJustCompleted = false;   // a fresh pick clears the prior "ready" confirmation
      ScheduleConfigPush();            // the picked voice (OnnxVoice id) rides in SetConfig
    }
    public bool SelectedVoiceInstalled => SelectedOnnxVoice?.Installed == true;
    // The strip shows for an ONNX voice that isn't on disk yet (the needs-download button
    // when idle, the progress bar while downloading) and lingers briefly on a success
    // confirmation after a download so completion isn't just an abrupt disappearance.
    public bool ShowDownloadPrompt => IsOnnx && SelectedOnnxVoice != null && !SelectedOnnxVoice.Installed;
    public bool ShowDownloadStrip => ShowDownloadPrompt || DownloadJustCompleted;
    public bool DownloadButtonVisible => ShowDownloadPrompt && !IsDownloading;
    public string DownloadNoticeText => IsKokoro
      ? "One 333 MB pack unlocks every Kokoro voice."
      : "This voice isn't downloaded yet.";
    public string DownloadButtonText =>
      "Download · " + (IsKokoro ? 333 : SelectedOnnxVoice?.Info.SizeMB ?? 0) + " MB";

    // The persisted OnnxVoice id, stashed by FromSettings and applied by Initialize
    // once OnnxVoices has been built (SelectedOnnxVoice is an item, not an id).
    private string loadedOnnxVoiceId = "";

    // Cancels the in-flight voice download (on re-entry guard or shutdown).
    private CancellationTokenSource downloadCts;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DownloadButtonVisible))]
    [NotifyPropertyChangedFor(nameof(ShowDownloadStrip))]
    private bool isDownloading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DownloadStatusText))]
    private double downloadProgress;

    public string DownloadStatusText =>
      "Downloading " + (SelectedOnnxVoice?.Name ?? "voice") + "… " + (int)DownloadProgress + "%";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowDownloadStrip))]
    private bool downloadJustCompleted;

    [ObservableProperty] private string downloadDoneText = "";

    // CPU threads (Advanced) — three discrete choices, same paired-bool pattern.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsThreads1))]
    [NotifyPropertyChangedFor(nameof(IsThreads2))]
    [NotifyPropertyChangedFor(nameof(IsThreads4))]
    private int ttsThreads = 4;
    partial void OnTtsThreadsChanged(int value) => ScheduleConfigPush();
    public bool IsThreads1 { get => TtsThreads == 1; set { if (value) TtsThreads = 1; } }
    public bool IsThreads2 { get => TtsThreads == 2; set { if (value) TtsThreads = 2; } }
    public bool IsThreads4 { get => TtsThreads == 4; set { if (value) TtsThreads = 4; } }

    [ObservableProperty] private string modelsDir = "";
    partial void OnModelsDirChanged(string value) {
      ScheduleConfigPush();
      // Re-scan the new folder so the ✓/size marks and download strip reflect what's
      // installed there. Skipped during load (suppressPush), where Initialize rebuilds.
      if (suppressPush) return;
      RefreshInstallState();
      Log("Models folder changed to " + OnnxCatalog.ResolveModelsDir(value) + " — " + InstallCountText() + ".");
    }
    [ObservableProperty] private bool isAdvancedExpanded;

    // --- Computed (presentation) ------------------------------------------------
    public string FxChanceLabel => "FX Chance: " + FxChance + "%";
    public string NormalizeTargetLabel => "Auto-level Target: -" + NormalizeTarget + " LUFS";
    public string LocalOutputVolumeLabel => "Local playback volume: " + LocalOutputVolume + "%";
    // The High tier may exceed an unboosted channel's 96 kbps cap; the view shows a warning.
    public bool ShowHighQualityWarning => AudioQualityIndex == PluginSettings.AudioQualityIndexMax;

    // --- Command-enable state ---------------------------------------------------
    // True once the bot has signalled BotReady; flipped back on disconnect (explicit
    // or a dropped bridge). Drives the Connect/Disconnect button swap and gates which
    // of the two commands can run.
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    [NotifyCanExecuteChangedFor(nameof(DisconnectCommand))]
    [NotifyPropertyChangedFor(nameof(CanConnect))]
    private bool isConnected;

    // CanExecute can't express negation directly, so expose the inverse for the
    // Connect command (and any view that wants it). Only meaningful in bot mode —
    // local output has no Discord connection to make.
    public bool CanConnect => !IsConnected && IsBotMode;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(JoinCommand))]
    private bool canJoin;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LeaveCommand))]
    [NotifyCanExecuteChangedFor(nameof(TestCommand))]
    private bool canLeave;

    // --- Selection --------------------------------------------------------------
    [ObservableProperty] private string selectedServer;
    [ObservableProperty] private string selectedChannel;

    // Repopulating Servers transiently sets this to null; bail before NRE-ing.
    partial void OnSelectedServerChanged(string value) {
      if (!string.IsNullOrEmpty(value)) _ = PopulateChannelsAsync(value);
    }

    // --- Commands ---------------------------------------------------------------
    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectAsync() {
      try {
        if (await discord.IsConnectedAsync()) {
          Log("Already connected to Discord.");
          return;
        }
        Log("Connecting to Discord...");
        await discord.ConnectAsync(ToSettings());
      } catch (Exception ex) {
        Log("Connect failed", ex);
      }
    }

    [RelayCommand(CanExecute = nameof(IsConnected))]
    private async Task DisconnectAsync() {
      Log("Disconnecting from Discord...");
      // Drop the UI to a disconnected state up front so the button swaps back and the
      // stale server/channel lists clear immediately, regardless of teardown timing.
      ResetConnectionState();
      try {
        await discord.DeinitAsync();
      } catch (Exception ex) {
        Log("Disconnect error", ex);
      }
    }

    // Revert all connection-derived UI state. Shared by the explicit Disconnect command
    // and the unsolicited Disconnected event (dropped bridge). Marshalled to the UI
    // thread: the event path arrives on a background thread, and clearing the
    // ObservableCollections / restoring ACT delegates must happen on the UI thread.
    private void ResetConnectionState() {
      OnUi(() => {
        IsConnected = false;
        CanJoin = false;
        CanLeave = false;
        Servers.Clear();
        Channels.Clear();
        SelectedServer = null;
        SelectedChannel = null;
        // If output was live (joined channel or local device), hand ACT's TTS/sound
        // delegates back. Idempotent, so a double reset is harmless.
        DeactivateOutput();
      });
    }

    // Flip the unified output-active flag and raise the matching event exactly once
    // per transition, so the view swaps ACT's delegates a single time. Used by both
    // the bot-join path and the local-output path.
    private void ActivateOutput() {
      if (OutputActive) return;
      OutputActive = true;
      OutputActivated?.Invoke();
    }

    private void DeactivateOutput() {
      if (!OutputActive) return;
      OutputActive = false;
      OutputDeactivated?.Invoke();
    }

    // Runtime output-mode switch: drop the active path (revert delegates + clear any
    // bot connection UI), tear the bridge down so the next mode starts from a clean
    // process, then — for local — bring the bridge straight back up on the device.
    // Bot mode leaves the bridge down; the user connects explicitly (or AutoConnect
    // does on the next launch).
    private async Task ApplyOutputModeAsync() {
      Log("Output mode: " + (IsLocalMode ? "Local (this PC)" : "Discord bot") + ".");
      ResetConnectionState();
      try {
        await discord.DeinitAsync();
      } catch (Exception ex) {
        Log("Output mode switch teardown error", ex);
      }
      if (IsLocalMode) await StartLocalOutputAsync();
    }

    // Bring the bridge online in local-output mode and route ACT's callouts to it.
    // The bridge plays everything on the local device — no channel/login — so output
    // is "active" once the bridge confirms the device opened. If it didn't (no device,
    // audio addon missing), leave output inactive so ACT keeps its own playback rather
    // than routing callouts to a bridge that can't play them.
    private async Task StartLocalOutputAsync() {
      try {
        Log("Starting local audio output…");
        bool live = await discord.StartLocalAsync(ToSettings());
        if (live) OnUi(ActivateOutput);
        else Log("Local audio output did not start — check that an output device is available.", LogLevel.Warn);
      } catch (Exception ex) {
        Log("Local audio output failed to start", ex);
      }
    }

    [RelayCommand(CanExecute = nameof(CanJoin))]
    private async Task JoinAsync() {
      CanJoin = false;
      if (await discord.JoinChannelAsync(SelectedServer, SelectedChannel)) {
        CanLeave = true;
        Log("Joined channel " + SelectedChannel + " on " + SelectedServer + ".");
        ActivateOutput();
      } else {
        Log("Unable to join channel. Does your bot have permission to join this channel?", LogLevel.Warn);
        CanJoin = true;
        await PopulateServersAsync();
      }
    }

    [RelayCommand(CanExecute = nameof(CanLeave))]
    private async Task LeaveAsync() {
      CanLeave = false;
      try {
        await discord.LeaveChannelAsync();
        CanJoin = true;
        Log("Left channel.");
        DeactivateOutput();
      } catch (Exception ex) {
        CanLeave = true;
        Log("Error leaving channel. Possible connection issue.", ex);
      }
    }

    // --- Text-to-Speech page actions --------------------------------------------
    // Test plays a sample through the active output, so it requires output to be live
    // (a joined channel in bot mode, or the local device in local mode) and a ready
    // voice — a selected SAPI voice, or an installed ONNX one.
    public bool CanTest =>
      OutputActive && (IsOnnx ? SelectedVoiceInstalled : !string.IsNullOrEmpty(TtsVoice));

    [RelayCommand(CanExecute = nameof(CanTest))]
    private async Task Test() {
      // Push the current config first so the bridge synthesizes with exactly the
      // voice/settings selected right now. Test is the manual "does this work?" path,
      // so it shouldn't depend on a debounced push having already landed (e.g. right
      // after a voice download); pushing here makes it self-sufficient.
      await PushConfigNow().ConfigureAwait(true);
      // Await the synth/send directly (not the async-void SpeakText) so Test stays
      // properly async on the UI thread and any failure is surfaced in order.
      try {
        await SpeakTextCoreAsync("Discord Triggers voice test.");
      } catch (Exception ex) {
        Log("Test playback error", ex);
      }
    }

    // Download and install the selected voice pack via OnnxDownloader (HttpClient +
    // SharpCompress extract + atomic move into ModelsDir/<DownloadId>/). Progress drives
    // the bar; on success the on-disk state is re-scanned so the row flips to ✓ and the
    // strip clears. Kokoro shares one pack id across all speakers, so re-scanning flips
    // its whole family at once.
    [RelayCommand]
    private async Task DownloadVoice() {
      var item = SelectedOnnxVoice;
      if (item == null || IsDownloading) return;
      IsDownloading = true;
      DownloadJustCompleted = false;
      DownloadProgress = 0;
      var cts = new CancellationTokenSource();
      downloadCts = cts;
      Log("Downloading " + item.Name + "…");
      try {
        // Progress<T> posts back to the captured UI SynchronizationContext, so updating
        // the observable DownloadProgress from the download thread is UI-safe.
        var progress = new Progress<double>(p => DownloadProgress = p);
        await OnnxDownloader.DownloadAsync(item.Info, ModelsDir, progress, cts.Token, msg => Log(msg));

        RefreshInstallState();
        Log("Downloaded " + item.Name + ".");
        // Re-push config now (not via the debounce) so the bridge re-validates the
        // model files and loads the freshly-installed voice. The pre-download push
        // left the bridge with "ONNX voice not ready"; this readies it for real ACT
        // callouts straight after a download (Test pushes its own config separately).
        // The bridge re-checks the files on every SetConfig, so the same descriptor
        // flips it to ready once the pack is on disk.
        await PushConfigNow().ConfigureAwait(true);
        // Keep a success confirmation in place so completion reads as "done", not a vanish.
        // It clears when the user picks another voice (see OnSelectedOnnxVoiceChanged).
        DownloadDoneText = item.Name + " is ready.";
        DownloadJustCompleted = true;
      } catch (OperationCanceledException) {
        Log("Download of " + item.Name + " was cancelled.", LogLevel.Warn);
      } catch (Exception ex) {
        Log("Download of " + item.Name + " failed", ex);
      } finally {
        IsDownloading = false;
        if (downloadCts == cts) downloadCts = null;
        cts.Dispose();
        NotifyDownloadStateChanged();
      }
    }

    // Re-scan every listed voice's on-disk install-state against the current ModelsDir
    // and refresh the dependent UI (download strip + Test gate). Preserves the current
    // selection — used after a download and when the models folder changes.
    private void RefreshInstallState() {
      var dir = OnnxCatalog.ResolveModelsDir(ModelsDir);
      foreach (var v in OnnxVoices) v.Installed = OnnxCatalog.IsInstalled(v.Info, dir);
      NotifyDownloadStateChanged();
    }

    // Raise the download-strip + Test-gate notifications. Shared by RefreshInstallState
    // (which mutates each OnnxVoiceItem.Installed directly, so the selectedOnnxVoice
    // setter's attributes don't cover it) and the DownloadVoice completion path, so the
    // two sites can't drift.
    private void NotifyDownloadStateChanged() {
      OnPropertyChanged(nameof(SelectedVoiceInstalled));
      OnPropertyChanged(nameof(ShowDownloadPrompt));
      OnPropertyChanged(nameof(ShowDownloadStrip));
      OnPropertyChanged(nameof(DownloadButtonVisible));
      TestCommand.NotifyCanExecuteChanged();
    }

    // One-line install tally for the Debug Log, e.g. "2 of 49 voice(s) installed".
    private string InstallCountText() =>
      OnnxVoices.Count(v => v.Installed) + " of " + OnnxVoices.Count + " voice(s) installed";

    // Refill OnnxVoices for the given family, annotating each with its on-disk state,
    // then select the first installed voice, else the recommended default, else the
    // first entry. The collection stays contiguous by locale (catalog order) so the
    // view's CollectionViewSource groups it cleanly.
    private void RebuildOnnxVoices(string family) {
      OnnxVoices.Clear();
      var dir = OnnxCatalog.ResolveModelsDir(ModelsDir);
      foreach (var v in OnnxCatalog.ByFamily(family))
        OnnxVoices.Add(new OnnxVoiceItem(v, OnnxCatalog.IsInstalled(v, dir)));
      SelectedOnnxVoice = OnnxVoices.FirstOrDefault(x => x.Installed)
        ?? OnnxVoices.FirstOrDefault(x => x.Recommended)
        ?? OnnxVoices.FirstOrDefault();
    }

    // Restore the persisted voice within the already-rebuilt family list. If the saved
    // id is gone from the catalog (e.g. a voice dropped in a catalog update), warn and
    // keep the family's auto-selected default rather than silently switching voices.
    private void RestoreSavedOnnxVoice(string id) {
      if (string.IsNullOrEmpty(id)) return;   // no saved pick → keep the rebuild default
      var match = OnnxVoices.FirstOrDefault(
        x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
      if (match != null) { SelectedOnnxVoice = match; return; }
      Log("Saved ONNX voice \"" + id + "\" is no longer available; using the default for " + OnnxFamily + ".", LogLevel.Warn);
    }

    // --- Information-page actions (bound from the WPF view) ---------------------
    // The Information tab's external links open via the view's XAML
    // (Microsoft.Xaml.Behaviors LaunchUriOrFileAction), so no URL command lives here.

    // Open Explorer at the diagnostics log (selected if present) so the user can grab
    // the one file we ask for in bug reports.
    [RelayCommand]
    private void OpenLogFolder() {
      try {
        var path = DiagnosticsLog.UnifiedPath;
        if (string.IsNullOrEmpty(path)) return;
        if (File.Exists(path))
          Process.Start("explorer.exe", "/select,\"" + path + "\"");
        else
          Process.Start("explorer.exe", "\"" + Path.GetDirectoryName(path) + "\"");
      } catch { /* best effort */ }
    }

    // --- TTS/sound routing (target of ACT's delegates, wired by the view) -------
    // ACT's PlayTtsMethod delegate is void, so this is async void: it kicks off the
    // synth/send and returns to ACT's callout thread immediately rather than blocking
    // it (ACT is busy churning log data), while still observing — logging — any
    // failure, so it's async, not a discard-the-Task fire-and-forget. The Test command
    // awaits the same SpeakTextCoreAsync.
    public async void SpeakText(string text) {
      try {
        await SpeakTextCoreAsync(text).ConfigureAwait(false);
      } catch (Exception ex) {
        Log("TTS playback error", ex);
      }
    }

    // Awaitable synth/send for the configured engine, shared by the async-void ACT
    // delegate target above and the (awaited) Test command.
    private Task SpeakTextCoreAsync(string text) {
      Log("Playing TTS for text: " + text);
      // ONNX: the bridge synthesizes with the voice it learned from SetConfig; only
      // the text crosses the wire. If no installed ONNX voice is configured the bridge
      // logs + skips, so a not-yet-downloaded voice never crashes it.
      return string.Equals(Engine, "onnx", StringComparison.OrdinalIgnoreCase)
        ? discord.SpeakOnnxAsync(text)
        : discord.SpeakAsync(text, TtsVoice, TtsVolume, TtsSpeed);
    }

    // ACT's PlaySoundMethod delegate (also void): async void for the same reason —
    // fire the playback without blocking ACT's callout thread, log any failure.
    public async void SpeakSoundFile(string path, int volume) {
      Log("Playing Audio file: " + path);
      try {
        await discord.SpeakFileAsync(path).ConfigureAwait(false);
      } catch (Exception ex) {
        Log("Sound playback error", ex);
      }
    }

    // --- Lifecycle --------------------------------------------------------------
    public void Initialize() {
      FromSettings(store.Load());

      Voices.Clear();
      foreach (var v in discord.GetInstalledVoices()) Voices.Add(v);
      if (string.IsNullOrEmpty(TtsVoice) || !Voices.Contains(TtsVoice))
        TtsVoice = Voices.Count > 0 ? Voices[0] : "";

      // ONNX catalog: resolve the persisted (or default) models dir, build the
      // family-scoped voice list with install-state, then restore the saved voice.
      // Wrapped in suppressPush so this load-time churn never schedules a SetConfig
      // (a fresh connect re-pushes the whole config anyway).
      suppressPush = true;
      try {
        ModelsDir = OnnxCatalog.ResolveModelsDir(ModelsDir);
        RebuildOnnxVoices(OnnxFamily);
        RestoreSavedOnnxVoice(loadedOnnxVoiceId);
      } finally {
        suppressPush = false;
      }
      Log("ONNX " + OnnxFamily + " voices: " + InstallCountText() + " in " + ModelsDir + ".");

      // Local-output mode brings the bridge up immediately (no Connect click needed);
      // bot mode honours AutoConnect.
      if (IsLocalMode) _ = StartLocalOutputAsync();
      else if (AutoConnect) _ = ConnectAsync();
    }

    public void Save() => store.Save(ToSettings());

    public async Task ShutdownAsync() {
      discord.BotReady -= OnBotReady;
      discord.Log -= OnLog;
      discord.Disconnected -= OnDisconnected;
      // Stop any in-flight debounce so a pending SetConfig can't fire at the bridge
      // after we tear it down; suppressPush blocks a late property change (e.g. a
      // binding write-back during teardown) from scheduling a new one.
      suppressPush = true;
      CancelPendingPushes();
      // ConfigureAwait(false): teardown runs from the UI thread via an async-void
      // DeInitPlugin that ACT doesn't await, so by the time the bridge finishes
      // shutting down the host window handle may be gone. Resuming on the captured
      // WinForms context would marshal via Control.BeginInvoke and throw. Nothing
      // after this needs the UI thread.
      await discord.DeinitAsync().ConfigureAwait(false);
    }

    // --- Logging ----------------------------------------------------------------
    public void Log(string text, LogLevel level = LogLevel.Info) {
      // Capture to the diagnostics file first, off the UI thread, so a busy UI never
      // delays or drops a line; UI display is a separate, best-effort concern. The
      // level tags the file line and drives the row colour.
      DiagnosticsLog.Append(text, level);
      OnUi(() => LogEntries.Add(new LogEntry(DateTime.Now, text, level)));
    }

    // Exception overload: the UI/file row stays a short human line, while the file
    // ALSO records the full exception (type + inner + stack) — the detail a user's
    // diagnostics file needs that ex.Message alone drops. Defaults to Error since
    // this overload exists for caught failures.
    public void Log(string userText, Exception ex, LogLevel level = LogLevel.Error) {
      DiagnosticsLog.Append(userText + " | " + ex, level);
      OnUi(() => LogEntries.Add(new LogEntry(DateTime.Now, userText + ": " + ex.Message, level)));
    }

    private void OnLog(string text, LogLevel level) => Log(text, level);

    private void OnBotReady() {
      // Bridge notifications arrive on a thread-pool thread; marshal to the UI.
      OnUi(() => {
        IsConnected = true;
        CanJoin = true;
        _ = PopulateServersAsync();
      });
    }

    // The bridge connection dropped on its own (process exit / broken pipe). Revert
    // the UI so the user can reconnect. (The explicit Disconnect command resets state
    // itself, so this is the unsolicited path; resetting twice is harmless.)
    private void OnDisconnected() => ResetConnectionState();

    private async Task PopulateServersAsync() {
      try {
        string[] servers = await discord.GetServersAsync();
        Log("Found " + servers.Length + " discord server(s).");
        OnUi(() => {
          Servers.Clear();
          Channels.Clear();
          foreach (var s in servers) Servers.Add(s);
          SelectedServer = Servers.Count > 0 ? Servers[0] : null;
        });
      } catch (Exception ex) {
        Log("Error populating servers.", ex);
      }
    }

    private async Task PopulateChannelsAsync(string server) {
      try {
        string[] channels = await discord.GetChannelsAsync(server);
        OnUi(() => {
          Channels.Clear();
          foreach (var c in channels) Channels.Add(c);
          if (Channels.Count > 0) {
            SelectedChannel = Channels[0];
            Log("Found " + Channels.Count + " available voice channel(s) for " + server);
          } else {
            Log("Error: Could not find any available voice channels for " + server, LogLevel.Warn);
          }
        });
      } catch (Exception ex) {
        Log("Error populating channels.", ex);
      }
    }

    // --- Settings mapping -------------------------------------------------------
    private void FromSettings(PluginSettings s) {
      suppressPush = true;
      try {
        OutputMode = s.OutputMode ?? "bot";
        LocalOutputVolume = s.LocalOutputVolume;
        BotToken = s.BotToken ?? "";
        BotStatus = s.BotStatus ?? "";
        AutoConnect = s.AutoConnect;
        TtsVoice = s.TtsVoice ?? "";
        TtsVolume = s.TtsVolume;
        TtsSpeed = s.TtsSpeed;
        Engine = s.TtsEngine ?? "sapi";
        TtsThreads = s.TtsThreads;
        ModelsDir = s.ModelsDir ?? "";           // set before OnnxFamily, whose setter rebuilds the list
        OnnxFamily = s.OnnxFamily ?? "piper";
        loadedOnnxVoiceId = s.OnnxVoice ?? "";   // selected in Initialize, after the list is built
        RandomFx = s.RandomFx;
        FxChance = s.FxChance;
        Normalize = s.Normalize;
        NormalizeTarget = s.NormalizeTarget;
        AudioQualityIndex = s.AudioQualityIndex;
        LimiterEnabled = s.LimiterEnabled;
        LimiterCeilingIndex = s.LimiterCeilingIndex;
      } finally {
        suppressPush = false;
      }
    }

    private PluginSettings ToSettings() => new PluginSettings {
      OutputMode = OutputMode,
      LocalOutputVolume = LocalOutputVolume,
      BotToken = BotToken,
      BotStatus = BotStatus,
      AutoConnect = AutoConnect,
      TtsVoice = TtsVoice,
      TtsVolume = TtsVolume,
      TtsSpeed = TtsSpeed,
      TtsEngine = Engine,
      OnnxFamily = OnnxFamily,
      OnnxVoice = SelectedOnnxVoice?.Id ?? "",
      TtsThreads = TtsThreads,
      ModelsDir = ModelsDir,
      RandomFx = RandomFx,
      FxChance = FxChance,
      Normalize = Normalize,
      NormalizeTarget = NormalizeTarget,
      AudioQualityIndex = AudioQualityIndex,
      LimiterEnabled = LimiterEnabled,
      LimiterCeilingIndex = LimiterCeilingIndex,
    };

    // --- Debounced config pushes ------------------------------------------------
    private void ScheduleStatusPush() => Schedule(ref statusPushCts, StatusDebounceMs);
    private void ScheduleConfigPush() => Schedule(ref configPushCts, ConfigDebounceMs);

    // Push the current config to the bridge immediately, dropping any pending
    // debounced push so it can't fire a stale duplicate afterwards. SetConfigAsync
    // no-ops while the bridge pipe is absent, so this is safe when disconnected.
    private Task PushConfigNow() {
      Cancel(configPushCts); configPushCts = null;
      return discord.SetConfigAsync(ToSettings());
    }

    private void Schedule(ref CancellationTokenSource slot, int delayMs) {
      if (suppressPush) return;
      // Cancel + dispose the superseded source so its Task.Delay timer registration
      // is released. Cancel runs all registrations synchronously, so Dispose is safe
      // right after. (Debouncing runs on the UI thread, so no race on the slot.)
      var cts = new CancellationTokenSource();
      var old = slot;
      slot = cts;
      Cancel(old);
      _ = DebouncedPush(delayMs, cts.Token);
    }

    // Cancel both in-flight debounces (called on shutdown). Same UI-thread / no-race
    // assumption as Schedule.
    private void CancelPendingPushes() {
      Cancel(statusPushCts); statusPushCts = null;
      Cancel(configPushCts); configPushCts = null;
      Cancel(downloadCts); downloadCts = null;
    }

    private static void Cancel(CancellationTokenSource cts) {
      if (cts == null) return;
      cts.Cancel();
      cts.Dispose();
    }

    private async Task DebouncedPush(int delayMs, CancellationToken token) {
      // ConfigureAwait(false): this runs fire-and-forget off a UI-thread property
      // change, so the awaits would otherwise capture ACT's WinForms
      // SynchronizationContext and marshal the continuation back via Control.BeginInvoke.
      // The push does no UI work, and at shutdown the host handle may be gone — that
      // BeginInvoke throws on the thread pool as an unhandled exception. Resuming off
      // the pool avoids both the needless hop and the teardown crash.
      try { await Task.Delay(delayMs, token).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
      if (token.IsCancellationRequested) return;
      // No-op while disconnected; ConnectAsync re-pushes the whole config on connect.
      await discord.SetConfigAsync(ToSettings()).ConfigureAwait(false);
    }

    // --- Helpers ----------------------------------------------------------------
    private void OnUi(Action action) {
      if (sync != null) sync.Post(_ => action(), null);
      else action();
    }

    // Clamp into [min,max], assign, and on an actual change raise PropertyChanged for the
    // property (plus an optional dependent computed label) and schedule a config push.
    private bool SetClamped(ref int field, int value, int min, int max, bool push,
                            string dependentLabel = null, [CallerMemberName] string propertyName = null) {
      if (!SetProperty(ref field, Clamp(value, min, max), propertyName)) return false;
      if (dependentLabel != null) OnPropertyChanged(dependentLabel);
      if (push) ScheduleConfigPush();
      return true;
    }

    private static int Clamp(int value, int min, int max) {
      if (max < min) return min;
      return value < min ? min : (value > max ? max : value);
    }
  }
}
