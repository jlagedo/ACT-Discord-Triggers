using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace ACT_DiscordTriggers {
  // WinForms data binding refreshes a bound control from IBindingList.ListChanged and
  // ignores INotifyCollectionChanged. A ComboBox bound (directly or via BindingSource)
  // to a ViewModel's ObservableCollection<T> therefore never reflects items added or
  // cleared after the bind. This mirrors a source ObservableCollection<T> — kept on
  // the VM because it's the WPF-idiomatic type the eventual WPF view will bind to —
  // into a BindingList<T> the WinForms control can observe.
  //
  // The mirror is one-way (source -> this): the bound control only reads the item
  // list; selection flows back to the VM through a separate two-way SelectedItem
  // binding. Mutating this list directly is not supported.
  //
  // Thread affinity: the source ViewModel marshals every collection mutation onto the
  // captured UI SynchronizationContext, so CollectionChanged here (and the ListChanged
  // it raises) always fire on the UI thread, matching WinForms' single-thread rule.
  public sealed class ObservableBindingList<T> : BindingList<T>, IDisposable {
    private readonly ObservableCollection<T> source;

    public ObservableBindingList(ObservableCollection<T> source) {
      this.source = source ?? throw new ArgumentNullException(nameof(source));
      // Seed via Items (the protected backing list) so no ListChanged fires before
      // the control is even bound.
      foreach (var item in source) Items.Add(item);
      source.CollectionChanged += OnSourceChanged;
    }

    private void OnSourceChanged(object sender, NotifyCollectionChangedEventArgs e) {
      switch (e.Action) {
        case NotifyCollectionChangedAction.Add:
          for (int i = 0; i < e.NewItems.Count; i++)
            Insert(e.NewStartingIndex + i, (T)e.NewItems[i]);
          break;
        case NotifyCollectionChangedAction.Remove:
          for (int i = e.OldItems.Count - 1; i >= 0; i--)
            RemoveAt(e.OldStartingIndex + i);
          break;
        case NotifyCollectionChangedAction.Replace:
          for (int i = 0; i < e.NewItems.Count; i++)
            this[e.NewStartingIndex + i] = (T)e.NewItems[i];
          break;
        // Move is rare for these lists, and Reset (Clear) carries no item payload;
        // resync wholesale rather than special-case.
        case NotifyCollectionChangedAction.Move:
        case NotifyCollectionChangedAction.Reset:
        default:
          Resync();
          break;
      }
    }

    private void Resync() {
      // Rebuild silently, then raise a single ListChanged(Reset) so the bound control
      // refreshes once instead of per item.
      bool raise = RaiseListChangedEvents;
      RaiseListChangedEvents = false;
      try {
        Items.Clear();
        foreach (var item in source) Items.Add(item);
      } finally {
        RaiseListChangedEvents = raise;
      }
      ResetBindings();
    }

    public void Dispose() => source.CollectionChanged -= OnSourceChanged;
  }
}
