using System;
using System.IO;
using System.Linq;
using ACT_DiscordTriggers.Core.Tts;
using Xunit;

namespace ACT_DiscordTriggers.Tests {
  public class OnnxCatalogTests {
    private sealed class TempDir : IDisposable {
      public string Path { get; }
      public TempDir() {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "act-dt-onnx-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
      }
      public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
    }

    [Fact]
    public void Catalog_LoadsFromEmbeddedResource_AndHasBothFamilies() {
      Assert.NotEmpty(OnnxCatalog.All);
      Assert.NotEmpty(OnnxCatalog.ByFamily(OnnxCatalog.FamilyPiper));
      Assert.NotEmpty(OnnxCatalog.ByFamily(OnnxCatalog.FamilyKokoro));
    }

    [Fact]
    public void EveryVoice_HasRequiredFields() {
      foreach (var v in OnnxCatalog.All) {
        Assert.False(string.IsNullOrWhiteSpace(v.Id), "Id");
        Assert.False(string.IsNullOrWhiteSpace(v.Family), $"Family ({v.Id})");
        Assert.False(string.IsNullOrWhiteSpace(v.Locale), $"Locale ({v.Id})");
        Assert.False(string.IsNullOrWhiteSpace(v.DisplayName), $"DisplayName ({v.Id})");
        Assert.False(string.IsNullOrWhiteSpace(v.DownloadId), $"DownloadId ({v.Id})");
        Assert.True(v.SizeMB > 0, $"SizeMB ({v.Id})");
      }
    }

    [Fact]
    public void VoiceIds_AreUnique() {
      var dupes = OnnxCatalog.All.GroupBy(v => v.Id, StringComparer.OrdinalIgnoreCase)
        .Where(g => g.Count() > 1).Select(g => g.Key).ToList();
      Assert.Empty(dupes);
    }

    [Fact]
    public void PiperVoice_IdEqualsDownloadId_AndSidIsZero() {
      foreach (var v in OnnxCatalog.ByFamily(OnnxCatalog.FamilyPiper)) {
        Assert.Equal(v.Id, v.DownloadId);
        Assert.Equal(0, v.Sid);
      }
    }

    [Fact]
    public void KokoroVoices_ShareTheSinglePackDownloadId() {
      var ids = OnnxCatalog.ByFamily(OnnxCatalog.FamilyKokoro)
        .Select(v => v.DownloadId).Distinct().ToList();
      Assert.Equal(new[] { "kokoro-multi-lang-v1_0" }, ids);
    }

    [Fact]
    public void Lang_IsBakedPerFamilyAndLocale() {
      // Piper carries its own espeak voice -> no per-call lang.
      foreach (var v in OnnxCatalog.ByFamily(OnnxCatalog.FamilyPiper))
        Assert.Equal("", v.Lang);

      // Kokoro lang is the espeak-ng voice id resolved from locale at build time.
      var expected = new System.Collections.Generic.Dictionary<string, string> {
        ["en_US"] = "", ["en_GB"] = "en-gb-x-rp", ["pt_BR"] = "pt-br",
      };
      foreach (var v in OnnxCatalog.ByFamily(OnnxCatalog.FamilyKokoro)) {
        Assert.True(expected.ContainsKey(v.Locale), $"unexpected Kokoro locale {v.Locale}");
        Assert.Equal(expected[v.Locale], v.Lang);
      }
    }

    [Fact]
    public void Piper_CoversAllSevenShippedLocales() {
      var locales = OnnxCatalog.Locales(OnnxCatalog.FamilyPiper).ToHashSet(StringComparer.OrdinalIgnoreCase);
      foreach (var expected in new[] { "en_US", "en_GB", "fr_FR", "de_DE", "es_ES", "pt_BR", "ru_RU" })
        Assert.Contains(expected, locales);
    }

    [Fact]
    public void Find_ResolvesById_AndIsNullForUnknown() {
      var first = OnnxCatalog.All.First();
      Assert.Same(first, OnnxCatalog.Find(first.Id));
      Assert.Null(OnnxCatalog.Find("no-such-voice"));
      Assert.Null(OnnxCatalog.Find(null));
    }

    [Fact]
    public void ResolveModelsDir_EmptyFallsBackToAppData() {
      var resolved = OnnxCatalog.ResolveModelsDir("");
      var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
      Assert.Equal(Path.Combine(appData, "ACT_DiscordTriggers", "models"), resolved);
    }

    [Fact]
    public void ResolveModelsDir_ExpandsEnvironmentVariables() {
      var resolved = OnnxCatalog.ResolveModelsDir(@"%APPDATA%\custom\models");
      var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
      Assert.Equal(Path.Combine(appData, "custom", "models"), resolved);
    }

    [Fact]
    public void IsInstalled_FalseWhenDirAbsent() {
      using (var tmp = new TempDir()) {
        var piper = OnnxCatalog.ByFamily(OnnxCatalog.FamilyPiper).First();
        Assert.False(OnnxCatalog.IsInstalled(piper, tmp.Path));
      }
    }

    [Fact]
    public void IsInstalled_TrueForPiper_WhenOnnxPresent() {
      using (var tmp = new TempDir()) {
        var piper = OnnxCatalog.ByFamily(OnnxCatalog.FamilyPiper).First();
        var dir = Path.Combine(tmp.Path, piper.DownloadId);
        Directory.CreateDirectory(dir);
        Assert.False(OnnxCatalog.IsInstalled(piper, tmp.Path)); // empty dir is not installed
        File.WriteAllText(Path.Combine(dir, "model.onnx"), "x");
        Assert.True(OnnxCatalog.IsInstalled(piper, tmp.Path));
      }
    }

    [Fact]
    public void IsInstalled_Kokoro_RequiresModelAndVoicesBin() {
      using (var tmp = new TempDir()) {
        var kokoro = OnnxCatalog.ByFamily(OnnxCatalog.FamilyKokoro).First();
        var dir = Path.Combine(tmp.Path, kokoro.DownloadId);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "model.onnx"), "x");
        Assert.False(OnnxCatalog.IsInstalled(kokoro, tmp.Path)); // voices.bin still missing
        File.WriteAllText(Path.Combine(dir, "voices.bin"), "x");
        Assert.True(OnnxCatalog.IsInstalled(kokoro, tmp.Path));
      }
    }
  }
}
