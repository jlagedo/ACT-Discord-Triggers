using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;

namespace ACT_DiscordTriggers.Core.Settings {
  /// <summary>
  /// One-time bootstrap that converts the legacy ACT <c>SettingsSerializer</c> format
  /// (the pre-schema "v0") into a <see cref="PluginSettings"/> (schema v1).
  ///
  /// The legacy file looks like:
  /// <code>
  /// &lt;Config&gt;
  ///   &lt;SettingsSerializer&gt;
  ///     &lt;TextBox  Name="txtToken"  Value="..." /&gt;
  ///     &lt;TrackBar Name="sliderTTSVol" Value="10" /&gt;
  ///     &lt;ComboBox Name="cmbAudioQuality" Value="High (128 kbps)" /&gt;
  ///     ...
  ///   &lt;/SettingsSerializer&gt;
  /// &lt;/Config&gt;
  /// </code>
  /// Elements are named by control TYPE; identity is the <c>Name</c> attribute and the
  /// value is the <c>Value</c> attribute. Parsing is deliberately defensive: any
  /// missing or malformed entry falls back to the POCO default rather than throwing.
  /// </summary>
  public static class LegacyConfigImporter {
    /// <summary>True if the document root is the legacy ACT format.</summary>
    public static bool IsLegacy(XDocument doc) {
      var root = doc?.Root;
      return root != null
             && root.Name.LocalName == "Config"
             && root.Element("SettingsSerializer") != null;
    }

    /// <summary>
    /// GOTCHA: ACT persists a DropDownList ComboBox by its selected TEXT, not its
    /// index. The audio-quality combo stores e.g. "High (128 kbps)". Map text → index;
    /// anything unrecognised falls back to Medium (1), the historical default.
    /// </summary>
    private static int AudioQualityTextToIndex(string text) {
      if (string.IsNullOrEmpty(text)) return 1;
      if (text.StartsWith("Low", StringComparison.OrdinalIgnoreCase)) return 0;
      if (text.StartsWith("Medium", StringComparison.OrdinalIgnoreCase)) return 1;
      if (text.StartsWith("High", StringComparison.OrdinalIgnoreCase)) return 2;
      return 1;
    }

    public static PluginSettings Import(XDocument legacy) {
      var s = new PluginSettings();
      if (!IsLegacy(legacy)) return s;

      // control Name -> Value, last one wins (the file lists each control once).
      var values = new Dictionary<string, string>(StringComparer.Ordinal);
      foreach (var el in legacy.Root.Element("SettingsSerializer").Elements()) {
        var name = (string)el.Attribute("Name");
        if (string.IsNullOrEmpty(name)) continue;
        values[name] = (string)el.Attribute("Value") ?? "";
      }

      s.BotToken = GetString(values, "txtToken", s.BotToken);
      s.BotStatus = GetString(values, "txtBotStatus", s.BotStatus);
      s.AutoConnect = GetBool(values, "chkAutoConnect", s.AutoConnect);

      // cmbTTS was never persisted by the legacy format, so it will be absent here —
      // TtsVoice stays "" (→ first installed voice), matching old behaviour.
      s.TtsVoice = GetString(values, "cmbTTS", s.TtsVoice);
      s.TtsVolume = GetInt(values, "sliderTTSVol", s.TtsVolume);
      s.TtsSpeed = GetInt(values, "sliderTTSSpeed", s.TtsSpeed);

      s.RandomFx = GetBool(values, "chkRandomFx", s.RandomFx);
      s.FxChance = GetInt(values, "sliderFxChance", s.FxChance);
      s.Normalize = GetBool(values, "chkNormalize", s.Normalize);
      s.NormalizeTarget = GetInt(values, "sliderNormalizeTarget", s.NormalizeTarget);

      if (values.TryGetValue("cmbAudioQuality", out var aq))
        s.AudioQualityIndex = AudioQualityTextToIndex(aq);

      s.SchemaVersion = PluginSettings.CurrentSchemaVersion;
      return s;
    }

    private static string GetString(IDictionary<string, string> v, string key, string fallback)
      => v.TryGetValue(key, out var s) ? s : fallback;

    private static int GetInt(IDictionary<string, string> v, string key, int fallback)
      => v.TryGetValue(key, out var s) && int.TryParse(s, out var n) ? n : fallback;

    private static bool GetBool(IDictionary<string, string> v, string key, bool fallback)
      => v.TryGetValue(key, out var s) && bool.TryParse(s, out var b) ? b : fallback;
  }
}
