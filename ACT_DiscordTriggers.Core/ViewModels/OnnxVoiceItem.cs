using CommunityToolkit.Mvvm.ComponentModel;
using ACT_DiscordTriggers.Core.Tts;

namespace ACT_DiscordTriggers.Core.ViewModels {
  /// <summary>
  /// A catalog voice as the picker sees it: the underlying <see cref="OnnxVoiceInfo"/>
  /// plus its install-state and the strings the dropdown renders (name, tier, locale,
  /// closed-picker label). <see cref="Installed"/> is observable so a finished download
  /// flips the row's ✓ and clears its size tag live.
  /// </summary>
  public sealed partial class OnnxVoiceItem : ObservableObject {
    public OnnxVoiceItem(OnnxVoiceInfo info, bool installed) {
      Info = info;
      this.installed = installed;
    }

    public OnnxVoiceInfo Info { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SizeText))]
    private bool installed;

    public string Id => Info.Id;
    public bool Recommended => Info.Recommended;

    /// <summary>Voice name only (e.g. <c>Amy</c>) — the dropdown row's primary text.</summary>
    public string Name => Info.DisplayName;

    /// <summary>Quality tier (e.g. <c>medium</c>, <c>high</c>, <c>B-</c>) — used by search.</summary>
    public string Tier => Info.Quality;

    /// <summary>Display locale (hyphenated, e.g. <c>pt-BR</c>) — the dropdown's group key.</summary>
    public string Locale => Info.Locale.Replace('_', '-');

    /// <summary>Closed-picker label: locale · name (e.g. <c>en-US · Amy</c>).</summary>
    public string Label => Locale + " · " + Info.DisplayName;

    /// <summary>Muted quality tier suffix shown after the name in a row (e.g. <c>· medium</c>).</summary>
    public string TierLabel => "· " + Info.Quality;

    /// <summary>Right-hand size tag for a not-yet-installed voice; empty once installed.</summary>
    public string SizeText => Installed ? "" : Info.SizeMB + " MB";
  }
}
