using System;
using System.ComponentModel;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows.Controls;
using System.Windows.Data;
using ACT_DiscordTriggers;
using Xunit;

namespace ACT_DiscordTriggers.Tests {
  // Regression tests for the PasswordBox -> ViewModel write-back. The masked token
  // field binds the bridgeable BotToken through PasswordBoxBinding.BoundPassword.
  // The bug these guard: the BoundPassword DP defaulted to "", so when BotToken
  // also started "" the binding's first transfer ("" -> "") was a no-op, the
  // change callback never ran, the PasswordChanged handler was never subscribed,
  // and typed tokens were silently dropped (the bridge got an empty token).
  public class PasswordBoxBindingTests {
    // A minimal stand-in for the ViewModel's bindable BotToken.
    private sealed class TokenHost : INotifyPropertyChanged {
      private string token = "";
      public string Token {
        get => token;
        set {
          if (token == value) return;
          token = value;
          PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Token)));
        }
      }
      public event PropertyChangedEventHandler PropertyChanged;
    }

    // PasswordBox is a WPF control; it must be created and driven on an STA thread.
    // xUnit runs tests on MTA pool threads, so marshal onto a dedicated STA thread
    // and rethrow any failure with its stack intact.
    private static void RunSta(Action action) {
      Exception captured = null;
      var t = new Thread(() => {
        try { action(); } catch (Exception ex) { captured = ex; }
      });
      t.SetApartmentState(ApartmentState.STA);
      t.IsBackground = true;
      t.Start();
      t.Join();
      if (captured != null) ExceptionDispatchInfo.Capture(captured).Throw();
    }

    private static PasswordBox BoundBox(TokenHost host) {
      var box = new PasswordBox { DataContext = host };
      var binding = new Binding(nameof(TokenHost.Token)) {
        Mode = BindingMode.TwoWay,
        UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged,
      };
      BindingOperations.SetBinding(box, PasswordBoxBinding.BoundPasswordProperty, binding);
      return box;
    }

    [Fact]
    public void Typing_updates_source_when_initial_token_is_empty() {
      RunSta(() => {
        var host = new TokenHost(); // Token == "" — the fresh-install / no-saved-token case.
        var box = BoundBox(host);

        // Setting Password raises PasswordChanged, exactly as user input would.
        box.Password = "my-secret-token";

        Assert.Equal("my-secret-token", host.Token);
      });
    }

    [Fact]
    public void Existing_token_is_pushed_into_the_box() {
      RunSta(() => {
        var host = new TokenHost { Token = "saved-token" };
        var box = BoundBox(host);

        Assert.Equal("saved-token", box.Password);
      });
    }

    [Fact]
    public void Subsequent_edits_continue_to_flow_to_source() {
      RunSta(() => {
        var host = new TokenHost();
        var box = BoundBox(host);

        box.Password = "first";
        Assert.Equal("first", host.Token);

        box.Password = "second";
        Assert.Equal("second", host.Token);
      });
    }
  }
}
