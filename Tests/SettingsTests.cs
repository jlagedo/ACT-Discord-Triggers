using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Xml.Linq;
using ACT_DiscordTriggers.Settings;
using ACT_DiscordTriggers.Settings.Migrations;
using Xunit;

namespace ActDiscordTriggers.Tests {
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
          BotToken = "tok", BotStatus = "hi", AutoConnect = true,
          TtsVoice = "Microsoft Zira Desktop", TtsVolume = 7, TtsSpeed = 13,
          RandomFx = true, FxChance = 42, Normalize = false,
          NormalizeTarget = 15, AudioQualityIndex = 2,
        };
        store.Save(original);

        var loaded = store.Load();
        Assert.Equal(original.BotToken, loaded.BotToken);
        Assert.Equal(original.BotStatus, loaded.BotStatus);
        Assert.Equal(original.AutoConnect, loaded.AutoConnect);
        Assert.Equal(original.TtsVoice, loaded.TtsVoice);
        Assert.Equal(original.TtsVolume, loaded.TtsVolume);
        Assert.Equal(original.TtsSpeed, loaded.TtsSpeed);
        Assert.Equal(original.RandomFx, loaded.RandomFx);
        Assert.Equal(original.FxChance, loaded.FxChance);
        Assert.Equal(original.Normalize, loaded.Normalize);
        Assert.Equal(original.NormalizeTarget, loaded.NormalizeTarget);
        Assert.Equal(original.AudioQualityIndex, loaded.AudioQualityIndex);
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
      var doc = XDocument.Parse("<DiscordTriggersSettings SchemaVersion=\"1\" />");
      Assert.False(new SettingsMigrator().MigrateInPlace(doc)); // target = current (1)
    }

    [Fact]
    public void Migrator_MissingStep_Throws() {
      var doc = XDocument.Parse("<DiscordTriggersSettings SchemaVersion=\"1\" />");
      Assert.Throws<SettingsMigrationException>(
        () => new SettingsMigrator().MigrateInPlace(doc, targetVersion: 2));
    }

    // ---- Migration logging -----------------------------------------------

    [Fact]
    public void Migrator_LogsEachStep() {
      var logs = new List<string>();
      var doc = XDocument.Parse("<DiscordTriggersSettings SchemaVersion=\"1\" />");
      var migrator = new SettingsMigrator(new ISettingsMigration[] { new FakeV1ToV2() }, logs.Add);

      migrator.MigrateInPlace(doc, targetVersion: 2);

      Assert.Contains(logs, l => l.Contains("v1 -> v2"));
    }

    [Fact]
    public void Load_LegacyFile_LogsTheMigration() {
      using (var t = new TempDir()) {
        var path = t.File("c.xml");
        File.WriteAllText(path, LegacyXml, new UTF8Encoding(false));
        var logs = new List<string>();

        new SettingsStore(path, new SettingsMigrator(), logs.Add).Load();

        Assert.Contains(logs, l => l.Contains("Detected legacy"));
        Assert.Contains(logs, l => l.Contains("migrated"));
      }
    }
  }
}
