using System;
using System.IO;
using System.Linq;
using ACT_DiscordTriggers.Core.Settings;
using ACT_DiscordTriggers.Core.Tts;
using Xunit;

namespace ACT_DiscordTriggers.Tests {
  // OnnxSynthParams is the C#-side safety gate: it emits the bridge synth descriptor
  // only for an installed ONNX voice, so the bridge never receives an unknown voice
  // or the crash-critical espeak lang for a model that isn't on disk.
  public class OnnxSynthParamsTests {
    private sealed class TempDir : IDisposable {
      public string Path { get; }
      public TempDir() {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "act-dt-syn-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
      }
      public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
    }

    private static void InstallPiper(string modelsDir, OnnxVoiceInfo v) {
      var dir = System.IO.Path.Combine(modelsDir, v.DownloadId);
      Directory.CreateDirectory(dir);
      File.WriteAllText(System.IO.Path.Combine(dir, "model.onnx"), "x");
    }

    private static void InstallKokoro(string modelsDir, OnnxVoiceInfo v) {
      var dir = System.IO.Path.Combine(modelsDir, v.DownloadId);
      Directory.CreateDirectory(dir);
      File.WriteAllText(System.IO.Path.Combine(dir, "model.onnx"), "x");
      File.WriteAllText(System.IO.Path.Combine(dir, "voices.bin"), "x");
    }

    [Fact]
    public void Resolve_Sapi_ReturnsEmptyBag() {
      var p = OnnxSynthParams.Resolve(new PluginSettings { TtsEngine = "sapi" });
      Assert.Empty(p);
    }

    [Fact]
    public void Resolve_Onnx_NotInstalled_ReturnsEmptyBag() {
      using (var t = new TempDir()) {
        var voice = OnnxCatalog.ByFamily(OnnxCatalog.FamilyPiper).First();
        var p = OnnxSynthParams.Resolve(new PluginSettings {
          TtsEngine = "onnx", OnnxVoice = voice.Id, ModelsDir = t.Path,
        });
        Assert.Empty(p); // nothing on disk -> the bridge loads nothing
      }
    }

    [Fact]
    public void Resolve_Onnx_PiperInstalled_EmitsDescriptor() {
      using (var t = new TempDir()) {
        var voice = OnnxCatalog.ByFamily(OnnxCatalog.FamilyPiper).First();
        InstallPiper(t.Path, voice);

        var p = OnnxSynthParams.Resolve(new PluginSettings {
          TtsEngine = "onnx", OnnxVoice = voice.Id, ModelsDir = t.Path,
          TtsSpeed = 12, TtsThreads = 2,
        });

        Assert.Equal("onnx", p["engine"]);
        Assert.Equal("piper", p["family"]);
        Assert.Equal(Path.Combine(t.Path, voice.DownloadId), p["modelDir"]);
        Assert.Equal("0", p["sid"]);
        Assert.Equal("", p["lang"]);   // Piper carries its own espeak voice
        Assert.Equal("12", p["speed"]);
        Assert.Equal("2", p["threads"]);
      }
    }

    [Fact]
    public void Resolve_Onnx_KokoroInstalled_CarriesBakedLang() {
      using (var t = new TempDir()) {
        var voice = OnnxCatalog.ByFamily(OnnxCatalog.FamilyKokoro)
          .First(v => string.Equals(v.Locale, "pt_BR", StringComparison.OrdinalIgnoreCase));
        InstallKokoro(t.Path, voice);

        var p = OnnxSynthParams.Resolve(new PluginSettings {
          TtsEngine = "onnx", OnnxVoice = voice.Id, ModelsDir = t.Path,
        });

        Assert.Equal("kokoro", p["family"]);
        Assert.Equal("pt-br", p["lang"]);
        Assert.Equal(voice.Sid.ToString(), p["sid"]);
        Assert.Equal(Path.Combine(t.Path, voice.DownloadId), p["modelDir"]);
      }
    }

    [Fact]
    public void Resolve_Onnx_UnknownVoiceId_ReturnsEmptyBag() {
      using (var t = new TempDir()) {
        var p = OnnxSynthParams.Resolve(new PluginSettings {
          TtsEngine = "onnx", OnnxVoice = "no-such-voice", ModelsDir = t.Path,
        });
        Assert.Empty(p);
      }
    }
  }
}
