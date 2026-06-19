using System;

namespace ACT_DiscordTriggers.Core.Settings.Migrations {
  /// <summary>
  /// Thrown when the settings document cannot be migrated to the current schema
  /// version (e.g. a version gap with no registered <see cref="ISettingsMigration"/>).
  /// <see cref="SettingsStore"/> catches this and falls back to defaults rather than
  /// failing plugin startup.
  /// </summary>
  public class SettingsMigrationException : Exception {
    public SettingsMigrationException(string message) : base(message) { }
  }
}
