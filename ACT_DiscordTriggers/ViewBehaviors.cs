using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ACT_DiscordTriggers.Core.ViewModels;

namespace ACT_DiscordTriggers {
  // View-layer attached helpers. Deliberately STATIC classes (base type System.Object):
  // ACT loads the plugin via Assembly.LoadFrom + GetTypes(), which resolves every defined
  // type's base/interfaces before our Costura resolver attaches. A type deriving from a
  // Costura-merged base (e.g. Microsoft.Xaml.Behaviors' Behavior<T>) would throw there.
  // Static helpers sidestep that — they only touch merged/WPF types inside method bodies,
  // which GetTypes() never resolves.

  // Bridges PasswordBox.Password (not a DependencyProperty, so not directly bindable) to a
  // bindable attached string. Lets the XAML keep the token masked while two-way binding it
  // to the ViewModel's BotToken with no view code-behind.
  public static class PasswordBoxBinding {
    public static readonly DependencyProperty BoundPasswordProperty =
      DependencyProperty.RegisterAttached(
        "BoundPassword", typeof(string), typeof(PasswordBoxBinding),
        new FrameworkPropertyMetadata(
          "", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnBoundPasswordChanged));

    // Re-entrancy guard: HandlePasswordChanged writes BoundPassword, which would otherwise
    // re-enter OnBoundPasswordChanged and reset PasswordBox.Password (moving the caret).
    private static readonly DependencyProperty UpdatingProperty =
      DependencyProperty.RegisterAttached(
        "Updating", typeof(bool), typeof(PasswordBoxBinding), new PropertyMetadata(false));

    public static string GetBoundPassword(DependencyObject d) =>
      (string)d.GetValue(BoundPasswordProperty);
    public static void SetBoundPassword(DependencyObject d, string value) =>
      d.SetValue(BoundPasswordProperty, value);

    private static void OnBoundPasswordChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) {
      if (!(d is PasswordBox box)) return;
      box.PasswordChanged -= HandlePasswordChanged;
      if (!(bool)box.GetValue(UpdatingProperty))
        box.Password = (string)e.NewValue ?? "";
      box.PasswordChanged += HandlePasswordChanged;
    }

    private static void HandlePasswordChanged(object sender, RoutedEventArgs e) {
      var box = (PasswordBox)sender;
      box.SetValue(UpdatingProperty, true);
      SetBoundPassword(box, box.Password);
      box.SetValue(UpdatingProperty, false);
    }
  }

  // Enables Ctrl+C on a ListBox/ListView of LogEntry rows to copy the selected messages to
  // the clipboard (one per line). Clipboard is a view concern, so it stays out of the Core
  // ViewModel. Mirrors the old WinForms LogList_KeyUp.
  public static class LogListCopy {
    public static readonly DependencyProperty EnabledProperty =
      DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(LogListCopy),
        new PropertyMetadata(false, OnEnabledChanged));

    public static bool GetEnabled(DependencyObject d) => (bool)d.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject d, bool value) => d.SetValue(EnabledProperty, value);

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) {
      if (!(d is ListBox list)) return; // ListView derives from ListBox
      if ((bool)e.NewValue) list.KeyDown += OnKeyDown;
      else list.KeyDown -= OnKeyDown;
    }

    private static void OnKeyDown(object sender, KeyEventArgs e) {
      if (e.Key != Key.C || (Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
      var list = (ListBox)sender;
      if (list.SelectedItems.Count == 0) return;

      var sb = new StringBuilder();
      foreach (var item in list.SelectedItems)
        if (item is LogEntry entry) sb.AppendLine(entry.Message);

      string text = sb.ToString();
      if (text.Length > 0) {
        try { Clipboard.SetText(text); } catch { /* clipboard busy — best effort */ }
        e.Handled = true;
      }
    }
  }
}
