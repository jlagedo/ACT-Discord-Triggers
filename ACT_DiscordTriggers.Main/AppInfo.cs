using System;
using System.Reflection;

namespace ACT_DiscordTriggers {
  // Version identity for the Main assembly, surfaced to the Information-tab XAML via
  // {x:Static local:AppInfo.VersionDisplay}. Lives here (not on the bootstrap plugin
  // type) because the view is compiled into this byte-loaded assembly; the bootstrap
  // is a separate assembly the view's `local:` namespace can't see.
  public static class AppInfo {
    public static string PluginVersion() {
      try { return typeof(AppInfo).Assembly.GetName().Version.ToString(); }
      catch { return "?"; }
    }

    public static string VersionDisplay => "v" + PluginVersion();
  }
}
