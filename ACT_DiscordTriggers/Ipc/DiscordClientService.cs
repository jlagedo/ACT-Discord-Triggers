using System;
using System.Linq;
using System.Speech.Synthesis;
using System.Threading.Tasks;
using ACT_DiscordTriggers.Settings;

namespace ACT_DiscordTriggers.Ipc {
  // Production IDiscordService: forwards to the static DiscordClient facade and
  // bridges its static BotReady/Log events to instance events. Not source-linked
  // into tests (it pulls in DiscordClient + System.Speech).
  public sealed class DiscordClientService : IDiscordService, IDisposable {
    private readonly DiscordClient.BotLoaded botReadyHandler;
    private readonly DiscordClient.BotMessage logHandler;

    public event Action BotReady;
    public event Action<string> Log;

    public DiscordClientService() {
      botReadyHandler = () => BotReady?.Invoke();
      logHandler = msg => Log?.Invoke(msg);
      DiscordClient.BotReady += botReadyHandler;
      DiscordClient.Log += logHandler;
    }

    public Task ConnectAsync(PluginSettings config) => DiscordClient.ConnectAsync(config);
    public Task SetConfigAsync(PluginSettings config) => DiscordClient.SetConfigAsync(config);
    public Task<bool> IsConnectedAsync() => DiscordClient.IsConnectedAsync();
    public Task<string[]> GetServersAsync() => DiscordClient.GetServersAsync();
    public Task<string[]> GetChannelsAsync(string server) => DiscordClient.GetChannelsAsync(server);
    public Task<bool> JoinChannelAsync(string server, string channel) => DiscordClient.JoinChannel(server, channel);
    public Task LeaveChannelAsync() => DiscordClient.LeaveChannelAsync();
    public Task DeinitAsync() => DiscordClient.DeinitAsync();

    public void Speak(string text, string voice, int vol, int speed) => DiscordClient.Speak(text, voice, vol, speed);
    public void SpeakFile(string path) => DiscordClient.SpeakFile(path);

    public string[] GetInstalledVoices() {
      try {
        using (var tts = new SpeechSynthesizer())
          return tts.GetInstalledVoices().Select(v => v.VoiceInfo.Name).ToArray();
      } catch {
        return new string[0];
      }
    }

    public void Dispose() {
      DiscordClient.BotReady -= botReadyHandler;
      DiscordClient.Log -= logHandler;
    }
  }
}
