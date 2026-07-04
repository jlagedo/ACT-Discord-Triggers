using System;
using System.Collections.Specialized;
using System.Linq;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ACT_DiscordTriggers.Core.ViewModels;

namespace Fct.Plugins.DiscordTriggers.Ui {
  /// <summary>
  /// The plugin's Avalonia config UI. <c>DataContext</c> is the shared
  /// <see cref="DiscordTriggersViewModel"/>, supplied by <see cref="DiscordTriggersPlugin"/> when the
  /// host builds the settings surface. Code-behind holds only view concerns the WPF code-behind also
  /// owned: log copy/auto-scroll, the folder picker, and external-link launching.
  /// </summary>
  public partial class DiscordTriggersView : UserControl {
    private const string ProjectUrl = "https://github.com/jlagedo/ACT-Discord-Triggers";
    private const string IssuesUrl = "https://github.com/jlagedo/ACT-Discord-Triggers/issues/new";
    private const string GuideUrl = "https://github.com/jlagedo/ACT-Discord-Triggers/wiki/First-Time-Setup-Guide";

    private ListBox? logList;
    private ScrollViewer? logScroll;
    private DiscordTriggersViewModel? boundVm;

    public DiscordTriggersView() {
      InitializeComponent();
      logList = this.FindControl<ListBox>("LogList");
    }

    private void InitializeComponent() {
      AvaloniaXamlLoader.Load(this);
    }

    protected override void OnDataContextChanged(EventArgs e) {
      base.OnDataContextChanged(e);
      if (boundVm != null) boundVm.LogEntries.CollectionChanged -= OnLogChanged;
      boundVm = DataContext as DiscordTriggersViewModel;
      if (boundVm != null) boundVm.LogEntries.CollectionChanged += OnLogChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e) {
      if (boundVm != null) boundVm.LogEntries.CollectionChanged -= OnLogChanged;
      boundVm = null;
      base.OnDetachedFromVisualTree(e);
    }

    // --- Diagnostics log: follow the tail only when already parked at the bottom ---------------
    private void OnLogChanged(object? sender, NotifyCollectionChangedEventArgs e) {
      if (e.Action != NotifyCollectionChangedAction.Add) return;
      Dispatcher.UIThread.Post(() => {
        var sv = ResolveLogScroll();
        if (sv == null) return;
        // Within one viewport of the end → keep following; otherwise leave the user's scroll alone.
        bool atBottom = sv.Offset.Y >= sv.Extent.Height - sv.Viewport.Height - sv.Viewport.Height;
        if (atBottom) sv.ScrollToEnd();
      }, DispatcherPriority.Background);
    }

    private ScrollViewer? ResolveLogScroll() {
      if (logScroll != null) return logScroll;
      logScroll = logList?.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
      return logScroll;
    }

    private void OnLogKeyDown(object? sender, KeyEventArgs e) {
      if (e.Key == Key.C && e.KeyModifiers.HasFlag(KeyModifiers.Control)) {
        CopyLog();
        e.Handled = true;
      }
    }

    private void OnCopyLog(object? sender, RoutedEventArgs e) => CopyLog();

    // Copy the selected log lines (or all, if none are selected) to the clipboard.
    private void CopyLog() {
      if (boundVm == null) return;
      var items = (logList?.SelectedItems?.Count ?? 0) > 0
        ? logList!.SelectedItems!.Cast<LogEntry>()
        : boundVm.LogEntries;
      var sb = new StringBuilder();
      foreach (var le in items) sb.AppendLine(le.Message);
      var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
      if (clipboard != null) _ = clipboard.SetTextAsync(sb.ToString());
    }

    // --- Advanced: pick the ONNX models folder -------------------------------------------------
    private async void OnBrowseModels(object? sender, RoutedEventArgs e) {
      var vm = DataContext as DiscordTriggersViewModel;
      var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
      if (vm == null || storage == null) return;
      try {
        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions {
          Title = "Select the ONNX models folder",
          AllowMultiple = false,
        });
        var picked = folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
        if (!string.IsNullOrEmpty(picked)) vm.ModelsDir = picked!;
      } catch { /* user cancelled / picker unavailable — leave ModelsDir unchanged */ }
    }

    // --- Information tab links -----------------------------------------------------------------
    private void OnOpenProject(object? sender, RoutedEventArgs e) => Launch(ProjectUrl);
    private void OnReportProblem(object? sender, RoutedEventArgs e) => Launch(IssuesUrl);
    private void OnSetupGuide(object? sender, RoutedEventArgs e) => Launch(GuideUrl);

    private void Launch(string url) {
      var launcher = TopLevel.GetTopLevel(this)?.Launcher;
      if (launcher != null) _ = launcher.LaunchUriAsync(new Uri(url));
    }
  }
}
