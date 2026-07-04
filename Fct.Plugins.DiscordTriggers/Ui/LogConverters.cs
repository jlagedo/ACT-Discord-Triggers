using Avalonia.Data.Converters;
using ACT_DiscordTriggers.Core.Ipc;

namespace Fct.Plugins.DiscordTriggers.Ui {
  /// <summary>
  /// Log-row helpers: map <see cref="LogLevel"/> to the <c>warn</c>/<c>error</c> style classes so the
  /// diagnostics list colours Warn/Error lines via the token-bound selectors in PluginStyles.axaml.
  /// </summary>
  public static class LogConverters {
    public static readonly IValueConverter IsWarn =
      new FuncValueConverter<LogLevel, bool>(l => l == LogLevel.Warn);

    public static readonly IValueConverter IsError =
      new FuncValueConverter<LogLevel, bool>(l => l == LogLevel.Error);
  }
}
