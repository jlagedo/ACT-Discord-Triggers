using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;

namespace ACT_DiscordTriggers.Core.Settings.Migrations {
  /// <summary>
  /// Upgrades a new-format settings document (root <c>&lt;DiscordTriggersSettings&gt;</c>)
  /// from whatever <c>SchemaVersion</c> it carries up to
  /// <see cref="PluginSettings.CurrentSchemaVersion"/>, by applying a chain of
  /// <see cref="ISettingsMigration"/> steps in order.
  ///
  /// The registry holds one step (<see cref="V1ToV2"/>). Adding a future schema
  /// change is the same three moves:
  ///   1. Bump <see cref="PluginSettings.CurrentSchemaVersion"/>.
  ///   2. Add a class implementing <see cref="ISettingsMigration"/> whose
  ///      <c>FromVersion</c> is the prior version, mutating the XML DOM.
  ///   3. Add it to <see cref="DefaultMigrations"/>.
  ///
  /// NOTE: the legacy ACT control-keyed format (root <c>&lt;Config&gt;</c>) is NOT a
  /// step in this chain — it is a different file structure entirely and is converted
  /// once by <see cref="LegacyConfigImporter"/> as a bootstrap into v1. This chain
  /// only handles new-format v1 → v2 → ... upgrades.
  /// </summary>
  public class SettingsMigrator {
    private readonly List<ISettingsMigration> migrations;
    private readonly Action<string> log;

    /// <summary>The shipping set of migrations, ordered by source version.</summary>
    public static IReadOnlyList<ISettingsMigration> DefaultMigrations { get; } =
      new List<ISettingsMigration> { new V1ToV2() };

    public SettingsMigrator() : this(DefaultMigrations) { }

    /// <summary>Test seam: supply a custom migration set (and optional log sink).</summary>
    public SettingsMigrator(IEnumerable<ISettingsMigration> migrations, Action<string> log = null) {
      this.migrations = migrations?.OrderBy(m => m.FromVersion).ToList()
                        ?? new List<ISettingsMigration>();
      this.log = log;
    }

    /// <summary>
    /// Apply migrations in place until the document reaches <paramref name="targetVersion"/>
    /// (defaults to the current schema version). Returns true if anything changed
    /// (so the caller can re-save). Throws if a version gap has no registered step.
    /// </summary>
    public bool MigrateInPlace(XDocument doc, int targetVersion = PluginSettings.CurrentSchemaVersion) {
      var root = doc?.Root;
      if (root == null) return false;

      bool changed = false;
      int version = ReadSchemaVersion(root);

      while (version < targetVersion) {
        var step = migrations.FirstOrDefault(m => m.FromVersion == version);
        if (step == null) {
          throw new SettingsMigrationException(
            $"No migration registered from schema version {version} to {version + 1}.");
        }
        log?.Invoke($"Applying settings migration v{version} -> v{version + 1}.");
        step.Apply(root);
        version++;
        root.SetAttributeValue("SchemaVersion", version);
        changed = true;
      }

      return changed;
    }

    /// <summary>
    /// Read the <c>SchemaVersion</c> attribute off a new-format document root.
    /// A root with no/garbled version attribute is treated as v1. Shared so
    /// <see cref="SettingsStore"/> reports the same "from" version it migrates from.
    /// </summary>
    public static int ReadSchemaVersion(XElement root) {
      var attr = root?.Attribute("SchemaVersion");
      return attr != null && int.TryParse(attr.Value, out var v) ? v : 1;
    }
  }
}
