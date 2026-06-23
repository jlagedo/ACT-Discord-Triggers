using System;

namespace ACT_DiscordTriggers.Core.Ipc {
  // Severity of a diagnostics line. Mirrors the bridge's wire-level
  // LogLevel ('Info' | 'Warn' | 'Error' in protocol.ts) so a line keeps its
  // classification from origin to the unified log file and the UI row colour.
  public enum LogLevel {
    Info,
    Warn,
    Error,
  }

  public static class LogLevels {
    // Map the bridge's wire string to the enum. Unknown/absent → Info so a
    // malformed notification never throws and never masquerades as a failure.
    public static LogLevel ParseWire(string wire) {
      if (string.IsNullOrEmpty(wire)) return LogLevel.Info;
      if (string.Equals(wire, "Error", StringComparison.OrdinalIgnoreCase)) return LogLevel.Error;
      if (string.Equals(wire, "Warn", StringComparison.OrdinalIgnoreCase)) return LogLevel.Warn;
      return LogLevel.Info;
    }
  }
}
