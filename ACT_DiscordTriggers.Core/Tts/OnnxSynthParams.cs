using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using ACT_DiscordTriggers.Core.Settings;

namespace ACT_DiscordTriggers.Core.Tts {
  /// <summary>
  /// Resolves the ONNX synthesis descriptor sent to the bridge in SetConfig's
  /// extensible <c>ttsParams</c> bag. This is where the safety gate lives: it
  /// emits a voice only when the engine is ONNX <b>and</b> the selected catalog
  /// voice is actually installed, so the bridge never receives an unknown voice or
  /// the crash-critical espeak <c>lang</c> for a model that isn't on disk. The
  /// <c>lang</c> itself is read from the catalog (baked at generation), never
  /// computed here. An empty map means "no ONNX voice" — the bridge loads nothing.
  /// </summary>
  public static class OnnxSynthParams {
    public const string EngineOnnx = "onnx";

    public static Dictionary<string, string> Resolve(PluginSettings settings) {
      var p = new Dictionary<string, string>();
      if (settings == null) return p;
      if (!string.Equals(settings.TtsEngine, EngineOnnx, StringComparison.OrdinalIgnoreCase))
        return p;

      var voice = OnnxCatalog.Find(settings.OnnxVoice);
      if (voice == null) return p;

      var modelsDir = OnnxCatalog.ResolveModelsDir(settings.ModelsDir);
      if (!OnnxCatalog.IsInstalled(voice, modelsDir)) return p;

      var inv = CultureInfo.InvariantCulture;
      p["engine"] = EngineOnnx;
      p["family"] = voice.Family;
      p["modelDir"] = Path.Combine(modelsDir, voice.DownloadId);
      p["sid"] = voice.Sid.ToString(inv);
      p["lang"] = voice.Lang ?? "";
      p["speed"] = settings.TtsSpeed.ToString(inv);
      p["threads"] = settings.TtsThreads.ToString(inv);
      // Baked per-voice loudness, emitted only when measured (a real dBFS value
      // is negative). The bridge levels the clip to the user's target with these
      // fixed values instead of scanning the whole buffer; absent them it falls
      // back to a runtime RMS measure. Valid dBFS is < 0, so a non-negative value
      // is the "unmeasured" sentinel and is not forwarded.
      if (voice.RmsDbfs < 0 && voice.PeakDbfs < 0) {
        p["rms"] = voice.RmsDbfs.ToString(inv);
        p["peak"] = voice.PeakDbfs.ToString(inv);
      }
      return p;
    }
  }
}
