using System;
using ACT_DiscordTriggers.Core.Ipc;

namespace ACT_DiscordTriggers.Core.ViewModels {
  // One line in the diagnostics/UI log. Immutable; rendered by the view from the
  // timestamp + message, with Level driving the row colour (Warn/Error stand out).
  public sealed class LogEntry {
    public DateTime Timestamp { get; }
    public string Message { get; }
    public LogLevel Level { get; }

    public LogEntry(DateTime timestamp, string message, LogLevel level = LogLevel.Info) {
      Timestamp = timestamp;
      Message = message;
      Level = level;
    }
  }
}
