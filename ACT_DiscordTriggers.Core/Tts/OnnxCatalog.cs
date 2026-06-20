using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ACT_DiscordTriggers.Core.Tts {
  /// <summary>
  /// The curated ONNX voice catalog: a read-only view over the embedded
  /// <c>onnx-voices.json</c> resource plus the install-state helpers the UI uses
  /// to mark a voice ✓ (installed) or ⬇ (needs download). Pure C# / data — no
  /// bridge round-trip — so it works whether or not Discord is connected.
  /// Update the catalog by regenerating the JSON (<c>tools/gen-onnx-catalog</c>),
  /// not by editing code.
  /// </summary>
  public static class OnnxCatalog {
    public const string FamilyPiper = "piper";
    public const string FamilyKokoro = "kokoro";

    /// <summary>Default models root when <c>ModelsDir</c> is left empty.</summary>
    public const string ModelsDirAppFolder = "ACT_DiscordTriggers";

    private static readonly Lazy<IReadOnlyList<OnnxVoiceInfo>> _all =
      new Lazy<IReadOnlyList<OnnxVoiceInfo>>(LoadEmbedded);

    /// <summary>Every catalog voice, in the curated display order from the resource.</summary>
    public static IReadOnlyList<OnnxVoiceInfo> All => _all.Value;

    /// <summary>Voices of one family (<see cref="FamilyPiper"/> / <see cref="FamilyKokoro"/>).</summary>
    public static IEnumerable<OnnxVoiceInfo> ByFamily(string family) =>
      All.Where(v => string.Equals(v.Family, family, StringComparison.OrdinalIgnoreCase));

    /// <summary>Distinct locales available in a family, in first-seen (curated) order.</summary>
    public static IEnumerable<string> Locales(string family) =>
      ByFamily(family).Select(v => v.Locale).Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>Look up a voice by its <see cref="OnnxVoiceInfo.Id"/>; null if absent.</summary>
    public static OnnxVoiceInfo Find(string id) =>
      string.IsNullOrEmpty(id)
        ? null
        : All.FirstOrDefault(v => string.Equals(v.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Resolve the configured models directory to an absolute path. An empty
    /// setting means "use <c>%APPDATA%\ACT_DiscordTriggers\models</c>"; a set
    /// value is trimmed and has environment variables expanded.
    /// </summary>
    public static string ResolveModelsDir(string settingValue) {
      if (!string.IsNullOrWhiteSpace(settingValue))
        return Environment.ExpandEnvironmentVariables(settingValue.Trim());
      var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
      return Path.Combine(appData, ModelsDirAppFolder, "models");
    }

    /// <summary>
    /// True when <paramref name="voice"/>'s model is present under
    /// <paramref name="modelsDir"/>. The model lives in
    /// <c>&lt;modelsDir&gt;/&lt;DownloadId&gt;/</c>; a Piper voice needs any
    /// <c>*.onnx</c>, a Kokoro pack needs <c>model.onnx</c> + <c>voices.bin</c>.
    /// Downloads land atomically (temp dir, then move) so a present directory
    /// with the model file means a complete install.
    /// </summary>
    public static bool IsInstalled(OnnxVoiceInfo voice, string modelsDir) {
      if (voice == null || string.IsNullOrEmpty(modelsDir)) return false;
      var dir = Path.Combine(modelsDir, voice.DownloadId);
      if (!Directory.Exists(dir)) return false;
      if (string.Equals(voice.Family, FamilyKokoro, StringComparison.OrdinalIgnoreCase))
        return File.Exists(Path.Combine(dir, "model.onnx"))
            && File.Exists(Path.Combine(dir, "voices.bin"));
      return Directory.EnumerateFiles(dir, "*.onnx", SearchOption.TopDirectoryOnly).Any();
    }

    private sealed class CatalogFile {
      [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; }
      [JsonPropertyName("voices")] public List<OnnxVoiceInfo> Voices { get; set; }
    }

    private static IReadOnlyList<OnnxVoiceInfo> LoadEmbedded() {
      var asm = typeof(OnnxCatalog).Assembly;
      var name = asm.GetManifestResourceNames()
        .FirstOrDefault(n => n.EndsWith("onnx-voices.json", StringComparison.OrdinalIgnoreCase));
      if (name == null)
        throw new InvalidOperationException("Embedded onnx-voices.json resource not found.");
      using (var stream = asm.GetManifestResourceStream(name))
      using (var reader = new StreamReader(stream)) {
        var json = reader.ReadToEnd();
        var file = JsonSerializer.Deserialize<CatalogFile>(json);
        return (file?.Voices ?? new List<OnnxVoiceInfo>()).AsReadOnly();
      }
    }
  }
}
