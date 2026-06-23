using System;
using System.Diagnostics;
using System.Threading.Tasks;
using ACT_DiscordTriggers.Core.Ipc;
using Xunit;

namespace ACT_DiscordTriggers.Tests {
  // TaskTimeout.CompletesWithinAsync races a task against a timeout. The contract:
  // return true iff the task wins, and — the reason it exists — cancel the backing
  // Task.Delay on a win so its timer doesn't outlive the call.
  public class TaskTimeoutTests {
    [Fact]
    public async Task Already_completed_task_returns_true() {
      Assert.True(await TaskTimeout.CompletesWithinAsync(Task.CompletedTask, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Task_completing_before_timeout_returns_true_promptly() {
      var sw = Stopwatch.StartNew();
      // Generous 30s timeout, fast work: the call must return ~when the work
      // finishes, not when the timeout elapses — proving the delay was cancelled
      // rather than awaited to term (the timer leak this guards against).
      var ok = await TaskTimeout.CompletesWithinAsync(
        Task.Delay(50, TestContext.Current.CancellationToken), TimeSpan.FromSeconds(30));
      sw.Stop();
      Assert.True(ok);
      Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"returned in {sw.Elapsed}, expected ~50ms");
    }

    [Fact]
    public async Task Task_that_never_completes_times_out_to_false() {
      var never = new TaskCompletionSource<bool>().Task;
      Assert.False(await TaskTimeout.CompletesWithinAsync(never, TimeSpan.FromMilliseconds(100)));
    }
  }
}
