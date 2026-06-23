using System;
using System.Threading.Tasks;
using ACT_DiscordTriggers.Core.Ipc;

namespace ACT_DiscordTriggers.Tests {
    // Polyfill for Task.WaitAsync(TimeSpan) — net6+ only on the BCL, but the
    // production code targets net48. Keeps test bodies readable. Delegates the
    // race to the production TaskTimeout helper so tests exercise (and don't
    // re-leak) the same timer-reaping path as the real code.
    internal static class TaskExtensions {
        public static async Task<T> WaitAsync<T>(this Task<T> task, TimeSpan timeout) {
            if (!await TaskTimeout.CompletesWithinAsync(task, timeout).ConfigureAwait(false))
                throw new TimeoutException();
            return await task.ConfigureAwait(false);
        }

        public static async Task WaitAsync(this Task task, TimeSpan timeout) {
            if (!await TaskTimeout.CompletesWithinAsync(task, timeout).ConfigureAwait(false))
                throw new TimeoutException();
            await task.ConfigureAwait(false);
        }
    }
}
