using System;
using System.Threading;
using System.Threading.Tasks;

namespace ACT_DiscordTriggers.Core.Ipc {
    // Race a task against a timeout without leaking the timeout's timer. The naive
    // `Task.WhenAny(task, Task.Delay(timeout))` leaves the Task.Delay timer alive
    // until it fires even when `task` wins — on a hot request path (one per TTS /
    // sound clip, default 60s timeout) those timers accumulate. Cancelling the
    // delay the moment `task` completes reaps the timer immediately.
    internal static class TaskTimeout {
        // Returns true if `task` completed before `timeout`; false on timeout. Does
        // not observe `task`'s result/exception — callers await `task` themselves
        // after a true return.
        internal static async Task<bool> CompletesWithinAsync(Task task, TimeSpan timeout) {
            using (var cts = new CancellationTokenSource()) {
                var delay = Task.Delay(timeout, cts.Token);
                var winner = await Task.WhenAny(task, delay).ConfigureAwait(false);
                if (winner == task) {
                    cts.Cancel(); // reap the delay timer now, don't let it run to term
                    return true;
                }
                return false;
            }
        }
    }
}
