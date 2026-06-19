using System.Xml.Serialization;

namespace ACT_DiscordTriggers.Settings {
  /// <summary>
  /// The plugin's settings model — the single source of truth for everything that
  /// used to live directly on the WinForms controls (persisted via ACT's
  /// <c>SettingsSerializer</c>). It is a plain POCO with no WinForms / ACT
  /// dependency so it can be unit-tested and, later, bound from a WPF UI.
  ///
  /// Defaults below MUST match the control defaults in <c>DiscordPlugin.InitializeComponent</c>
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

    [XmlAttribute]
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    // --- Connection ---
    public string BotToken { get; set; } = "";
    public string BotStatus { get; set; } = "Playing with ACT Triggers";
    public bool AutoConnect { get; set; } = false;

    // --- Text-to-speech ---
    /// <summary>Installed-voice name; empty selects the first available voice.</summary>
    public string TtsVoice { get; set; } = "";
    public int TtsVolume { get; set; } = 10;   // slider 0..20
    public int TtsSpeed { get; set; } = 10;    // slider 0..20

    // --- Effects & leveling ---
    public bool RandomFx { get; set; } = false;
    public int FxChance { get; set; } = 25;            // 0..100 (%)
    public bool Normalize { get; set; } = true;
    public int NormalizeTarget { get; set; } = 20;     // 12..30, positive dB magnitude
    public int AudioQualityIndex { get; set; } = 1;    // 0=Low, 1=Medium, 2=High
  }
}
