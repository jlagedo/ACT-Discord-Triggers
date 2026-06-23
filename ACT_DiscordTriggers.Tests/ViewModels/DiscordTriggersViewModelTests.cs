using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ACT_DiscordTriggers.Core.Ipc;
using ACT_DiscordTriggers.Core.Settings;
using ACT_DiscordTriggers.Core.Tts;
using ACT_DiscordTriggers.Core.ViewModels;
using Xunit;

namespace ACT_DiscordTriggers.Tests {
  public class DiscordTriggersViewModelTests {
    // The VM captures SynchronizationContext.Current in its ctor. xUnit may have one
    // installed; null it so OnUi(...) runs inline and the async-over-completed-tasks
    // chains finish synchronously, making assertions deterministic.
    private static DiscordTriggersViewModel NewVm(FakeDiscordService fake, SettingsStore store) {
      var prev = SynchronizationContext.Current;
      SynchronizationContext.SetSynchronizationContext(null);
      try { return new DiscordTriggersViewModel(fake, store); }
      finally { SynchronizationContext.SetSynchronizationContext(prev); }
    }

    private static SettingsStore TempStore() {
      string dir = Path.Combine(Path.GetTempPath(), "actdt-vmtest-" + Guid.NewGuid().ToString("N"));
      Directory.CreateDirectory(dir);
      return new SettingsStore(dir, "test.config.xml", (_, __) => { });
    }

    [Fact]
    public void Initialize_LoadsSavedSettings_IntoProperties() {
      var store = TempStore();
      store.Save(new PluginSettings {
        BotToken = "tok", BotStatus = "hi", AutoConnect = false,
        TtsVoice = "Bravo", FxChance = 70, AudioQualityIndex = 2,
      });
      var fake = new FakeDiscordService { VoicesToReturn = new[] { "Alpha", "Bravo" } };
      var vm = NewVm(fake, store);

      vm.Initialize();

      Assert.Equal("tok", vm.BotToken);
      Assert.Equal("hi", vm.BotStatus);
      Assert.Equal(70, vm.FxChance);
      Assert.Equal(2, vm.AudioQualityIndex);
      Assert.Equal("Bravo", vm.TtsVoice);
    }

    [Fact]
    public void Initialize_FallsBackToFirstVoice_WhenSavedVoiceMissing() {
      var store = TempStore();
      store.Save(new PluginSettings { TtsVoice = "Ghost" });
      var fake = new FakeDiscordService { VoicesToReturn = new[] { "Alpha", "Bravo" } };
      var vm = NewVm(fake, store);

      vm.Initialize();

      Assert.Equal("Alpha", vm.TtsVoice);
    }

    [Fact]
    public void Initialize_AutoConnect_ConnectsWithLoadedSettings() {
      var store = TempStore();
      store.Save(new PluginSettings { AutoConnect = true, BotToken = "auto" });
      var fake = new FakeDiscordService { IsConnectedResult = false };
      var vm = NewVm(fake, store);

      vm.Initialize();

      Assert.NotNull(fake.ConnectCalledWith);
      Assert.Equal("auto", fake.ConnectCalledWith.BotToken);
    }

    [Fact]
    public void FxChance_UpdatesLabel() {
      var vm = NewVm(new FakeDiscordService(), TempStore());
      vm.FxChance = 50;
      Assert.Equal("FX Chance: 50%", vm.FxChanceLabel);
    }

    [Fact]
    public void FxChance_ClampsToRange() {
      var vm = NewVm(new FakeDiscordService(), TempStore());
      vm.FxChance = 999;
      Assert.Equal(100, vm.FxChance);
      vm.FxChance = -5;
      Assert.Equal(0, vm.FxChance);
    }

    [Fact]
    public void ShowHighQualityWarning_OnlyAtHighTier() {
      var vm = NewVm(new FakeDiscordService(), TempStore());
      vm.AudioQualityIndex = 2;
      Assert.True(vm.ShowHighQualityWarning);
      vm.AudioQualityIndex = 1;
      Assert.False(vm.ShowHighQualityWarning);
    }

    [Fact]
    public void LimiterCeilingIndex_ClampsToRange() {
      var vm = NewVm(new FakeDiscordService(), TempStore());
      vm.LimiterCeilingIndex = 99;
      Assert.Equal(PluginSettings.LimiterCeilingIndexMax, vm.LimiterCeilingIndex);
      vm.LimiterCeilingIndex = -3;
      Assert.Equal(PluginSettings.LimiterCeilingIndexMin, vm.LimiterCeilingIndex);
    }

    [Fact]
    public void Initialize_LoadsLimiterSettings_IntoProperties() {
      var store = TempStore();
      store.Save(new PluginSettings { LimiterEnabled = false, LimiterCeilingIndex = 3 });
      var vm = NewVm(new FakeDiscordService(), store);

      vm.Initialize();

      Assert.False(vm.LimiterEnabled);
      Assert.Equal(3, vm.LimiterCeilingIndex);
    }

    [Fact]
    public async Task Connect_WhenNotConnected_CallsConnectWithCurrentSettings() {
      var fake = new FakeDiscordService { IsConnectedResult = false };
      var vm = NewVm(fake, TempStore());
      vm.BotToken = "abc";

      await vm.ConnectCommand.ExecuteAsync(null);

      Assert.NotNull(fake.ConnectCalledWith);
      Assert.Equal("abc", fake.ConnectCalledWith.BotToken);
    }

    [Fact]
    public async Task Connect_WhenAlreadyConnected_DoesNotReconnect() {
      var fake = new FakeDiscordService { IsConnectedResult = true };
      var vm = NewVm(fake, TempStore());

      await vm.ConnectCommand.ExecuteAsync(null);

      Assert.Null(fake.ConnectCalledWith);
    }

    [Fact]
    public void BotReady_PopulatesServersAndChannels_AndEnablesJoin() {
      var fake = new FakeDiscordService {
        ServersToReturn = new[] { "S1", "S2" },
        ChannelsToReturn = new[] { "C1" },
      };
      var vm = NewVm(fake, TempStore());

      fake.RaiseBotReady();

      Assert.True(vm.CanJoin);
      Assert.Equal(new[] { "S1", "S2" }, vm.Servers);
      Assert.Equal("S1", vm.SelectedServer);
      Assert.Equal(new[] { "C1" }, vm.Channels);
      Assert.Equal("C1", vm.SelectedChannel);
    }

    [Fact]
    public async Task Join_Success_EnablesLeave_AndRaisesOutputActivated() {
      var fake = new FakeDiscordService {
        ServersToReturn = new[] { "S1" }, ChannelsToReturn = new[] { "C1" }, JoinResult = true,
      };
      var vm = NewVm(fake, TempStore());
      fake.RaiseBotReady();
      bool activated = false;
      vm.OutputActivated += () => activated = true;

      await vm.JoinCommand.ExecuteAsync(null);

      Assert.True(vm.CanLeave);
      Assert.True(vm.OutputActive);
      Assert.True(activated);
      Assert.Equal("S1", fake.JoinedServer);
      Assert.Equal("C1", fake.JoinedChannel);
    }

    [Fact]
    public async Task Join_Failure_RepopulatesServers_AndKeepsJoinEnabled() {
      var fake = new FakeDiscordService {
        ServersToReturn = new[] { "S1" }, ChannelsToReturn = new[] { "C1" }, JoinResult = false,
      };
      var vm = NewVm(fake, TempStore());
      fake.RaiseBotReady();
      int serversAfterReady = fake.GetServersCallCount;

      await vm.JoinCommand.ExecuteAsync(null);

      Assert.True(vm.CanJoin);
      Assert.False(vm.CanLeave);
      Assert.Equal(serversAfterReady + 1, fake.GetServersCallCount);
    }

    [Fact]
    public async Task Leave_DisablesLeave_EnablesJoin_AndRaisesOutputDeactivated() {
      var fake = new FakeDiscordService {
        ServersToReturn = new[] { "S1" }, ChannelsToReturn = new[] { "C1" },
      };
      var vm = NewVm(fake, TempStore());
      fake.RaiseBotReady();
      await vm.JoinCommand.ExecuteAsync(null);
      bool deactivated = false;
      vm.OutputDeactivated += () => deactivated = true;

      await vm.LeaveCommand.ExecuteAsync(null);

      Assert.True(fake.LeaveCalled);
      Assert.True(deactivated);
      Assert.False(vm.OutputActive);
      Assert.True(vm.CanJoin);
      Assert.False(vm.CanLeave);
    }

    [Fact]
    public void BotReady_MarksConnected_AndTogglesCommandAvailability() {
      var fake = new FakeDiscordService { ServersToReturn = new[] { "S1" } };
      var vm = NewVm(fake, TempStore());

      Assert.False(vm.IsConnected);
      Assert.True(vm.CanConnect);
      Assert.True(vm.ConnectCommand.CanExecute(null));
      Assert.False(vm.DisconnectCommand.CanExecute(null));

      fake.RaiseBotReady();

      Assert.True(vm.IsConnected);
      Assert.False(vm.CanConnect);
      Assert.False(vm.ConnectCommand.CanExecute(null));
      Assert.True(vm.DisconnectCommand.CanExecute(null));
    }

    [Fact]
    public async Task Disconnect_TearsDownBridge_ClearsChannels_AndRevertsState() {
      var fake = new FakeDiscordService {
        ServersToReturn = new[] { "S1" }, ChannelsToReturn = new[] { "C1" },
      };
      var vm = NewVm(fake, TempStore());
      fake.RaiseBotReady();
      Assert.NotEmpty(vm.Servers);

      await vm.DisconnectCommand.ExecuteAsync(null);

      Assert.Equal(1, fake.DeinitCallCount);
      Assert.False(vm.IsConnected);
      Assert.True(vm.CanConnect);
      Assert.False(vm.CanJoin);
      Assert.False(vm.CanLeave);
      Assert.Empty(vm.Servers);
      Assert.Empty(vm.Channels);
      Assert.Null(vm.SelectedServer);
      Assert.Null(vm.SelectedChannel);
    }

    [Fact]
    public async Task Disconnect_WhileJoined_RaisesOutputDeactivated() {
      var fake = new FakeDiscordService {
        ServersToReturn = new[] { "S1" }, ChannelsToReturn = new[] { "C1" },
      };
      var vm = NewVm(fake, TempStore());
      fake.RaiseBotReady();
      await vm.JoinCommand.ExecuteAsync(null);
      bool deactivated = false;
      vm.OutputDeactivated += () => deactivated = true;

      await vm.DisconnectCommand.ExecuteAsync(null);

      Assert.True(deactivated);
    }

    [Fact]
    public void UnsolicitedDisconnect_RevertsState() {
      var fake = new FakeDiscordService {
        ServersToReturn = new[] { "S1" }, ChannelsToReturn = new[] { "C1" },
      };
      var vm = NewVm(fake, TempStore());
      fake.RaiseBotReady();
      Assert.True(vm.IsConnected);

      // Bridge died / pipe broke — not a user action.
      fake.RaiseDisconnected();

      Assert.False(vm.IsConnected);
      Assert.True(vm.CanConnect);
      Assert.Empty(vm.Servers);
      Assert.Empty(vm.Channels);
    }

    // --- Output mode (bot vs local) -------------------------------------------------

    [Fact]
    public void Initialize_LocalMode_StartsLocalOutput_WithoutDiscordLogin() {
      var store = TempStore();
      store.Save(new PluginSettings { OutputMode = "local" });
      var fake = new FakeDiscordService();
      var vm = NewVm(fake, store);

      vm.Initialize();

      Assert.True(vm.IsLocalMode);
      Assert.Equal(1, fake.StartLocalCallCount);
      Assert.Equal("local", fake.StartLocalCalledWith.OutputMode);
      Assert.Null(fake.ConnectCalledWith);   // no Discord login in local mode
      Assert.True(vm.OutputActive);          // delegates routed immediately
    }

    [Fact]
    public void Initialize_LocalMode_DeviceFailedToOpen_LeavesOutputInactive() {
      var store = TempStore();
      store.Save(new PluginSettings { OutputMode = "local" });
      var fake = new FakeDiscordService { StartLocalResult = false };   // device didn't come up
      var vm = NewVm(fake, store);
      bool activated = false;
      vm.OutputActivated += () => activated = true;

      vm.Initialize();

      Assert.Equal(1, fake.StartLocalCallCount);  // start was attempted
      Assert.False(vm.OutputActive);              // but output is not routed…
      Assert.False(activated);                    // …and the delegate swap never fired
    }

    [Fact]
    public void LocalMode_DisablesConnectCommand() {
      var vm = NewVm(new FakeDiscordService(), TempStore());

      vm.IsLocalMode = true;

      Assert.False(vm.CanConnect);
      Assert.False(vm.ConnectCommand.CanExecute(null));
    }

    [Fact]
    public void SwitchToLocalMode_TearsDownBridge_AndStartsLocalOutput() {
      var fake = new FakeDiscordService();
      var vm = NewVm(fake, TempStore());

      vm.IsLocalMode = true;   // runtime switch from the default bot mode

      Assert.Equal(1, fake.DeinitCallCount);     // prior path torn down first
      Assert.Equal(1, fake.StartLocalCallCount); // then local output started
      Assert.True(vm.OutputActive);
    }

    [Fact]
    public void SwitchBackToBotMode_TearsDownLocalOutput_AndLeavesBridgeDown() {
      var store = TempStore();
      store.Save(new PluginSettings { OutputMode = "local" });
      var fake = new FakeDiscordService();
      var vm = NewVm(fake, store);
      vm.Initialize();
      Assert.True(vm.OutputActive);

      vm.IsBotMode = true;   // switch local -> bot

      Assert.False(vm.OutputActive);             // delegates handed back
      Assert.Equal(1, fake.StartLocalCallCount); // not started again for bot mode
      Assert.True(fake.DeinitCallCount >= 1);    // local bridge torn down
    }

    [Fact]
    public void SpeakText_ForwardsCurrentVoiceVolumeSpeed() {
      var fake = new FakeDiscordService();
      var vm = NewVm(fake, TempStore());
      vm.TtsVoice = "V"; vm.TtsVolume = 7; vm.TtsSpeed = 3;

      vm.SpeakText("hello");

      var call = Assert.Single(fake.SpeakCalls);
      Assert.Equal(("hello", "V", 7, 3), call);
    }

    [Fact]
    public async Task TestCommand_PushesConfigBeforeSpeaking() {
      var fake = new FakeDiscordService();
      var vm = NewVm(fake, TempStore());
      vm.TtsVoice = "V";        // SAPI voice → CanTest needs only live output + a voice
      vm.OutputActive = true;   // output is live (joined channel / local device), so Test is allowed
      Assert.True(vm.TestCommand.CanExecute(null));

      await vm.TestCommand.ExecuteAsync(null);

      // The bridge got the current config (so it synthesizes with the live selection),
      // and the sample was spoken.
      Assert.Contains(fake.SetConfigCalls, c => c.TtsVoice == "V");
      Assert.Contains(fake.SpeakCalls, c => c.voice == "V");
    }

    [Fact]
    public void SpeakText_OnnxEngine_RoutesToSpeakOnnx_NotSapi() {
      var fake = new FakeDiscordService();
      var vm = NewVm(fake, TempStore());
      vm.Engine = "onnx";

      vm.SpeakText("Stack for the tower");

      // ONNX sends only the text; the voice already reached the bridge via SetConfig.
      Assert.Equal("Stack for the tower", Assert.Single(fake.SpeakOnnxCalls));
      Assert.Empty(fake.SpeakCalls);
    }

    [Fact]
    public void Log_AppendsEntry() {
      var vm = NewVm(new FakeDiscordService(), TempStore());
      vm.Log("hello world");
      var entry = Assert.Single(vm.LogEntries);
      Assert.Equal("hello world", entry.Message);
    }

    // --- Network integration: drive the real download through the VM command --------
    // Opt-in (hits GitHub's tts-models release). Set ACT_DT_NETWORK_TESTS=1 to run; CI
    // skips it by default so a flaky/absent network never reds the gate.
    private static bool NetworkTestsEnabled =>
      !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ACT_DT_NETWORK_TESTS"));

    private const string SmallVoiceId = "vits-piper-en_US-amy-medium";

    private static (DiscordTriggersViewModel vm, string modelsDir, FakeDiscordService discord) NewOnnxVm() {
      var modelsDir = Path.Combine(Path.GetTempPath(), "actdt-dl-" + Guid.NewGuid().ToString("N"));
      Directory.CreateDirectory(modelsDir);
      var store = TempStore();
      store.Save(new PluginSettings { TtsEngine = "onnx", OnnxFamily = "piper", ModelsDir = modelsDir });
      var discord = new FakeDiscordService();
      var vm = NewVm(discord, store);
      vm.Initialize();
      return (vm, modelsDir, discord);
    }

    [Fact]
    public async Task DownloadVoiceCommand_RealNetwork_InstallsVoice_AndUpdatesUiAndTestGate() {
      Assert.SkipUnless(NetworkTestsEnabled, "Set ACT_DT_NETWORK_TESTS=1 to run network integration tests.");
      var (vm, modelsDir, discord) = NewOnnxVm();
      try {
        Assert.True(vm.IsOnnx);
        var amy = vm.OnnxVoices.Single(v => v.Id == SmallVoiceId);
        vm.SelectedOnnxVoice = amy;

        // Pre-state: not installed → download strip showing, Test gated off even though
        // output is live (OutputActive), proving the gate ties to install-state.
        Assert.False(amy.Installed);
        Assert.True(vm.ShowDownloadPrompt);
        Assert.True(vm.DownloadButtonVisible);
        vm.OutputActive = true;
        Assert.False(vm.TestCommand.CanExecute(null));

        await vm.DownloadVoiceCommand.ExecuteAsync(null);

        // Post-state: the action installed the pack, flipped the row, cleared the strip,
        // surfaced the "ready" confirmation, and enabled Test.
        Assert.True(amy.Installed);
        Assert.True(vm.SelectedVoiceInstalled);
        Assert.False(vm.ShowDownloadPrompt);
        Assert.False(vm.DownloadButtonVisible);
        Assert.True(vm.DownloadJustCompleted);
        Assert.Contains("ready", vm.DownloadDoneText, StringComparison.OrdinalIgnoreCase);
        Assert.True(vm.TestCommand.CanExecute(null));

        // The files really landed on disk and the catalog agrees.
        Assert.True(OnnxCatalog.IsInstalled(amy.Info, modelsDir));
        Assert.True(Directory.EnumerateFiles(Path.Combine(modelsDir, SmallVoiceId), "*.onnx").Any());

        // The download milestones reached the Debug Log.
        Assert.Contains(vm.LogEntries, e => e.Message.StartsWith("Fetching "));
        Assert.Contains(vm.LogEntries, e => e.Message.Contains("Installed " + SmallVoiceId));
        Assert.Contains(vm.LogEntries, e => e.Message == "Downloaded " + amy.Name + ".");

        // The download re-pushed config to the bridge with the now-installed voice, so a
        // Test right after finds it loaded instead of "ONNX voice not ready".
        Assert.Contains(discord.SetConfigCalls, c => c.OnnxVoice == SmallVoiceId);
      } finally {
        try { Directory.Delete(modelsDir, true); } catch { }
      }
    }

    [Fact]
    public async Task DownloadVoiceCommand_RealNetwork_RescanAfterFolderChange_FindsInstall() {
      Assert.SkipUnless(NetworkTestsEnabled, "Set ACT_DT_NETWORK_TESTS=1 to run network integration tests.");
      var (vm, modelsDir, _) = NewOnnxVm();
      try {
        var amy = vm.OnnxVoices.Single(v => v.Id == SmallVoiceId);
        vm.SelectedOnnxVoice = amy;
        await vm.DownloadVoiceCommand.ExecuteAsync(null);
        Assert.True(amy.Installed);

        // Point at an empty folder → the rescan must clear the install marks…
        var empty = Path.Combine(Path.GetTempPath(), "actdt-empty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(empty);
        try {
          vm.ModelsDir = empty;
          Assert.False(vm.OnnxVoices.Single(v => v.Id == SmallVoiceId).Installed);

          // …and pointing back at the populated folder must rediscover the install.
          vm.ModelsDir = modelsDir;
          Assert.True(vm.OnnxVoices.Single(v => v.Id == SmallVoiceId).Installed);
        } finally {
          try { Directory.Delete(empty, true); } catch { }
        }
      } finally {
        try { Directory.Delete(modelsDir, true); } catch { }
      }
    }

    private sealed class FakeDiscordService : IDiscordService {
      public event Action BotReady;
      public event Action<string, LogLevel> Log;
      public event Action Disconnected;

      public bool IsConnectedResult;
      public bool JoinResult = true;
      public string[] ServersToReturn = new string[0];
      public string[] ChannelsToReturn = new string[0];
      public string[] VoicesToReturn = new string[0];

      public PluginSettings ConnectCalledWith;
      public PluginSettings StartLocalCalledWith;
      public int StartLocalCallCount;
      // Whether StartLocalAsync reports the local device actually came up. Default
      // true (the device opens); set false to exercise the failed-device path.
      public bool StartLocalResult = true;
      public readonly List<PluginSettings> SetConfigCalls = new List<PluginSettings>();
      public string JoinedServer;
      public string JoinedChannel;
      public int GetServersCallCount;
      public bool LeaveCalled;
      public int DeinitCallCount;
      public readonly List<(string text, string voice, int vol, int speed)> SpeakCalls =
        new List<(string, string, int, int)>();
      public readonly List<string> SpeakOnnxCalls = new List<string>();

      public void RaiseBotReady() => BotReady?.Invoke();
      public void RaiseDisconnected() => Disconnected?.Invoke();

      public Task ConnectAsync(PluginSettings config) { ConnectCalledWith = config; return Task.CompletedTask; }
      public Task<bool> StartLocalAsync(PluginSettings config) { StartLocalCalledWith = config; StartLocalCallCount++; return Task.FromResult(StartLocalResult); }
      public Task SetConfigAsync(PluginSettings config) { SetConfigCalls.Add(config); return Task.CompletedTask; }
      public Task<bool> IsConnectedAsync() => Task.FromResult(IsConnectedResult);
      public Task<string[]> GetServersAsync() { GetServersCallCount++; return Task.FromResult(ServersToReturn); }
      public Task<string[]> GetChannelsAsync(string server) => Task.FromResult(ChannelsToReturn);
      public Task<bool> JoinChannelAsync(string server, string channel) {
        JoinedServer = server; JoinedChannel = channel; return Task.FromResult(JoinResult);
      }
      public Task LeaveChannelAsync() { LeaveCalled = true; return Task.CompletedTask; }
      public Task DeinitAsync() { DeinitCallCount++; return Task.CompletedTask; }
      public Task SpeakAsync(string text, string voice, int vol, int speed) { SpeakCalls.Add((text, voice, vol, speed)); return Task.CompletedTask; }
      public Task SpeakOnnxAsync(string text) { SpeakOnnxCalls.Add(text); return Task.CompletedTask; }
      public Task SpeakFileAsync(string path) => Task.CompletedTask;
      public string[] GetInstalledVoices() => VoicesToReturn;
    }
  }
}
