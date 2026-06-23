using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Xml.Linq;
using ACT_DiscordTriggers.Core.Settings;
using ACT_DiscordTriggers.Core.Settings.Migrations;
using Xunit;

namespace ACT_DiscordTriggers.Tests {
  public class SettingsTests {
    // The exact legacy ACT SettingsSerializer format captured from a live install.
    // Note: cmbAudioQuality stores the display TEXT, FxChance=100, NormalizeTarget=30,
    // and cmbTTS is absent (never persisted by the old format).
    private const string LegacyXml =
@"<?xml version=""1.0"" encoding=""utf-8"" standalone=""yes""?>
<Config>
  <SettingsSerializer>
    <TextBox Name=""txtToken"" Value=""SECRET-TOKEN-123"" />
    <TrackBar Name=""sliderTTSVol"" Value=""8"" />
    <TrackBar Name=""sliderTTSSpeed"" Value=""12"" />
    <CheckBox Name=""chkAutoConnect"" Value=""True"" />
    <TextBox Name=""txtBotStatus"" Value=""Playing with ACT Triggers"" />
    <CheckBox Name=""chkRandomFx"" Value=""False"" />
    <TrackBar Name=""sliderFxChance"" Value=""100"" />
    <CheckBox Name=""chkNormalize"" Value=""True"" />
    <TrackBar Name=""sliderNormalizeTarget"" Value=""30"" />
    <ComboBox Name=""cmbAudioQuality"" Value=""High (128 kbps)"" />
  </SettingsSerializer>
</Config>";

    private sealed class TempDir : IDisposable {
      public string Path { get; }
      public TempDir() {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "act-dt-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
      }
      public string File(string name) => System.IO.Path.Combine(Path, name);
      public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
    }

    private static SettingsStore StoreAt(string path) =>
      new SettingsStore(path, new SettingsMigrator(), null);

    // ---- Legacy import ---------------------------------------------------

    [Fact]
    public void LegacyImport_MapsEveryField() {
      var s = LegacyConfigImporter.Import(XDocument.Parse(LegacyXml));

      Assert.Equal("SECRET-TOKEN-123", s.BotToken);
      Assert.Equal(8, s.TtsVolume);
      Assert.Equal(12, s.TtsSpeed);
      Assert.True(s.AutoConnect);
      Assert.Equal("Playing with ACT Triggers", s.BotStatus);
      Assert.False(s.RandomFx);
      Assert.Equal(100, s.FxChance);                 // not clamped here; UI clamps on apply
      Assert.True(s.Normalize);
      Assert.Equal(30, s.NormalizeTarget);
      Assert.Equal(2, s.AudioQualityIndex);          // GOTCHA: "High (128 kbps)" -> 2
      Assert.Equal("", s.TtsVoice);                  // absent in legacy -> default
      Assert.Equal(PluginSettings.CurrentSchemaVersion, s.SchemaVersion);
    }

    [Theory]
    [InlineData("Low (48 kbps)", 0)]
    [InlineData("Medium (96 kbps)", 1)]
    [InlineData("High (128 kbps)", 2)]
    [InlineData("garbage", 1)]
    [InlineData("", 1)]
    public void LegacyImport_AudioQualityTextToIndex(string text, int expected) {
      var xml = LegacyXml.Replace("High (128 kbps)", text);
      var s = LegacyConfigImporter.Import(XDocument.Parse(xml));
      Assert.Equal(expected, s.AudioQualityIndex);
    }

    [Fact]
    public void LegacyImport_MissingEntries_FallBackToDefaults() {
      var minimal = "<Config><SettingsSerializer><TextBox Name=\"txtToken\" Value=\"x\" /></SettingsSerializer></Config>";
      var s = LegacyConfigImporter.Import(XDocument.Parse(minimal));
      var d = new PluginSettings();
      Assert.Equal("x", s.BotToken);
      Assert.Equal(d.TtsVolume, s.TtsVolume);
      Assert.Equal(d.Normalize, s.Normalize);
      Assert.Equal(d.AudioQualityIndex, s.AudioQualityIndex);
    }

    // ---- Store: round-trip, missing, corrupt -----------------------------

    [Fact]
    public void Save_ThenLoad_RoundTrips() {
      using (var t = new TempDir()) {
        var store = StoreAt(t.File("c.xml"));
        var original = new PluginSettings {
          BotToken = "tok", BotStatus = "hi", AutoConnect = true, OutputMode = "local",
          TtsVoice = "Microsoft Zira Desktop", TtsVolume = 7, TtsSpeed = 13,
          TtsEngine = "onnx", OnnxFamily = "kokoro", OnnxVoice = "kokoro-0",
          TtsThreads = 4, ModelsDir = @"D:\voices",
          RandomFx = true, FxChance = 42, Normalize = false,
          NormalizeTarget = 15, AudioQualityIndex = 2,
          LimiterEnabled = false, LimiterCeilingIndex = 3,
        };
        store.Save(original);

        var loaded = store.Load();
        Assert.Equal(original.BotToken, loaded.BotToken);
        Assert.Equal(original.BotStatus, loaded.BotStatus);
        Assert.Equal(original.AutoConnect, loaded.AutoConnect);
        Assert.Equal(original.OutputMode, loaded.OutputMode);
        Assert.Equal(original.TtsVoice, loaded.TtsVoice);
        Assert.Equal(original.TtsVolume, loaded.TtsVolume);
        Assert.Equal(original.TtsSpeed, loaded.TtsSpeed);
        Assert.Equal(original.TtsEngine, loaded.TtsEngine);
        Assert.Equal(original.OnnxFamily, loaded.OnnxFamily);
        Assert.Equal(original.OnnxVoice, loaded.OnnxVoice);
        Assert.Equal(original.TtsThreads, loaded.TtsThreads);
        Assert.Equal(original.ModelsDir, loaded.ModelsDir);
        Assert.Equal(original.RandomFx, loaded.RandomFx);
        Assert.Equal(original.FxChance, loaded.FxChance);
        Assert.Equal(original.Normalize, loaded.Normalize);
        Assert.Equal(original.NormalizeTarget, loaded.NormalizeTarget);
        Assert.Equal(original.AudioQualityIndex, loaded.AudioQualityIndex);
        Assert.Equal(original.LimiterEnabled, loaded.LimiterEnabled);
        Assert.Equal(original.LimiterCeilingIndex, loaded.LimiterCeilingIndex);
        Assert.Equal(PluginSettings.CurrentSchemaVersion, loaded.SchemaVersion);
      }
    }

    [Fact]
    public void Save_WritesUtf8WithoutBom() {
      using (var t = new TempDir()) {
        var path = t.File("c.xml");
        StoreAt(path).Save(new PluginSettings());
        var bytes = File.ReadAllBytes(path);
        // No UTF-8 BOM (EF BB BF) at the start.
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
      }
    }

    [Fact]
    public void Load_MissingFile_ReturnsDefaults() {
      using (var t = new TempDir()) {
        var loaded = StoreAt(t.File("nope.xml")).Load();
        var d = new PluginSettings();
        Assert.Equal(d.BotToken, loaded.BotToken);
        Assert.Equal(d.AudioQualityIndex, loaded.AudioQualityIndex);
        Assert.Equal(PluginSettings.CurrentSchemaVersion, loaded.SchemaVersion);
      }
    }

    [Fact]
    public void Load_CorruptFile_ReturnsDefaults() {
      using (var t = new TempDir()) {
        var path = t.File("c.xml");
        File.WriteAllText(path, "this is <not> valid xml <<<");
        var loaded = StoreAt(path).Load();
        Assert.Equal("", loaded.BotToken);
        Assert.Equal(PluginSettings.CurrentSchemaVersion, loaded.SchemaVersion);
      }
    }

    // A new-format v1 file (pre-ONNX fields) with real user data, to prove the
    // v1 -> current upgrade preserves existing settings instead of resetting to defaults.
    private const string V1Xml =
@"<?xml version=""1.0"" encoding=""utf-8""?>
<DiscordTriggersSettings SchemaVersion=""1"">
  <BotToken>KEEP-ME</BotToken>
  <BotStatus>hi</BotStatus>
  <AutoConnect>true</AutoConnect>
  <TtsVoice>Microsoft Zira Desktop</TtsVoice>
  <TtsVolume>7</TtsVolume>
  <TtsSpeed>13</TtsSpeed>
  <RandomFx>false</RandomFx>
  <FxChance>25</FxChance>
  <Normalize>true</Normalize>
  <NormalizeTarget>20</NormalizeTarget>
  <AudioQualityIndex>1</AudioQualityIndex>
</DiscordTriggersSettings>";

    [Fact]
    public void Load_V1File_UpgradesToCurrent_PreservesFields_AndDefaultsOnnx() {
      using (var t = new TempDir()) {
        var path = t.File("c.xml");
        File.WriteAllText(path, V1Xml, new UTF8Encoding(false));

        var loaded = StoreAt(path).Load();

        // Existing settings survive the migration (no defaults-reset wipe).
        Assert.Equal("KEEP-ME", loaded.BotToken);
        Assert.Equal("Microsoft Zira Desktop", loaded.TtsVoice);
        Assert.Equal(7, loaded.TtsVolume);
        // New ONNX fields (v1 -> v2) land on their defaults.
        Assert.Equal("sapi", loaded.TtsEngine);
        Assert.Equal("piper", loaded.OnnxFamily);
        Assert.Equal("vits-piper-pt_BR-faber-medium", loaded.OnnxVoice);
        Assert.Equal(4, loaded.TtsThreads);
        Assert.Equal("", loaded.ModelsDir);
        // The v2 -> v3 metric change shifts the auto-level target down by the
        // calibration offset (broadband RMS -> K-weighted LUFS), so the old 20
        // becomes 17 — keeping the same perceived speech level.
        Assert.Equal(17, loaded.NormalizeTarget);
        Assert.Equal(PluginSettings.CurrentSchemaVersion, loaded.SchemaVersion);
        // The store rewrites the upgraded doc back to disk at the current version.
        Assert.Equal(
          PluginSettings.CurrentSchemaVersion.ToString(),
          XDocument.Load(path).Root.Attribute("SchemaVersion").Value);
      }
    }

    // ---- Store: legacy migration + one-time backup -----------------------

    [Fact]
    public void Load_LegacyFile_BacksUpOnce_AndRewritesNewFormat() {
      using (var t = new TempDir()) {
        var path = t.File("ACT_DiscordTriggers.config.xml");
        File.WriteAllText(path, LegacyXml, new UTF8Encoding(false));
        var store = StoreAt(path);

        var s = store.Load();
        Assert.Equal("SECRET-TOKEN-123", s.BotToken);
        Assert.Equal(2, s.AudioQualityIndex);

        // legacy-v0 backup created with the original contents
        var bakPath = path + ".legacy-v0.bak";
        Assert.True(File.Exists(bakPath));
        Assert.Contains("SettingsSerializer", File.ReadAllText(bakPath));

        // primary file rewritten in the new format
        var doc = XDocument.Load(path);
        Assert.Equal("DiscordTriggersSettings", doc.Root.Name.LocalName);

        // a second load reads the new format and does NOT overwrite the backup
        var bakBefore = File.ReadAllText(bakPath);
        var s2 = store.Load();
        Assert.Equal(2, s2.AudioQualityIndex);
        Assert.Equal(bakBefore, File.ReadAllText(bakPath));
      }
    }

    [Fact]
    public void Load_LegacyFile_WhenRewriteFails_DoesNotThrow_AndReturnsImported() {
      using (var t = new TempDir()) {
        var path = t.File("ACT_DiscordTriggers.config.xml");
        File.WriteAllText(path, LegacyXml, new UTF8Encoding(false));

        // Force Save() to fail: occupy the temp path Save writes to with a DIRECTORY,
        // so creating the temp FILE throws. Load must swallow it and still return the
        // imported settings (callers run Load unguarded during plugin init).
        Directory.CreateDirectory(path + ".tmp");

        var s = StoreAt(path).Load();

        Assert.Equal("SECRET-TOKEN-123", s.BotToken);   // imported despite the failed rewrite
        Assert.Equal(2, s.AudioQualityIndex);
        Assert.True(File.Exists(path + ".legacy-v0.bak")); // backup still taken
      }
    }

    // ---- Migration framework ---------------------------------------------

    private sealed class FakeV1ToV2 : ISettingsMigration {
      public int FromVersion => 1;
      public void Apply(XElement root) => root.Add(new XElement("AddedInV2", "yes"));
    }

    [Fact]
    public void Migrator_AppliesChain_AndBumpsVersion() {
      var doc = XDocument.Parse("<DiscordTriggersSettings SchemaVersion=\"1\" />");
      var migrator = new SettingsMigrator(new[] { new FakeV1ToV2() });

      bool changed = migrator.MigrateInPlace(doc, targetVersion: 2);

      Assert.True(changed);
      Assert.Equal("2", doc.Root.Attribute("SchemaVersion").Value);
      Assert.NotNull(doc.Root.Element("AddedInV2"));
    }

    [Fact]
    public void Migrator_AtCurrentVersion_NoOp() {
      var doc = XDocument.Parse(
        "<DiscordTriggersSettings SchemaVersion=\"" + PluginSettings.CurrentSchemaVersion + "\" />");
      Assert.False(new SettingsMigrator().MigrateInPlace(doc)); // already at the current version
    }

    [Fact]
    public void Migrator_MissingStep_Throws() {
      // An empty registry has no step for the v1 -> v2 gap, so the migrator must throw
      // (the framework's gap-detection, independent of the shipping migration set).
      var doc = XDocument.Parse("<DiscordTriggersSettings SchemaVersion=\"1\" />");
      Assert.Throws<SettingsMigrationException>(
        () => new SettingsMigrator(new ISettingsMigration[0]).MigrateInPlace(doc, targetVersion: 2));
    }

    [Fact]
    public void Migrator_V1ToV2_AddsOnnxDefaults() {
      // The shipping registry (DefaultMigrations) must carry the real v1 -> v2 step, or
      // existing v1 files would throw on load and reset to defaults (wiping the token).
      var doc = XDocument.Parse("<DiscordTriggersSettings SchemaVersion=\"1\" />");

      bool changed = new SettingsMigrator().MigrateInPlace(doc, targetVersion: 2); // just the v1 -> v2 step

      Assert.True(changed);
      Assert.Equal("2", doc.Root.Attribute("SchemaVersion").Value);
      Assert.Equal("sapi", doc.Root.Element("TtsEngine").Value);
      Assert.Equal("piper", doc.Root.Element("OnnxFamily").Value);
      Assert.Equal("vits-piper-pt_BR-faber-medium", doc.Root.Element("OnnxVoice").Value);
      Assert.Equal("4", doc.Root.Element("TtsThreads").Value);
      Assert.NotNull(doc.Root.Element("ModelsDir"));
    }

    [Fact]
    public void Migrator_V2ToV3_ShiftsNormalizeTargetForTheLufsMetric() {
      // v2 -> v3 lowers the stored auto-level target by the calibration offset so an
      // upgrading user's speech stays at the same level once the loudness metric
      // becomes K-weighted LUFS (the voice catalog is re-baked to match).
      var doc = XDocument.Parse(
        "<DiscordTriggersSettings SchemaVersion=\"2\"><NormalizeTarget>20</NormalizeTarget></DiscordTriggersSettings>");

      bool changed = new SettingsMigrator().MigrateInPlace(doc, targetVersion: 3);

      Assert.True(changed);
      Assert.Equal("3", doc.Root.Attribute("SchemaVersion").Value);
      Assert.Equal("17", doc.Root.Element("NormalizeTarget").Value);
    }

    [Fact]
    public void Migrator_V2ToV3_ClampsShiftedTargetIntoTheNewRange() {
      // The old minimum (12) shifted down by 3 would be 9 — the new minimum — and a
      // hand-edited value below that must not escape the range.
      var doc = XDocument.Parse(
        "<DiscordTriggersSettings SchemaVersion=\"2\"><NormalizeTarget>10</NormalizeTarget></DiscordTriggersSettings>");

      new SettingsMigrator().MigrateInPlace(doc, targetVersion: 3);

      Assert.Equal("9", doc.Root.Element("NormalizeTarget").Value); // 10 - 3 = 7 -> clamped to 9
    }

    // ---- Migration logging -----------------------------------------------

    [Fact]
    public void Migrator_LogsEachStep() {
      var logs = new List<string>();
      var doc = XDocument.Parse("<DiscordTriggersSettings SchemaVersion=\"1\" />");
      var migrator = new SettingsMigrator(new ISettingsMigration[] { new FakeV1ToV2() }, (m, lvl) => logs.Add(m));

      migrator.MigrateInPlace(doc, targetVersion: 2);

      Assert.Contains(logs, l => l.Contains("v1 -> v2"));
    }

    [Fact]
    public void Load_LegacyFile_LogsTheMigration() {
      using (var t = new TempDir()) {
        var path = t.File("c.xml");
        File.WriteAllText(path, LegacyXml, new UTF8Encoding(false));
        var logs = new List<string>();

        new SettingsStore(path, new SettingsMigrator(), (m, lvl) => logs.Add(m)).Load();

        Assert.Contains(logs, l => l.Contains("Detected legacy"));
        Assert.Contains(logs, l => l.Contains("migrated"));
      }
    }
  }
}
