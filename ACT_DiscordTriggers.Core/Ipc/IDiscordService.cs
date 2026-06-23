using System;
using System.Threading.Tasks;
using ACT_DiscordTriggers.Core.Settings;

namespace ACT_DiscordTriggers.Core.Ipc {
  // The slice of the Discord/bridge facade the ViewModel uses. Exists so the VM
  // depends on an interface (not the static DiscordClient): the production adapter
  // DiscordClientService forwards to DiscordClient, while tests inject a fake.
  // References only PluginSettings + BCL, so it source-links into the test project.
  public interface IDiscordService {
    event Action BotReady;
    event Action<string, LogLevel> Log;
    // Raised when the bridge connection is lost (clean teardown, bridge exit, or a
    // broken pipe), so the UI can revert to a disconnected state.
    event Action Disconnected;

    Task ConnectAsync(PluginSettings config);
    // Bring the bridge up in local-output mode: spawn + configure, no Discord login.
    // The config's OutputMode="local" makes the bridge play on the local device.
    // Returns whether the local device actually came up (the bridge confirms it on
    // SetConfig), so callers route audio only when there is something live to play it.
    Task<bool> StartLocalAsync(PluginSettings config);
    Task SetConfigAsync(PluginSettings config);
    Task<bool> IsConnectedAsync();
    Task<string[]> GetServersAsync();
    Task<string[]> GetChannelsAsync(string server);
    Task<bool> JoinChannelAsync(string server, string channel);
    Task LeaveChannelAsync();
    Task DeinitAsync();

    // Playback is awaitable (not blocking, not fire-and-forget): the caller awaits
    // so ACT's callout thread is never parked, while failures stay observable.
    Task SpeakAsync(string text, string voice, int vol, int speed);
    Task SpeakOnnxAsync(string text);
    Task SpeakFileAsync(string path);

    string[] GetInstalledVoices();
  }
}
