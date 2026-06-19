using System;
using System.IO;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Serialization;
using ACT_DiscordTriggers.Settings.Migrations;

namespace ACT_DiscordTriggers.Settings {
  /// <summary>
  /// Loads and saves <see cref="PluginSettings"/> as XML, owning format detection,
  /// legacy migration, schema upgrades, and crash-safe writes.
  ///
  /// Pure C# — the config directory and file name are injected (not read from
  /// <c>ActGlobals</c>) so the whole thing is unit-testable without ACT. The plugin
  /// supplies <c>{AppDataFolder}\Config</c> and <c>ACT_DiscordTriggers.config.xml</c>.
  ///
  /// <see cref="Load"/> never throws: a missing, corrupt, or un-migratable file falls
  /// back to defaults so the plugin always starts.
  /// </summary>
  public class SettingsStore {
    // The legacy file is backed up here ONCE, before the first destructive rewrite,
    // and never overwritten — so the user's original bot token survives forever.
    private const string LegacyBackupSuffix = ".legacy-v0.bak";
    private const string TempSuffix = ".tmp";

    private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

    private readonly string configPath;
    private readonly Action<string> log;
    private readonly SettingsMigrator migrator;

    public SettingsStore(string configDirectory, string fileName, Action<string> log = null)
      : this(Path.Combine(configDirectory, fileName),
             new SettingsMigrator(SettingsMigrator.DefaultMigrations, log), log) { }

    /// <summary>Test seam: full path + custom migrator.</summary>
    public SettingsStore(string configPath, SettingsMigrator migrator, Action<string> log = null) {
      this.configPath = configPath;
      this.migrator = migrator ?? new SettingsMigrator();
      this.log = log;
    }

    public string ConfigPath => configPath;
    public string LegacyBackupPath => configPath + LegacyBackupSuffix;

    public PluginSettings Load() {
      if (!File.Exists(configPath)) {
        return new PluginSettings(); // fresh install
      }

      XDocument doc = TryParse(configPath);
      if (doc == null) {
        // Corrupt primary file: fall back to the one-time legacy backup if we have it.
        if (File.Exists(LegacyBackupPath)) {
          var bak = TryParse(LegacyBackupPath);
          if (bak != null && LegacyConfigImporter.IsLegacy(bak)) {
            Log("Primary config unreadable; recovering from " + Path.GetFileName(LegacyBackupPath));
            var recovered = LegacyConfigImporter.Import(bak);
            Save(recovered);
            return recovered;
          }
        }
        Log("Config unreadable; starting with defaults.");
        return new PluginSettings();
      }

      if (LegacyConfigImporter.IsLegacy(doc)) {
        Log("Detected legacy (v0) config; backing up to " + Path.GetFileName(LegacyBackupPath)
            + " and migrating to schema v" + PluginSettings.CurrentSchemaVersion + ".");
        BackupLegacyOnce();
        var imported = LegacyConfigImporter.Import(doc);
        Save(imported); // rewrite in the new format, in place
        Log("Legacy settings migrated successfully (schema v" + imported.SchemaVersion + ").");
        return imported;
      }

      if (IsNewFormat(doc)) {
        try {
          int fromVersion = ReadSchemaVersion(doc);
          bool changed = migrator.MigrateInPlace(doc);
          var settings = Deserialize(doc);
          if (settings == null) return new PluginSettings();
          if (changed) {
            Log("Settings schema upgraded from v" + fromVersion + " to v" + settings.SchemaVersion + ".");
            Save(settings);
          }
          return settings;
        } catch (Exception ex) {
          Log("Failed to migrate/parse config; starting with defaults. " + ex.Message);
          return new PluginSettings();
        }
      }

      Log("Unrecognised config format; starting with defaults.");
      return new PluginSettings();
    }

    public void Save(PluginSettings settings) {
      if (settings == null) return;
      settings.SchemaVersion = PluginSettings.CurrentSchemaVersion;

      var dir = Path.GetDirectoryName(configPath);
      if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

      // Crash-safe: serialize to a temp file, then atomically swap it over the target
      // so an interrupted write can never leave a half-written config behind.
      string tmp = configPath + TempSuffix;
      var serializer = new XmlSerializer(typeof(PluginSettings));
      var ns = new XmlSerializerNamespaces();
      ns.Add("", ""); // strip xsi/xsd noise

      var xmlSettings = new XmlWriterSettings {
        Encoding = Utf8NoBom,
        Indent = true,
        IndentChars = "  ",
      };
      using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
      using (var writer = XmlWriter.Create(fs, xmlSettings)) {
        serializer.Serialize(writer, settings, ns);
      }

      if (File.Exists(configPath))
        File.Replace(tmp, configPath, null);
      else
        File.Move(tmp, configPath);
    }

    private void BackupLegacyOnce() {
      try {
        if (!File.Exists(LegacyBackupPath))
          File.Copy(configPath, LegacyBackupPath, false);
      } catch (Exception ex) {
        Log("Could not back up legacy config: " + ex.Message);
      }
    }

    private static bool IsNewFormat(XDocument doc)
      => doc?.Root != null && doc.Root.Name.LocalName == "DiscordTriggersSettings";

    private static int ReadSchemaVersion(XDocument doc) {
      var attr = doc?.Root?.Attribute("SchemaVersion");
      return attr != null && int.TryParse(attr.Value, out var v) ? v : 1;
    }

    private XDocument TryParse(string path) {
      try {
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
          return XDocument.Load(fs);
      } catch {
        return null;
      }
    }

    private static PluginSettings Deserialize(XDocument doc) {
      var serializer = new XmlSerializer(typeof(PluginSettings));
      using (var reader = doc.CreateReader())
        return serializer.Deserialize(reader) as PluginSettings;
    }

    private void Log(string message) {
      try { log?.Invoke("[settings] " + message); } catch { /* logging must never break load/save */ }
    }
  }
}
