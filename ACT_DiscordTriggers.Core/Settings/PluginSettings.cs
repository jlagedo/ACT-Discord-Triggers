using System.Text.Json.Serialization;
using System.Xml.Serialization;

namespace ACT_DiscordTriggers.Core.Settings {
  /// <summary>
  /// The plugin's settings model — the single source of truth for everything that
  /// used to live directly on the WinForms controls (persisted via ACT's
  /// <c>SettingsSerializer</c>). It is a plain POCO with no WinForms / ACT
  /// dependency so it can be unit-tested and, later, bound from a WPF UI.
  ///
  /// Defaults below MUST match the control defaults in <c>DiscordTriggersView.InitializeComponent</c>
  /// so a fresh install behaves exactly as it did before this refactor.
  /// </summary>
  [XmlRoot("DiscordTriggersSettings")]
  public class PluginSettings {
    /// <summary>
    /// Schema version of the settings file. Bumped whenever the on-disk shape
    /// changes incompatibly; <see cref="Migrations.SettingsMigrator"/> upgrades
    /// older files to this version. v1 is the first POCO format (the legacy
    /// control-keyed ACT format is treated as the pre-schema "v0").
    /// </summary>
    public const int CurrentSchemaVersion = 1;

    // [JsonPropertyName] sets the wire names the node bridge reads (this whole POCO
    // is sent verbatim as the SetConfig payload). XML attributes drive on-disk
    // persistence and are independent of the JSON wire shape.
    [XmlAttribute]
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    // --- Connection ---
    [JsonPropertyName("botToken")] public string BotToken { get; set; } = "";
    [JsonPropertyName("botStatus")] public string BotStatus { get; set; } = "Playing with ACT Triggers";
    [JsonPropertyName("autoConnect")] public bool AutoConnect { get; set; } = false;

    // --- Text-to-speech (synthesized in-process; the bridge ignores these) ---
    /// <summary>Installed-voice name; empty selects the first available voice.</summary>
    [JsonPropertyName("ttsVoice")] public string TtsVoice { get; set; } = "";
    [JsonPropertyName("ttsVolume")] public int TtsVolume { get; set; } = 10;   // slider 0..20
    [JsonPropertyName("ttsSpeed")] public int TtsSpeed { get; set; } = 10;    // slider 0..20

    // --- Effects & leveling (interpreted by the bridge) ---
    [JsonPropertyName("randomFx")] public bool RandomFx { get; set; } = false;
    [JsonPropertyName("fxChance")] public int FxChance { get; set; } = 25;            // 0..100 (%)
    [JsonPropertyName("normalize")] public bool Normalize { get; set; } = true;
    [JsonPropertyName("normalizeTarget")] public int NormalizeTarget { get; set; } = 20;     // 12..30, positive dB magnitude
    [JsonPropertyName("audioQualityIndex")] public int AudioQualityIndex { get; set; } = 1;  // 0=Low, 1=Medium, 2=High
  }
}
