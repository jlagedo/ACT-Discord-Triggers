using System.Xml.Linq;

namespace ACT_DiscordTriggers.Core.Settings.Migrations {
  /// <summary>
  /// Schema v1 → v2: adds the ONNX TTS fields (engine selection, neural voice
  /// family/voice, CPU threads, models directory) introduced alongside the
  /// Text-to-Speech page's ONNX engine.
  ///
  /// The defaults written here are <b>hard-coded literals</b>, not references to
  /// <see cref="PluginSettings"/>, per <see cref="ISettingsMigration"/>: a migration
  /// must stay stable even if the POCO's defaults change later. They land an
  /// upgrading user on the SAPI engine with the catalog default neural voice — i.e.
  /// identical behaviour to before, since the engine stays "sapi".
  /// </summary>
  public class V1ToV2 : ISettingsMigration {
    public int FromVersion => 1;

    public void Apply(XElement root) {
      AddIfMissing(root, "TtsEngine", "sapi");
      AddIfMissing(root, "OnnxFamily", "piper");
      AddIfMissing(root, "OnnxVoice", "vits-piper-pt_BR-faber-medium");
      AddIfMissing(root, "TtsThreads", "4");
      AddIfMissing(root, "ModelsDir", "");
    }

    // Only add an element the v1 document lacks, so re-running the step is a no-op
    // and a hand-edited file that already carries the field keeps its value.
    private static void AddIfMissing(XElement root, string name, string value) {
      if (root.Element(name) == null) root.Add(new XElement(name, value));
    }
  }
}
