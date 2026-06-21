using ACT_DiscordTriggers.Core.Settings;
using ACT_DiscordTriggers.Core.Ipc;
using ACT_DiscordTriggers.Core.Protocol;
using ACT_DiscordTriggers.Core.Tts;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace ACT_DiscordTriggers.Tests {
  // End-to-end ONNX synthesis across the real C# -> bridge boundary: resolves the
  // synth descriptor from PluginSettings exactly as production does
  // (OnnxSynthParams.Resolve), pushes it in SetConfig's ttsParams, sends SpeakText,
  // and confirms a non-silent 48 kHz/stereo WAV is produced. The bridge's diagnostic
  // audio sink (ACT_DT_AUDIO_SINK) is what makes the played audio observable without
  // a Discord voice channel.
  //
  // Auto-skips unless everything needed is present: a built dist/ with the native
  // addon staged (run build.ps1 with sherpa-onnx-node installed) and the voice
  // models (default E:\ai, override ACT_DT_MODELS_DIR).
  public class OnnxBridgeE2ETests {
    private static string DistDir() {
      string testDir = Path.GetDirectoryName(typeof(OnnxBridgeE2ETests).Assembly.Location);
      string solDir = Path.GetFullPath(Path.Combine(testDir, "..", "..", "..", ".."));
      return Path.Combine(solDir, "DiscordBridge-node", "dist");
    }

    private static string ModelsDir() {
      var env = Environment.GetEnvironmentVariable("ACT_DT_MODELS_DIR");
      return string.IsNullOrWhiteSpace(env) ? @"E:\ai" : env.Trim();
    }

    private static bool DistReady(string dist) =>
      File.Exists(Path.Combine(dist, "node.exe")) &&
      File.Exists(Path.Combine(dist, "bundle.js")) &&
      Directory.Exists(Path.Combine(dist, "node_modules", "sherpa-onnx-node"));

    // First installed Piper voice under the models dir, or null.
    private static OnnxVoiceInfo InstalledPiper(string modelsDir) =>
      OnnxCatalog.ByFamily(OnnxCatalog.FamilyPiper)
        .FirstOrDefault(v => OnnxCatalog.IsInstalled(v, modelsDir));

    // First installed pt-BR Kokoro voice — picked so the baked espeak lang
    // ('pt-br') is the one resolved + transported + synthesized.
    private static OnnxVoiceInfo InstalledKokoroPtBr(string modelsDir) =>
      OnnxCatalog.ByFamily(OnnxCatalog.FamilyKokoro)
        .FirstOrDefault(v => string.Equals(v.Locale, "pt_BR", StringComparison.OrdinalIgnoreCase)
                          && OnnxCatalog.IsInstalled(v, modelsDir));

    [Fact]
    public async Task SpeakText_Piper_endToEnd_resolves_descriptor_synthesizes_and_captures_wav() {
      string dist = DistDir();
      string modelsDir = ModelsDir();
      Assert.SkipUnless(DistReady(dist), "Bridge dist/ with the sherpa-onnx addon not built (build.ps1).");
      Assert.SkipUnless(Directory.Exists(modelsDir), "Voice models not found (set ACT_DT_MODELS_DIR).");
      var voice = InstalledPiper(modelsDir);
      Assert.SkipUnless(voice != null, "No installed Piper voice under the models dir.");

      // Resolve the descriptor through the production gate — not a hand-built bag.
      var settings = new PluginSettings {
        TtsEngine = "onnx", OnnxVoice = voice.Id, ModelsDir = modelsDir,
        TtsSpeed = 10, TtsThreads = 1,
      };
      var ttsParams = OnnxSynthParams.Resolve(settings);
      Assert.Equal("onnx", ttsParams["engine"]);
      Assert.Equal("piper", ttsParams["family"]);

      await RunSpeakAndCaptureAsync(dist, settings, ttsParams,
        "Pull complete. Stack for the raid wide attack.");
    }

    [Fact]
    public async Task SpeakText_Kokoro_ptBR_endToEnd_carries_baked_lang_synthesizes_and_captures_wav() {
      string dist = DistDir();
      string modelsDir = ModelsDir();
      Assert.SkipUnless(DistReady(dist), "Bridge dist/ with the sherpa-onnx addon not built (build.ps1).");
      Assert.SkipUnless(Directory.Exists(modelsDir), "Voice models not found (set ACT_DT_MODELS_DIR).");
      var voice = InstalledKokoroPtBr(modelsDir);
      Assert.SkipUnless(voice != null, "No installed pt-BR Kokoro voice under the models dir.");

      var settings = new PluginSettings {
        TtsEngine = "onnx", OnnxVoice = voice.Id, ModelsDir = modelsDir,
        TtsSpeed = 10, TtsThreads = 2,
      };
      var ttsParams = OnnxSynthParams.Resolve(settings);
      Assert.Equal("kokoro", ttsParams["family"]);
      // The crash-critical espeak lang is baked in the catalog and carried verbatim
      // across the wire — this is the path the Piper test can't exercise.
      Assert.Equal("pt-br", ttsParams["lang"]);
      Assert.Equal(voice.Sid.ToString(System.Globalization.CultureInfo.InvariantCulture), ttsParams["sid"]);

      await RunSpeakAndCaptureAsync(dist, settings, ttsParams,
        "Cuidado, ataque pesado chegando. Use a mitigação agora e saia do fogo.");
    }

    // Spawn the bridge with the audio sink, push the resolved config + ttsParams,
    // SpeakText, and assert exactly one non-silent 48k/stereo WAV is captured.
    private static async Task RunSpeakAndCaptureAsync(
        string dist, PluginSettings settings, Dictionary<string, string> ttsParams, string text) {
      string sinkDir = Path.Combine(Path.GetTempPath(), "act-cs-e2e-" + Guid.NewGuid().ToString("N"));
      Directory.CreateDirectory(sinkDir);
      using var bp = new BridgeProcess {
        ExtraEnv = new Dictionary<string, string> { ["ACT_DT_AUDIO_SINK"] = sinkDir },
      };
      try {
        var pipe = await bp.StartAndConnectAsync(dist, TimeSpan.FromSeconds(15));
        using var pc = new PipeClient(pipe);
        pc.Start();

        var hello = await pc.SendAsync<BridgeResponse<HelloData>>(
          new HelloRequest { ProtocolVersion = ProtocolConstants.Version }, TimeSpan.FromSeconds(5));
        Assert.True(hello.Ok);

        var cfg = await pc.SendAsync<BridgeResponse>(
          new SetConfigRequest<PluginSettings> { Config = settings, TtsParams = ttsParams },
          TimeSpan.FromSeconds(5));
        Assert.True(cfg.Ok);

        var spoke = await pc.SendAsync<BridgeResponse>(
          new SpeakTextRequest { Text = text }, TimeSpan.FromSeconds(30));
        Assert.True(spoke.Ok, $"SpeakText accept failed: {spoke.Error}");

        // The Result acks acceptance; synthesis runs detached on the bridge, so the
        // captured WAV lands shortly after. Poll the sink until it appears.
        var wavs = await WaitForWavsAsync(sinkDir, 1, TimeSpan.FromSeconds(30));
        Assert.Single(wavs);
        AssertNonSilent48kStereoWav(wavs[0]);
      } finally {
        try { await pc_Shutdown(bp); } catch { }
        try { Directory.Delete(sinkDir, true); } catch { }
      }
    }

    [Fact]
    public async Task SpeakText_withUninstalledVoice_resolves_empty_and_bridge_skips() {
      string dist = DistDir();
      Assert.SkipUnless(DistReady(dist), "Bridge dist/ with the sherpa-onnx addon not built (build.ps1).");

      // An ONNX voice that is selected but not installed: the gate yields an empty
      // bag, so the bridge loads nothing and SpeakText must skip (not crash).
      var settings = new PluginSettings {
        TtsEngine = "onnx", OnnxVoice = "vits-piper-en_US-amy-medium",
        ModelsDir = Path.Combine(Path.GetTempPath(), "act-empty-" + Guid.NewGuid().ToString("N")),
      };
      var ttsParams = OnnxSynthParams.Resolve(settings);
      Assert.Empty(ttsParams);

      using var bp = new BridgeProcess();
      try {
        var pipe = await bp.StartAndConnectAsync(dist, TimeSpan.FromSeconds(15));
        using var pc = new PipeClient(pipe);
        pc.Start();
        await pc.SendAsync<BridgeResponse<HelloData>>(
          new HelloRequest { ProtocolVersion = ProtocolConstants.Version }, TimeSpan.FromSeconds(5));
        await pc.SendAsync<BridgeResponse>(
          new SetConfigRequest<PluginSettings> { Config = settings, TtsParams = ttsParams },
          TimeSpan.FromSeconds(5));

        var spoke = await pc.SendAsync<BridgeResponse>(
          new SpeakTextRequest { Text = "hello" }, TimeSpan.FromSeconds(10));
        Assert.False(spoke.Ok);

        // Bridge is still responsive.
        var ic = await pc.SendAsync<BridgeResponse<ConnectedData>>(
          new IsConnectedRequest(), TimeSpan.FromSeconds(3));
        Assert.False(ic.Data.Connected);
      } finally {
        try { await pc_Shutdown(bp); } catch { }
      }
    }

    // Poll the audio sink until at least `count` WAVs are present or the timeout
    // elapses (then return whatever's there so the caller's assert reports it).
    private static async Task<string[]> WaitForWavsAsync(string sinkDir, int count, TimeSpan timeout) {
      var deadline = DateTime.UtcNow + timeout;
      for (; ; ) {
        var wavs = Directory.GetFiles(sinkDir, "*.wav");
        if (wavs.Length >= count || DateTime.UtcNow >= deadline) return wavs;
        await Task.Delay(50);
      }
    }

    private static async Task pc_Shutdown(BridgeProcess bp) {
      // Best-effort: ask the bridge to exit, then wait. The pipe may already be gone.
      await bp.WaitForExitAsync(TimeSpan.FromSeconds(1));
      bp.Kill();
      await bp.WaitForExitAsync(TimeSpan.FromSeconds(5));
    }

    // Validate the captured clip is a 48 kHz, 16-bit, stereo WAV with real signal.
    private static void AssertNonSilent48kStereoWav(string path) {
      byte[] b = File.ReadAllBytes(path);
      Assert.True(b.Length > 44, "WAV smaller than its header");
      Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(b, 0, 4));
      Assert.Equal("WAVE", System.Text.Encoding.ASCII.GetString(b, 8, 4));
      int channels = BitConverter.ToUInt16(b, 22);
      int sampleRate = (int)BitConverter.ToUInt32(b, 24);
      int bits = BitConverter.ToUInt16(b, 34);
      Assert.Equal(2, channels);
      Assert.Equal(48000, sampleRate);
      Assert.Equal(16, bits);

      // RMS over the left channel of the PCM body must be well above silence.
      double sum = 0; int n = 0;
      for (int i = 44; i + 4 <= b.Length; i += 4) {
        short l = BitConverter.ToInt16(b, i);
        double s = l / 32768.0;
        sum += s * s; n++;
      }
      double rms = n > 0 ? Math.Sqrt(sum / n) : 0;
      Assert.True(rms > 0.01, $"captured audio is ~silent (rms={rms:F4})");
      Assert.True(n > 48000 / 2, $"captured clip too short ({n} frames)");
    }
  }
}
