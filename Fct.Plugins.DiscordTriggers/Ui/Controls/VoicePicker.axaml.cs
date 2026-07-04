using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
using ACT_DiscordTriggers.Core.ViewModels;

namespace Fct.Plugins.DiscordTriggers.Ui.Controls {
  /// <summary>One locale group in the voice picker (header + its voices).</summary>
  public sealed class VoiceGroup {
    public VoiceGroup(string name, IReadOnlyList<OnnxVoiceItem> items) {
      Name = name;
      Items = items;
    }
    public string Name { get; }
    public IReadOnlyList<OnnxVoiceItem> Items { get; }
  }

  /// <summary>
  /// The ONNX voice picker: a searchable, locale-grouped flyout over the VM's
  /// <see cref="DiscordTriggersViewModel.OnnxVoices"/>. Replaces the WPF CollectionViewSource +
  /// FilterEventArgs path — filtering and grouping stay a view concern; selection is pushed to
  /// <see cref="DiscordTriggersViewModel.SelectedOnnxVoice"/>.
  /// </summary>
  public partial class VoicePicker : UserControl {
    private DiscordTriggersViewModel? boundVm;

    public VoicePicker() {
      InitializeComponent();
    }

    private void InitializeComponent() {
      AvaloniaXamlLoader.Load(this);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e) {
      base.OnAttachedToVisualTree(e);
      Bind();
    }

    protected override void OnDataContextChanged(EventArgs e) {
      base.OnDataContextChanged(e);
      Bind();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e) {
      if (boundVm != null) boundVm.OnnxVoices.CollectionChanged -= OnVoicesChanged;
      boundVm = null;
      base.OnDetachedFromVisualTree(e);
    }

    // Track the VM so a family switch (which refills OnnxVoices) rebuilds the visible groups.
    private void Bind() {
      var vm = DataContext as DiscordTriggersViewModel;
      if (ReferenceEquals(vm, boundVm)) return;
      if (boundVm != null) boundVm.OnnxVoices.CollectionChanged -= OnVoicesChanged;
      boundVm = vm;
      if (boundVm != null) boundVm.OnnxVoices.CollectionChanged += OnVoicesChanged;
      Rebuild();
    }

    private void OnVoicesChanged(object? sender, NotifyCollectionChangedEventArgs e) => Rebuild();

    private void OnSearchChanged(object? sender, TextChangedEventArgs e) => Rebuild();

    // Opening resets the search to show the full list and focuses the box.
    private void OnOpened(object? sender, EventArgs e) {
      var search = this.FindControl<TextBox>("Search");
      if (search != null) {
        search.Text = "";
        search.Focus();
      }
      Rebuild();
    }

    private void OnVoiceClick(object? sender, RoutedEventArgs e) {
      if (sender is Control c && c.DataContext is OnnxVoiceItem item && boundVm != null) {
        boundVm.SelectedOnnxVoice = item;
      }
      var toggle = this.FindControl<ToggleButton>("Toggle");
      if (toggle != null) toggle.IsChecked = false;   // close the flyout
    }

    // Tokenized substring match over locale + name + tier, grouped by friendly locale name,
    // mirroring the old OnnxVoiceFilter.
    private void Rebuild() {
      var list = this.FindControl<ItemsControl>("GroupList");
      if (list == null) return;
      if (boundVm == null) {
        list.ItemsSource = Array.Empty<VoiceGroup>();
        return;
      }

      var query = this.FindControl<TextBox>("Search")?.Text ?? "";
      var tokens = query.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

      var groups = boundVm.OnnxVoices
        .Where(v => Matches(v, tokens))
        .GroupBy(v => v.LocaleName)
        .Select(g => new VoiceGroup(g.Key, g.ToList()))
        .ToList();

      list.ItemsSource = groups;
    }

    private static bool Matches(OnnxVoiceItem v, IReadOnlyList<string> tokens) {
      if (tokens.Count == 0) return true;
      string hay = (v.LocaleName + " " + v.Locale + " " + v.Name + " " + v.Tier).ToLowerInvariant();
      foreach (var t in tokens) {
        if (!hay.Contains(t.ToLowerInvariant())) return false;
      }
      return true;
    }
  }
}
