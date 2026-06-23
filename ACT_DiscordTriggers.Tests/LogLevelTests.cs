using ACT_DiscordTriggers.Core.Ipc;
using Xunit;

namespace ACT_DiscordTriggers.Tests {
  // LogLevels.ParseWire is the single boundary that turns the bridge's wire-level
  // string ('Info'|'Warn'|'Error') into the C# severity the UI and the unified log
  // file carry. It must never throw and must default unknowns to Info so a stray
  // notification can't masquerade as a failure.
  public class LogLevelTests {
    [Theory]
    [InlineData("Info", LogLevel.Info)]
    [InlineData("Warn", LogLevel.Warn)]
    [InlineData("Error", LogLevel.Error)]
    [InlineData("error", LogLevel.Error)]   // case-insensitive
    [InlineData("WARN", LogLevel.Warn)]
    public void Maps_known_wire_strings(string wire, LogLevel expected) {
      Assert.Equal(expected, LogLevels.ParseWire(wire));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("verbose")]   // unknown level
    [InlineData("Information")]
    public void Unknown_or_missing_defaults_to_Info(string wire) {
      Assert.Equal(LogLevel.Info, LogLevels.ParseWire(wire));
    }
  }
}
