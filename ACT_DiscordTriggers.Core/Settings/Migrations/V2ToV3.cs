using System.Globalization;
using System.Xml.Linq;

namespace ACT_DiscordTriggers.Core.Settings.Migrations {
  /// <summary>
  /// Schema v2 → v3: the auto-level loudness metric changes from broadband RMS to
  /// ITU-R BS.1770 K-weighting (LUFS). Under the new metric the same program reads
  /// roughly <see cref="CalibrationOffsetDb"/> dB louder (channel-sum, the −0.691
  /// LUFS offset, and the K-weighting of a speech spectrum), and every baked voice
  /// level is re-measured to match. To keep an upgrading user's <i>speech</i>
  /// playback level unchanged — rather than silently reinterpreting their old
  /// <c>-20 dBFS</c> target as <c>-20 LUFS</c> (which would play quieter) — this
  /// shifts the stored <c>NormalizeTarget</c> magnitude down by the same offset so
  /// the target tracks the re-baked voice levels.
  ///
  /// Hard-coded literals only, per <see cref="ISettingsMigration"/>: the offset and
  /// bounds are frozen here so the step stays stable even if the POCO's defaults or
  /// ranges change later.
  /// </summary>
  public class V2ToV3 : ISettingsMigration {
    public int FromVersion => 2;

    // dB the new K-weighted LUFS measure reads above the old broadband RMS for
    // typical speech — the amount to lower the |target| magnitude by.
    private const int CalibrationOffsetDb = 3;

    // The v3 NormalizeTarget bounds (frozen literals, matching PluginSettings v3).
    private const int TargetMin = 9, TargetMax = 27;
    private const int TargetDefault = 17;

    public void Apply(XElement root) {
      var el = root.Element("NormalizeTarget");
      if (el == null) {
        root.Add(new XElement("NormalizeTarget", TargetDefault));
        return;
      }
      if (int.TryParse(el.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var old)) {
        var shifted = Clamp(old - CalibrationOffsetDb, TargetMin, TargetMax);
        el.Value = shifted.ToString(CultureInfo.InvariantCulture);
      } else {
        el.Value = TargetDefault.ToString(CultureInfo.InvariantCulture);
      }
    }

    private static int Clamp(int v, int min, int max) => v < min ? min : v > max ? max : v;
  }
}
