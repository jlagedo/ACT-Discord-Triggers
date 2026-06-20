using System.Text.Json.Serialization;

namespace ACT_DiscordTriggers.Core.Tts {
  /// <summary>
  /// One entry in the curated ONNX voice catalog (a row of the embedded
  /// <c>onnx-voices.json</c> resource). Pure data: identity, where to download
  /// it, and what to show in the picker. The catalog is generated — see
  /// <c>tools/gen-onnx-catalog/gen.mjs</c> — so this shape mirrors that script's
  /// output and must not drift from it.
  /// </summary>
  public sealed class OnnxVoiceInfo {
    /// <summary>Stable catalog key and the value persisted as <c>OnnxVoice</c>.
    /// Piper: equals <see cref="DownloadId"/> (e.g. <c>vits-piper-pt_BR-faber-medium</c>);
    /// Kokoro: <c>kokoro-&lt;sid&gt;</c>.</summary>
    [JsonPropertyName("id")] public string Id { get; set; } = "";

    /// <summary><c>piper</c> or <c>kokoro</c> — the Quality toggle's two families.</summary>
    [JsonPropertyName("family")] public string Family { get; set; } = "";

    /// <summary>Piper-style locale code, e.g. <c>en_US</c>, <c>pt_BR</c>.</summary>
    [JsonPropertyName("locale")] public string Locale { get; set; } = "";

    /// <summary>Friendly group label shown as the picker's section header
    /// (language name + hyphenated locale, e.g. <c>Portuguese (pt-br)</c>).</summary>
    [JsonPropertyName("localeName")] public string LocaleName { get; set; } = "";

    /// <summary>Human label for the dropdown (e.g. <c>Faber</c>, <c>Heart (female)</c>).</summary>
    [JsonPropertyName("displayName")] public string DisplayName { get; set; } = "";

    /// <summary>Piper tier (<c>medium</c>/<c>high</c>) or Kokoro letter grade (<c>A</c>, <c>B-</c>…).</summary>
    [JsonPropertyName("quality")] public string Quality { get; set; } = "";

    /// <summary>Speaker index in the model. Piper voices use 0; Kokoro uses the
    /// speaker's index in the multi-lang pack.</summary>
    [JsonPropertyName("sid")] public int Sid { get; set; }

    /// <summary>espeak-ng voice id passed to the bridge per synthesis, baked at
    /// catalog-build time (an unknown one hard-exits sherpa). Empty for Piper
    /// (the model carries its own) and for Kokoro American English (uses the
    /// lexicon); <c>en-gb-x-rp</c> / <c>pt-br</c> for the other Kokoro locales.</summary>
    [JsonPropertyName("lang")] public string Lang { get; set; } = "";

    /// <summary>Release asset / model-directory name. Every Kokoro voice shares the
    /// single pack id <c>kokoro-multi-lang-v1_0</c>; each Piper voice has its own.
    /// The download URL is <c>…/tts-models/&lt;DownloadId&gt;.tar.bz2</c>.</summary>
    [JsonPropertyName("downloadId")] public string DownloadId { get; set; } = "";

    /// <summary>Approximate on-disk model size in MB, shown beside not-yet-installed voices.</summary>
    [JsonPropertyName("sizeMB")] public int SizeMB { get; set; }

    /// <summary>Flags a sensible default for the locale so undecided users have a pick.</summary>
    [JsonPropertyName("recommended")] public bool Recommended { get; set; }
  }
}
