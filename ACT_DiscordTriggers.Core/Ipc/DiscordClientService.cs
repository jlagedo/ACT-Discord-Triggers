using System;
using System.Threading.Tasks;
using ACT_DiscordTriggers.Core.Settings;
using ACT_DiscordTriggers.Core.Tts;

namespace ACT_DiscordTriggers.Core.Ipc {
  // Production IDiscordService: forwards to the static DiscordClient facade and
  // bridges its static BotReady/Log events to instance events. Not source-linked
  // into tests (it pulls in DiscordClient + System.Speech).
  public sealed class DiscordClientService : IDiscordService, IDisposable {
    private readonly DiscordClient.BotLoaded botReadyHandler;
    private readonly DiscordClient.BotMessage logHandler;
    private readonly Action disconnectedHandler;

    public event Action BotReady;
    public event Action<string, LogLevel> Log;
    public event Action Disconnected;

    public DiscordClientService() {
      botReadyHandler = () => BotReady?.Invoke();
      logHandler = (msg, level) => Log?.Invoke(msg, level);
      disconnectedHandler = () => Disconnected?.Invoke();
      DiscordClient.BotReady += botReadyHandler;
      DiscordClient.Log += logHandler;
      DiscordClient.Disconnected += disconnectedHandler;
    }

    // Resolve the ONNX synth descriptor here (where PluginSettings + the catalog
    // are both in scope) so DiscordClient stays decoupled from the concrete POCO.
    public Task ConnectAsync(PluginSettings config) => DiscordClient.ConnectAsync(config, OnnxSynthParams.Resolve(config));
    public Task<bool> StartLocalAsync(PluginSettings config) => DiscordClient.StartLocalAsync(config, OnnxSynthParams.Resolve(config));
    public Task SetConfigAsync(PluginSettings config) => DiscordClient.SetConfigAsync(config, OnnxSynthParams.Resolve(config));
    public Task<bool> IsConnectedAsync() => DiscordClient.IsConnectedAsync();
    public Task<string[]> GetServersAsync() => DiscordClient.GetServersAsync();
    public Task<string[]> GetChannelsAsync(string server) => DiscordClient.GetChannelsAsync(server);
    public Task<bool> JoinChannelAsync(string server, string channel) => DiscordClient.JoinChannel(server, channel);
    public Task LeaveChannelAsync() => DiscordClient.LeaveChannelAsync();
    public Task DeinitAsync() => DiscordClient.DeinitAsync();

    public Task SpeakAsync(string text, string voice, int vol, int speed) => DiscordClient.SpeakAsync(text, voice, vol, speed);
    public Task SpeakOnnxAsync(string text) => DiscordClient.SpeakOnnxAsync(text);
    public Task SpeakFileAsync(string path) => DiscordClient.SpeakFileAsync(path);

    public string[] GetInstalledVoices() => DiscordClient.GetInstalledVoices();

    public void Dispose() {
      DiscordClient.BotReady -= botReadyHandler;
      DiscordClient.Log -= logHandler;
      DiscordClient.Disconnected -= disconnectedHandler;
    }
  }
}
