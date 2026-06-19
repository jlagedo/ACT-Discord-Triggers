using System;

namespace ACT_DiscordTriggers.ViewModels {
  // One line in the diagnostics/UI log. Immutable; rendered by the view (and later
  // a WPF ItemsControl) from the timestamp + message.
  public sealed class LogEntry {
    public DateTime Timestamp { get; }
    public string Message { get; }

    public LogEntry(DateTime timestamp, string message) {
      Timestamp = timestamp;
      Message = message;
    }
  }
}
