using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ACT_DiscordTriggers.Core.Ipc;
using ACT_DiscordTriggers.Core.Settings;

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

    // Domain ranges (mirror the WinForms control limits). The VM clamps so loading a
    // stale/out-of-range saved value can't throw when a range-limited control binds.
    private const int VolMin = 0, VolMax = 20;
    private const int SpeedMin = 0, SpeedMax = 20;
    private const int FxMin = 0, FxMax = 100;
    private const int NormMin = 12, NormMax = 30;
    private const int QualityMin = 0, QualityMax = 2; // 0=Low, 1=Medium, 2=High

    // Suppresses config/status pushes while loading settings into the properties.
    private bool suppressPush;

    public ObservableCollection<string> Servers { get; } = new ObservableCollection<string>();
    public ObservableCollection<string> Channels { get; } = new ObservableCollection<string>();
    public ObservableCollection<string> Voices { get; } = new ObservableCollection<string>();
    public ObservableCollection<LogEntry> LogEntries { get; } = new ObservableCollection<LogEntry>();

    // Raised when a channel is (re)joined/left so the view can swap ACT's
    // PlayTtsMethod/PlaySoundMethod delegates (kept out of the VM to stay ACT-free).
    public event Action JoinedChannel;
    public event Action LeftChannel;

    public DiscordTriggersViewModel(IDiscordService discord, SettingsStore store) {
      this.discord = discord;
      this.store = store;
      this.sync = SynchronizationContext.Current;
      this.discord.BotReady += OnBotReady;
      this.discord.Log += OnLog;
    }

    // --- Settings-backed properties ---------------------------------------------
    // Source-generated (no side effects): the value lives in the generated field and
    // is mapped to/from PluginSettings at the load/save boundary.
    [ObservableProperty] private string botToken = "";
    [ObservableProperty] private bool autoConnect;
    [ObservableProperty] private string ttsVoice = "";

    // Source-generated, side effect lives in the partial On…Changed hook.
    [ObservableProperty] private string botStatus = "Playing with ACT Triggers";
    partial void OnBotStatusChanged(string value) => ScheduleStatusPush();

    // Manual where a clamp or computed label applies (the generator writes the field
    // before the hook, so it can't enforce a clamp).
    private int ttsVolume = 10;
    public int TtsVolume {
      get => ttsVolume;
      set => SetProperty(ref ttsVolume, Clamp(value, VolMin, VolMax));
    }

    private int ttsSpeed = 10;
    public int TtsSpeed {
      get => ttsSpeed;
      set => SetProperty(ref ttsSpeed, Clamp(value, SpeedMin, SpeedMax));
    }

    [ObservableProperty] private bool randomFx;
    partial void OnRandomFxChanged(bool value) => ScheduleConfigPush();

    private int fxChance = 25;
    public int FxChance {
      get => fxChance;
      set {
        if (SetProperty(ref fxChance, Clamp(value, FxMin, FxMax))) {
          OnPropertyChanged(nameof(FxChanceLabel));
          ScheduleConfigPush();
        }
      }
    }

    [ObservableProperty] private bool normalize = true;
    partial void OnNormalizeChanged(bool value) => ScheduleConfigPush();

    private int normalizeTarget = 20;
    public int NormalizeTarget {
      get => normalizeTarget;
      set {
        if (SetProperty(ref normalizeTarget, Clamp(value, NormMin, NormMax))) {
          OnPropertyChanged(nameof(NormalizeTargetLabel));
          ScheduleConfigPush();
        }
      }
    }

    private int audioQualityIndex = 1;
    public int AudioQualityIndex {
      get => audioQualityIndex;
      set {
        if (SetProperty(ref audioQualityIndex, Clamp(value, QualityMin, QualityMax))) {
          OnPropertyChanged(nameof(ShowHighQualityWarning));
          ScheduleConfigPush();
        }
      }
    }

    // --- Computed (presentation) ------------------------------------------------
    public string FxChanceLabel => "FX Chance: " + FxChance + "%";
    public string NormalizeTargetLabel => "Auto-level Target: -" + NormalizeTarget + " dBFS";
    // The High tier may exceed an unboosted channel's 96 kbps cap; the view shows a warning.
    public bool ShowHighQualityWarning => AudioQualityIndex == QualityMax;

    // --- Command-enable state ---------------------------------------------------
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(JoinCommand))]
    private bool canJoin;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LeaveCommand))]
    private bool canLeave;

    // --- Selection --------------------------------------------------------------
    [ObservableProperty] private string selectedServer;
    [ObservableProperty] private string selectedChannel;

    // Repopulating Servers transiently sets this to null; bail before NRE-ing.
    partial void OnSelectedServerChanged(string value) {
      if (!string.IsNullOrEmpty(value)) _ = PopulateChannelsAsync(value);
    }

    // --- Commands ---------------------------------------------------------------
    [RelayCommand]
    private async Task ConnectAsync() {
      try {
        if (await discord.IsConnectedAsync()) {
          Log("Already connected to Discord.");
          return;
        }
        await discord.ConnectAsync(ToSettings());
      } catch (Exception ex) {
        Log("Connect failed: " + ex.Message);
      }
    }

    [RelayCommand(CanExecute = nameof(CanJoin))]
    private async Task JoinAsync() {
      CanJoin = false;
      if (await discord.JoinChannelAsync(SelectedServer, SelectedChannel)) {
        CanLeave = true;
        JoinedChannel?.Invoke();
      } else {
        Log("Unable to join channel. Does your bot have permission to join this channel?");
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
        LeftChannel?.Invoke();
      } catch (Exception ex) {
        Log("Error leaving channel. Possible connection issue.");
        CanLeave = true;
        Log(ex.Message);
      }
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
    public void SpeakText(string text) => discord.Speak(text, TtsVoice, TtsVolume, TtsSpeed);
    public void SpeakSoundFile(string path, int volume) => discord.SpeakFile(path);

    // --- Lifecycle --------------------------------------------------------------
    public void Initialize() {
      FromSettings(store.Load());

      Voices.Clear();
      foreach (var v in discord.GetInstalledVoices()) Voices.Add(v);
      if (string.IsNullOrEmpty(TtsVoice) || !Voices.Contains(TtsVoice))
        TtsVoice = Voices.Count > 0 ? Voices[0] : "";

      if (AutoConnect) _ = ConnectAsync();
    }

    public void Save() => store.Save(ToSettings());

    public async Task ShutdownAsync() {
      discord.BotReady -= OnBotReady;
      discord.Log -= OnLog;
      // Stop any in-flight debounce so a pending SetConfig can't fire at the bridge
      // after we tear it down; suppressPush blocks a late property change (e.g. a
      // binding write-back during teardown) from scheduling a new one.
      suppressPush = true;
      CancelPendingPushes();
      await discord.DeinitAsync();
    }

    // --- Logging ----------------------------------------------------------------
    public void Log(string text) {
      // Capture to the diagnostics file first, off the UI thread, so a busy UI never
      // delays or drops a line; UI display is a separate, best-effort concern.
      DiagnosticsLog.Append(text);
      OnUi(() => LogEntries.Add(new LogEntry(DateTime.Now, text)));
    }

    private void OnLog(string text) => Log(text);

    private void OnBotReady() {
      // Bridge notifications arrive on a thread-pool thread; marshal to the UI.
      OnUi(() => {
        CanJoin = true;
        _ = PopulateServersAsync();
      });
    }

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
        Log("Error populating servers.");
        Log(ex.Message);
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
            Log("Error: Could not find any available voice channels for " + server);
          }
        });
      } catch (Exception ex) {
        Log("Error populating channels.");
        Log(ex.Message);
      }
    }

    // --- Settings mapping -------------------------------------------------------
    private void FromSettings(PluginSettings s) {
      suppressPush = true;
      try {
        BotToken = s.BotToken ?? "";
        BotStatus = s.BotStatus ?? "";
        AutoConnect = s.AutoConnect;
        TtsVoice = s.TtsVoice ?? "";
        TtsVolume = s.TtsVolume;
        TtsSpeed = s.TtsSpeed;
        RandomFx = s.RandomFx;
        FxChance = s.FxChance;
        Normalize = s.Normalize;
        NormalizeTarget = s.NormalizeTarget;
        AudioQualityIndex = s.AudioQualityIndex;
      } finally {
        suppressPush = false;
      }
    }

    private PluginSettings ToSettings() => new PluginSettings {
      BotToken = BotToken,
      BotStatus = BotStatus,
      AutoConnect = AutoConnect,
      TtsVoice = TtsVoice,
      TtsVolume = TtsVolume,
      TtsSpeed = TtsSpeed,
      RandomFx = RandomFx,
      FxChance = FxChance,
      Normalize = Normalize,
      NormalizeTarget = NormalizeTarget,
      AudioQualityIndex = AudioQualityIndex,
    };

    // --- Debounced config pushes ------------------------------------------------
    private void ScheduleStatusPush() => Schedule(ref statusPushCts, StatusDebounceMs);
    private void ScheduleConfigPush() => Schedule(ref configPushCts, ConfigDebounceMs);

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
    }

    private static void Cancel(CancellationTokenSource cts) {
      if (cts == null) return;
      cts.Cancel();
      cts.Dispose();
    }

    private async Task DebouncedPush(int delayMs, CancellationToken token) {
      try { await Task.Delay(delayMs, token); } catch (OperationCanceledException) { return; }
      if (token.IsCancellationRequested) return;
      // No-op while disconnected; ConnectAsync re-pushes the whole config on connect.
      await discord.SetConfigAsync(ToSettings());
    }

    // --- Helpers ----------------------------------------------------------------
    private void OnUi(Action action) {
      if (sync != null) sync.Post(_ => action(), null);
      else action();
    }

    private static int Clamp(int value, int min, int max) {
      if (max < min) return min;
      return value < min ? min : (value > max ? max : value);
    }
  }
}
