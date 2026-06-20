using System.Text.Json.Serialization;
using System.Xml.Serialization;

namespace ACT_DiscordTriggers.Core.Settings {
  /// <summary>
  /// The plugin's settings model — the single source of truth for everything that
  /// used to live directly on the WinForms controls (persisted via ACT's
  /// <c>SettingsSerializer</c>). It is a plain POCO with no WinForms / ACT
  /// dependency so it can be unit-tested and, later, bound from a WPF UI.
  ///
  /// Defaults below are the single source of truth for a fresh install; the WPF
  /// view binds to the ViewModel, which seeds its initial values from this POCO.
  /// </summary>
  [XmlRoot("DiscordTriggersSettings")]
  public class PluginSettings {
    /// <summary>
    /// Schema version of the settings file. Bumped whenever the on-disk shape
    /// changes incompatibly; <see cref="Migrations.SettingsMigrator"/> upgrades
    /// older files to this version. v1 is the first POCO format (the legacy
    /// control-keyed ACT format is treated as the pre-schema "v0").
    /// </summary>
    public const int CurrentSchemaVersion = 2;

    // Valid ranges for the tunable integer settings — the single source of truth the
    // ViewModel clamps loaded/entered values to (and that the WinForms-era sliders used).
    public const int TtsVolumeMin = 0, TtsVolumeMax = 20;
    public const int TtsSpeedMin = 0, TtsSpeedMax = 20;
    public const int FxChanceMin = 0, FxChanceMax = 100;
    public const int NormalizeTargetMin = 12, NormalizeTargetMax = 30;
    public const int AudioQualityIndexMin = 0, AudioQualityIndexMax = 2; // 0=Low, 1=Medium, 2=High

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

    // --- Text-to-speech (SAPI synthesized in-process; the bridge ignores TtsVoice/TtsVolume) ---
    /// <summary>Installed-voice name; empty selects the first available voice.</summary>
    [JsonPropertyName("ttsVoice")] public string TtsVoice { get; set; } = "";
    [JsonPropertyName("ttsVolume")] public int TtsVolume { get; set; } = 10;   // slider 0..20; SAPI only
    [JsonPropertyName("ttsSpeed")] public int TtsSpeed { get; set; } = 10;    // slider 0..20; both engines

    // --- ONNX TTS (synthesized in the bridge; the bridge reads these from SetConfig) ---
    [JsonPropertyName("ttsEngine")] public string TtsEngine { get; set; } = "sapi";    // "sapi" | "onnx"
    [JsonPropertyName("onnxFamily")] public string OnnxFamily { get; set; } = "piper";  // "piper" | "kokoro"
    [JsonPropertyName("onnxVoice")] public string OnnxVoice { get; set; } = "vits-piper-pt_BR-faber-medium"; // catalog id
    [JsonPropertyName("ttsThreads")] public int TtsThreads { get; set; } = 4;           // sherpa numThreads
    [JsonPropertyName("modelsDir")] public string ModelsDir { get; set; } = "";         // empty ⇒ %APPDATA%\ACT_DiscordTriggers\models

    // --- Effects & leveling (interpreted by the bridge) ---
    [JsonPropertyName("randomFx")] public bool RandomFx { get; set; } = false;
    [JsonPropertyName("fxChance")] public int FxChance { get; set; } = 25;            // 0..100 (%)
    [JsonPropertyName("normalize")] public bool Normalize { get; set; } = true;
    [JsonPropertyName("normalizeTarget")] public int NormalizeTarget { get; set; } = 20;     // 12..30, positive dB magnitude
    [JsonPropertyName("audioQualityIndex")] public int AudioQualityIndex { get; set; } = 1;  // 0=Low, 1=Medium, 2=High
  }
}
