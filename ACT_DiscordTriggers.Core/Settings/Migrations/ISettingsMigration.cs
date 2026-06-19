using System.Xml.Linq;

namespace ACT_DiscordTriggers.Core.Settings.Migrations {
  /// <summary>
  /// One step that upgrades a new-format settings document from
  /// <see cref="FromVersion"/> to <c>FromVersion + 1</c>.
  ///
  /// Migrations operate on the raw XML DOM (the document <see cref="XElement"/>
  /// root) rather than on <see cref="PluginSettings"/>. That keeps old migrations
  /// stable even as the POCO evolves — a migration written today must never depend
  /// on the latest POCO shape, or it would silently break when fields are renamed
  /// or removed later.
  /// </summary>
  public interface ISettingsMigration {
    /// <summary>The schema version this migration upgrades FROM (it produces FromVersion + 1).</summary>
    int FromVersion { get; }

    /// <summary>Mutate the document root in place to the next schema version.</summary>
    void Apply(XElement root);
  }
}
