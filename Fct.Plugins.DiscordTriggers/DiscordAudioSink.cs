using System.Threading;
using System.Threading.Tasks;
using ACT_DiscordTriggers.Core.ViewModels;
using Fct.Abstractions;

namespace Fct.Plugins.DiscordTriggers {
  /// <summary>
  /// The host audio destination that routes Speak/Play through the Discord bridge. Registered as a
  /// high-priority <em>terminal</em> sink while output is live, so the host's built-in speaker sink
  /// never fires — the additive-model equivalent of the old ACT PlayTtsMethod/PlaySoundMethod hijack.
  /// </summary>
  internal sealed class DiscordAudioSink : IAudioSink {
    private readonly DiscordTriggersViewModel vm;

    public DiscordAudioSink(DiscordTriggersViewModel vm) => this.vm = vm;

    // AudioOptions.Voice/Rate are ignored by design — the plugin routes through its OWN configured
    // engine/voice, not the host's per-request options.
    public ValueTask SpeakAsync(string text, AudioOptions options, CancellationToken ct)
      => new(vm.SpeakTextCoreAsync(text));

    // volume is dropped, matching today's SpeakSoundFile behaviour; honouring it would need a
    // per-play gain across the wire protocol (a deliberate non-goal for this port).
    public ValueTask PlayAsync(string filePath, int volume, CancellationToken ct)
      => new(vm.SpeakFileCoreAsync(filePath));
  }
}
