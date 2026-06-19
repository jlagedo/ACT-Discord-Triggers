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
    event Action<string> Log;

    Task ConnectAsync(PluginSettings config);
    Task SetConfigAsync(PluginSettings config);
    Task<bool> IsConnectedAsync();
    Task<string[]> GetServersAsync();
    Task<string[]> GetChannelsAsync(string server);
    Task<bool> JoinChannelAsync(string server, string channel);
    Task LeaveChannelAsync();
    Task DeinitAsync();

    void Speak(string text, string voice, int vol, int speed);
    void SpeakFile(string path);

    string[] GetInstalledVoices();
  }
}
