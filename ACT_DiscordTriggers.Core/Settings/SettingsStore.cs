using System;
using System.IO;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Serialization;
using ACT_DiscordTriggers.Core.Settings.Migrations;

namespace ACT_DiscordTriggers.Core.Settings {
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

    // XmlSerializer construction generates and compiles an assembly per type — expensive,
    // so build it once and reuse (it's thread-safe once constructed).
    private static readonly XmlSerializer Serializer = new XmlSerializer(typeof(PluginSettings));

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
            TrySave(recovered); // rewrite is best-effort; the in-memory result is still good
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
        TrySave(imported); // rewrite in the new format, in place (best-effort; retries on next save)
        Log("Legacy settings migrated successfully (schema v" + imported.SchemaVersion + ").");
        return imported;
      }

      if (IsNewFormat(doc)) {
        try {
          int fromVersion = SettingsMigrator.ReadSchemaVersion(doc.Root);
          if (fromVersion > PluginSettings.CurrentSchemaVersion) {
            // Newer file than this build understands (the user downgraded the plugin).
            // We read what we can, but the NEXT Save rewrites at v{Current}, dropping any
            // field this build doesn't know about. Read-only here, so nothing is lost yet.
            Log("Config is schema v" + fromVersion + " but this build is v" + PluginSettings.CurrentSchemaVersion
                + "; newer-only settings will be dropped on the next save.");
          }
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
      var ns = new XmlSerializerNamespaces();
      ns.Add("", ""); // strip xsi/xsd noise

      var xmlSettings = new XmlWriterSettings {
        Encoding = Utf8NoBom,
        Indent = true,
        IndentChars = "  ",
      };
      using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
      using (var writer = XmlWriter.Create(fs, xmlSettings)) {
        Serializer.Serialize(writer, settings, ns);
      }

      if (File.Exists(configPath))
        File.Replace(tmp, configPath, null);
      else
        File.Move(tmp, configPath);
    }

    // Best-effort rewrite used on the load path. Load() must never throw (callers run it
    // unguarded during plugin init), but Save() does real IO that can fail (locked file,
    // read-only Config dir). Swallow the failure: the in-memory settings are already good
    // and the next interactive Save will retry.
    private void TrySave(PluginSettings settings) {
      try { Save(settings); }
      catch (Exception ex) { Log("Could not rewrite config (will retry on next save): " + ex.Message); }
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

    private XDocument TryParse(string path) {
      try {
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
          return XDocument.Load(fs);
      } catch {
        return null;
      }
    }

    private static PluginSettings Deserialize(XDocument doc) {
      using (var reader = doc.CreateReader())
        return Serializer.Deserialize(reader) as PluginSettings;
    }

    private void Log(string message) {
      try { log?.Invoke("[settings] " + message); } catch { /* logging must never break load/save */ }
    }
  }
}
