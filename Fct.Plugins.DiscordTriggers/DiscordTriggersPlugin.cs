using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using ACT_DiscordTriggers.Core.Ipc;
using ACT_DiscordTriggers.Core.Settings;
using ACT_DiscordTriggers.Core.ViewModels;
using Fct.Abstractions;
using Fct.Abstractions.UI;
using Mel = Microsoft.Extensions.Logging;

namespace Fct.Plugins.DiscordTriggers {
  /// <summary>
  /// The native FFXIV Combat Tracker plugin. Replaces the net48 ACT drop-in
  /// (<c>DiscordTriggersPlugin : IActPluginV1</c> + the WPF view code-behind): it owns the same
  /// bridge-discovery / diagnostics / VM wiring, but routes audio through a terminal
  /// <see cref="IAudioSink"/> instead of the ACT PlayTtsMethod/PlaySoundMethod delegate hijack, and
  /// contributes an Avalonia settings page instead of a WinForms TabPage.
  /// </summary>
  public sealed class DiscordTriggersPlugin : IPlugin, IUiContributor {
    private IPluginHost? host;
    private DiscordClientService? discordService;
    private DiscordTriggersViewModel? vm;
    private IDisposable? sink;

    // Host constructs the entry type via Activator.CreateInstance — needs a public parameterless ctor.
    public DiscordTriggersPlugin() { }

    public Task InitializeAsync(IPluginHost host, CancellationToken ct) {
      this.host = host;
      try {
        // Locate the out-of-process Discord bridge, then wire diagnostics BEFORE the VM loads
        // settings (the log sink drops anything written pre-Init, and the one-time config
        // migration logs through it).
        string bridgeDir = FindBridgeDir(host.Storage.DataDirectory);
        DiscordClient.SetBridgePath(bridgeDir);
        try {
          DiagnosticsLog.Init(host.Storage.DataDirectory, bridgeDir, host.Self.Version);
        } catch (Exception ex) {
          Mel.LoggerExtensions.LogWarning(host.Logger, ex, "Diagnostics log init failed");
        }

        discordService = new DiscordClientService();
        var store = new SettingsStore(
          host.Storage.DataDirectory, "DiscordTriggers.config.xml", ForwardLog);
        vm = new DiscordTriggersViewModel(discordService, store);

        // The VM stays host-agnostic: it raises these when output goes live/idle (bot channel or
        // local device) so we register/dispose the terminal audio sink — the modern form of the
        // old TTS/sound delegate swap.
        vm.OutputActivated += OnOutputActivated;
        vm.OutputDeactivated += OnOutputDeactivated;

        vm.Log("Diagnostics log: " + DiagnosticsLog.UnifiedPath);
        vm.Initialize();
      } catch (Exception ex) {
        Mel.LoggerExtensions.LogError(host.Logger, ex, "DiscordTriggers plugin init failed");
        throw;
      }
      return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync() {
      // Mirror the net48 OnPluginDeInitAsync order, each step independently guarded so one failure
      // can't orphan the node bridge.
      try { sink?.Dispose(); } catch { /* best-effort */ }
      sink = null;

      // Detach the static DiscordClient subscriptions synchronously, before the async shutdown, so a
      // deferred continuation can't leave a stale handler bound across a reload.
      try { discordService?.Dispose(); } catch (Exception ex) { LogError("Error disposing Discord service on exit", ex); }

      if (vm != null) {
        vm.OutputActivated -= OnOutputActivated;
        vm.OutputDeactivated -= OnOutputDeactivated;
        try { vm.Save(); } catch (Exception ex) { LogError("Error saving settings on exit", ex); }
        try { await vm.ShutdownAsync().ConfigureAwait(false); } catch (Exception ex) { LogError("Error shutting down the Discord bridge", ex); }
      }

      try { DiagnosticsLog.Flush(); DiagnosticsLog.Shutdown(); } catch { /* best-effort */ }
    }

    // Called once on the UI thread after InitializeAsync. Phase 2 ships a placeholder; the real
    // Avalonia view is Phase 3.
    public void RegisterUi(IUiHost ui) {
      ui.AddSettingsPage(new UiSurface(
        "discordtriggers.settings", "Discord Triggers", CreatePlaceholderView));
    }

    private void OnOutputActivated() {
      try {
        sink = host!.Audio.RegisterSink(new DiscordAudioSink(vm!), priority: 10, terminal: true);
      } catch (Exception ex) {
        LogError("Failed to register audio sink", ex);
      }
    }

    private void OnOutputDeactivated() {
      try { sink?.Dispose(); } catch { /* best-effort */ }
      sink = null;
    }

    // The bridge ships as node.exe + bundle.js + node_modules/ next to the plugin DLL (staged by the
    // build). Return the directory; BridgeProcess derives the file paths. Fall back to the host's
    // per-plugin storage dir when the bridge isn't staged next to the DLL.
    private static string FindBridgeDir(string fallbackDir) {
      try {
        string? dir = Path.GetDirectoryName(typeof(DiscordTriggersPlugin).Assembly.Location);
        if (!string.IsNullOrEmpty(dir)
            && File.Exists(Path.Combine(dir, "node.exe"))
            && File.Exists(Path.Combine(dir, "bundle.js"))) {
          return dir;
        }
      } catch { /* fall through */ }
      return fallbackDir;
    }

    private static Control CreatePlaceholderView() {
      return new StackPanel {
        Margin = new Thickness(16),
        Spacing = 8,
        Children = {
          new TextBlock { Text = "Discord Triggers", FontSize = 20, FontWeight = FontWeight.SemiBold },
          new TextBlock {
            Text = "Audio and lifecycle are active. The full settings UI arrives in a later migration phase.",
            TextWrapping = TextWrapping.Wrap,
          },
        },
      };
    }

    // Settings-store + lifecycle messages: keep the user-emailable unified diagnostics file (via the
    // VM) AND surface them in the host's structured log.
    private void ForwardLog(string message, LogLevel level) {
      try { vm?.Log(message, level); } catch { /* logging must never throw */ }
      try { if (host != null) Mel.LoggerExtensions.Log(host.Logger, ToMel(level), "{Message}", message); } catch { /* logging must never throw */ }
    }

    private void LogError(string message, Exception ex) {
      try { DiagnosticsLog.Append(message + ": " + ex, LogLevel.Error); } catch { }
      try { if (host != null) Mel.LoggerExtensions.LogError(host.Logger, ex, "{Message}", message); } catch { }
    }

    private static Mel.LogLevel ToMel(LogLevel level) => level switch {
      LogLevel.Error => Mel.LogLevel.Error,
      LogLevel.Warn => Mel.LogLevel.Warning,
      _ => Mel.LogLevel.Information,
    };
  }
}
