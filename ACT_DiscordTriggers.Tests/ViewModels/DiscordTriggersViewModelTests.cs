using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ACT_DiscordTriggers.Ipc;
using ACT_DiscordTriggers.Settings;
using ACT_DiscordTriggers.ViewModels;
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
      return new SettingsStore(dir, "test.config.xml", _ => { });
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
    public async Task Join_Success_EnablesLeave_AndRaisesJoinedChannel() {
      var fake = new FakeDiscordService {
        ServersToReturn = new[] { "S1" }, ChannelsToReturn = new[] { "C1" }, JoinResult = true,
      };
      var vm = NewVm(fake, TempStore());
      fake.RaiseBotReady();
      bool joined = false;
      vm.JoinedChannel += () => joined = true;

      await vm.JoinCommand.ExecuteAsync(null);

      Assert.True(vm.CanLeave);
      Assert.True(joined);
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
    public async Task Leave_DisablesLeave_EnablesJoin_AndRaisesLeftChannel() {
      var fake = new FakeDiscordService {
        ServersToReturn = new[] { "S1" }, ChannelsToReturn = new[] { "C1" },
      };
      var vm = NewVm(fake, TempStore());
      fake.RaiseBotReady();
      await vm.JoinCommand.ExecuteAsync(null);
      bool left = false;
      vm.LeftChannel += () => left = true;

      await vm.LeaveCommand.ExecuteAsync(null);

      Assert.True(fake.LeaveCalled);
      Assert.True(left);
      Assert.True(vm.CanJoin);
      Assert.False(vm.CanLeave);
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
    public void Log_AppendsEntry() {
      var vm = NewVm(new FakeDiscordService(), TempStore());
      vm.Log("hello world");
      var entry = Assert.Single(vm.LogEntries);
      Assert.Equal("hello world", entry.Message);
    }

    private sealed class FakeDiscordService : IDiscordService {
      public event Action BotReady;
      public event Action<string> Log;

      public bool IsConnectedResult;
      public bool JoinResult = true;
      public string[] ServersToReturn = new string[0];
      public string[] ChannelsToReturn = new string[0];
      public string[] VoicesToReturn = new string[0];

      public PluginSettings ConnectCalledWith;
      public string JoinedServer;
      public string JoinedChannel;
      public int GetServersCallCount;
      public bool LeaveCalled;
      public readonly List<(string text, string voice, int vol, int speed)> SpeakCalls =
        new List<(string, string, int, int)>();

      public void RaiseBotReady() => BotReady?.Invoke();

      public Task ConnectAsync(PluginSettings config) { ConnectCalledWith = config; return Task.CompletedTask; }
      public Task SetConfigAsync(PluginSettings config) => Task.CompletedTask;
      public Task<bool> IsConnectedAsync() => Task.FromResult(IsConnectedResult);
      public Task<string[]> GetServersAsync() { GetServersCallCount++; return Task.FromResult(ServersToReturn); }
      public Task<string[]> GetChannelsAsync(string server) => Task.FromResult(ChannelsToReturn);
      public Task<bool> JoinChannelAsync(string server, string channel) {
        JoinedServer = server; JoinedChannel = channel; return Task.FromResult(JoinResult);
      }
      public Task LeaveChannelAsync() { LeaveCalled = true; return Task.CompletedTask; }
      public Task DeinitAsync() => Task.CompletedTask;
      public void Speak(string text, string voice, int vol, int speed) => SpeakCalls.Add((text, voice, vol, speed));
      public void SpeakFile(string path) { }
      public string[] GetInstalledVoices() => VoicesToReturn;
    }
  }
}
