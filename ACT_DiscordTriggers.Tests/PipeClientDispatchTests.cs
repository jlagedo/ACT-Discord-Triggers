using System;
using System.IO.Pipes;
using ACT_DiscordTriggers.Core.Ipc;
using Xunit;

namespace ACT_DiscordTriggers.Tests {
  // Frame routing in DispatchJsonFrame, exercised without a live pipe (the dispatch
  // path never touches the pipe stream). Guards two things: a well-formed Result
  // still resolves its pending request after the early-reqId refactor, and a torn
  // frame is swallowed-and-logged rather than thrown out of the read loop.
  public class PipeClientDispatchTests {
    private static PipeClient NewClient() =>
      new PipeClient(new NamedPipeClientStream(
        ".", "actdt-dispatch-test-" + Guid.NewGuid().ToString("N"), PipeDirection.InOut));

    [Fact]
    public void Result_frame_resolves_its_pending_request() {
      var client = NewClient();
      var pending = client.RegisterPendingForTest(5);

      client.DispatchJsonFrame("{\"op\":\"Result\",\"reqId\":5,\"ok\":true}");

      Assert.True(pending.IsCompleted);
      Assert.False(pending.IsFaulted);
    }

    [Fact]
    public void Malformed_frame_is_swallowed_without_throwing() {
      var client = NewClient();
      var pending = client.RegisterPendingForTest(6);

      // A torn frame whose reqId can't be recovered: it must not throw out of the
      // read loop (it's logged instead), and it leaves the unrelated request pending.
      var thrown = Record.Exception(() => client.DispatchJsonFrame("{ this is not valid json"));

      Assert.Null(thrown);
      Assert.False(pending.IsCompleted);
    }

    [Fact]
    public void Throwing_notification_handler_is_contained_not_propagated() {
      var client = NewClient();
      client.OnBotReady += () => throw new InvalidOperationException("boom");
      var unrelated = client.RegisterPendingForTest(7);

      // The handler runs off the read loop and may throw; dispatch must not surface
      // that throw (it is logged, not swallowed), and unrelated requests are untouched.
      var thrown = Record.Exception(() => client.DispatchJsonFrame("{\"op\":\"BotReady\"}"));

      Assert.Null(thrown);
      Assert.False(unrelated.IsCompleted);
    }
  }
}
