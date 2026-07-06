using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ACT_DiscordTriggers.Core.Ipc;
using ACT_DiscordTriggers.Core.Settings;
using ACT_DiscordTriggers.Core.ViewModels;
using Fct.Abstractions;
using Fct.Plugins.DiscordTriggers;
using Xunit;

namespace ACT_DiscordTriggers.Tests {
  // The native port's audio seam. DiscordAudioSink is the terminal IAudioSink the plugin
  // registers while output is live; it forwards Speak/Play to the VM's internal synth cores
  // (the same cores the Test command and the old ACT PlayTtsMethod/PlaySoundMethod delegate
  // targets used). These tests pin that forwarding: the SAPI/ONNX engine branch, the
  // deliberately-ignored per-request AudioOptions, and the dropped per-file volume.
  //
  // The terminal-vs-built-in fan-out suppression (a terminal sink stops the chain so the
  // host's speaker sink never fires) is the host AudioService's responsibility (G3) and is
  // covered in the host repo — not reproducible here without the host runtime.
  public class DiscordAudioSinkTests {
    // The VM captures SynchronizationContext.Current in its ctor; null it so the async-over-
    // completed-task chains run inline and assertions are deterministic (as the VM tests do).
    private static DiscordTriggersViewModel NewVm(IDiscordService svc) {
      var prev = SynchronizationContext.Current;
      SynchronizationContext.SetSynchronizationContext(null);
      try {
        string dir = Path.Combine(Path.GetTempPath(), "actdt-sinktest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var store = new SettingsStore(dir, "test.config.xml", (_, __) => { });
        return new DiscordTriggersViewModel(svc, store);
      } finally {
        SynchronizationContext.SetSynchronizationContext(prev);
      }
    }

    [Fact]
    public async Task SpeakAsync_SapiEngine_RoutesTextThroughVmSapiSynth() {
      var fake = new RecordingDiscordService();
      var vm = NewVm(fake);
      vm.Engine = "sapi";
      vm.TtsVoice = "Bravo";
      vm.TtsVolume = 15;   // 0..20 scale (TtsVolumeMax)
      vm.TtsSpeed = 8;
      var sink = new DiscordAudioSink(vm);

      await sink.SpeakAsync("hello", AudioOptions.Default, CancellationToken.None);

      var call = Assert.Single(fake.SpeakCalls);
      Assert.Equal("hello", call.text);
      Assert.Equal("Bravo", call.voice);
      Assert.Equal(15, call.vol);
      Assert.Equal(8, call.speed);
      Assert.Empty(fake.SpeakOnnxCalls);
    }

    [Fact]
    public async Task SpeakAsync_OnnxEngine_RoutesTextThroughVmOnnxSynth() {
      var fake = new RecordingDiscordService();
      var vm = NewVm(fake);
      vm.Engine = "onnx";
      var sink = new DiscordAudioSink(vm);

      await sink.SpeakAsync("callout", AudioOptions.Default, CancellationToken.None);

      Assert.Equal("callout", Assert.Single(fake.SpeakOnnxCalls));
      Assert.Empty(fake.SpeakCalls);
    }

    [Fact]
    public async Task SpeakAsync_IgnoresPerRequestAudioOptions() {
      var fake = new RecordingDiscordService();
      var vm = NewVm(fake);
      vm.Engine = "sapi";
      vm.TtsVoice = "Configured";
      vm.TtsVolume = 15;
      var sink = new DiscordAudioSink(vm);

      // The host may pass its own voice/rate/volume; the sink routes through the plugin's OWN
      // configured engine/voice instead (a deliberate design choice of the port).
      var options = new AudioOptions(Volume: 5, Voice: "OptionVoice", Rate: 2f);
      await sink.SpeakAsync("x", options, CancellationToken.None);

      var call = Assert.Single(fake.SpeakCalls);
      Assert.Equal("Configured", call.voice);
      Assert.Equal(15, call.vol);
    }

    [Fact]
    public async Task PlayAsync_RoutesFileThroughVmFileSynth_DropsVolume() {
      var fake = new RecordingDiscordService();
      var vm = NewVm(fake);
      var sink = new DiscordAudioSink(vm);

      await sink.PlayAsync(@"C:\clips\alert.wav", volume: 42, CancellationToken.None);

      // The path forwards; volume 42 is intentionally dropped (no per-file gain over the wire).
      Assert.Equal(@"C:\clips\alert.wav", Assert.Single(fake.SpeakFileCalls));
    }

    private sealed class RecordingDiscordService : IDiscordService {
#pragma warning disable CS0067 // events are part of the interface; these tests don't raise them
      public event Action BotReady;
      public event Action<string, LogLevel> Log;
      public event Action Disconnected;
#pragma warning restore CS0067

      public readonly List<(string text, string voice, int vol, int speed)> SpeakCalls = new();
      public readonly List<string> SpeakOnnxCalls = new();
      public readonly List<string> SpeakFileCalls = new();

      public Task SpeakAsync(string text, string voice, int vol, int speed) {
        SpeakCalls.Add((text, voice, vol, speed));
        return Task.CompletedTask;
      }
      public Task SpeakOnnxAsync(string text) { SpeakOnnxCalls.Add(text); return Task.CompletedTask; }
      public Task SpeakFileAsync(string path) { SpeakFileCalls.Add(path); return Task.CompletedTask; }

      public Task ConnectAsync(PluginSettings config) => Task.CompletedTask;
      public Task<bool> StartLocalAsync(PluginSettings config) => Task.FromResult(true);
      public Task SetConfigAsync(PluginSettings config) => Task.CompletedTask;
      public Task<bool> IsConnectedAsync() => Task.FromResult(false);
      public Task<string[]> GetServersAsync() => Task.FromResult(Array.Empty<string>());
      public Task<string[]> GetChannelsAsync(string server) => Task.FromResult(Array.Empty<string>());
      public Task<bool> JoinChannelAsync(string server, string channel) => Task.FromResult(true);
      public Task LeaveChannelAsync() => Task.CompletedTask;
      public Task DeinitAsync() => Task.CompletedTask;
      public string[] GetInstalledVoices() => Array.Empty<string>();
    }
  }
}
