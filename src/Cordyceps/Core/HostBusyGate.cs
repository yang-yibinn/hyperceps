using System;
using System.Globalization;
using System.Threading;

namespace Cordyceps.Core
{
    /// <summary>A tool call stopped before reaching Grasshopper because it stayed busy.</summary>
    public sealed class HostBusyException : Exception
    {
        public HostBusyException(string message) : base(message) { }
    }

    /// <summary>
    /// Hyperceps: every call that needs Grasshopper's UI thread passes this gate first. While
    /// Grasshopper is solving, the UI thread isn't responding, or another call holds it, the call
    /// waits up to <see cref="MaxWait"/>; if it is still busy, the call stops with a message saying
    /// what is running and for how long. Before this, such a call waited until the AI app gave up
    /// (about 60 s) and the agent learned nothing: in one session a runaway checker script held
    /// Grasshopper for minutes and six calls in a row ended as "The operation timed out".
    /// Reads cached state only (<see cref="SolverState.Derive()"/>), never Grasshopper itself.
    /// </summary>
    public static class HostBusyGate
    {
        /// <summary>Long enough for an ordinary solve to finish, well inside the app's ~60 s limit.</summary>
        public static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(15);

        /// <summary>
        /// How long a solve counts as busy while Rhino still answers. A solve occupies the UI
        /// thread, but its heartbeat goes stale only after a few seconds; past this age, a "solving"
        /// flag on a responsive host is a missed end-of-solve event, which must not refuse every call.
        /// </summary>
        public static readonly TimeSpan FreshSolve = TimeSpan.FromSeconds(10);

        private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

        public static bool IsBusy(HostStatus status, DateTime nowUtc)
        {
            if (status == null) return false;
            if (status.UiWorkInProgress || status.Ui == UiLiveness.Blocked) return true;
            return status.Solving && (!status.SolvingSince.HasValue || nowUtc - status.SolvingSince.Value < FreshSolve);
        }

        public static void WaitUntilFree() =>
            WaitUntilFree(() => SolverState.Shared.Derive(), MaxWait, Thread.Sleep, () => DateTime.UtcNow);

        /// <summary>Return once Grasshopper is free; throw <see cref="HostBusyException"/> after <paramref name="maxWait"/>.</summary>
        public static void WaitUntilFree(Func<HostStatus> derive, TimeSpan maxWait, Action<TimeSpan> sleep, Func<DateTime> clock)
        {
            var start = clock();
            while (true)
            {
                var status = derive();
                var now = clock();
                if (!IsBusy(status, now))
                    return;
                if (now - start >= maxWait)
                    throw new HostBusyException(Message(status, now));
                sleep(PollInterval);
            }
        }

        /// <summary>What is keeping Grasshopper busy, and what to do about it.</summary>
        public static string Message(HostStatus status, DateTime nowUtc)
        {
            const string probe = "gh_inspect(action='connection') checks at once without waiting.";
            if (status.Solving)
            {
                var doc = string.IsNullOrEmpty(status.SolvingDocumentName) ? "the definition" : $"'{status.SolvingDocumentName}'";
                var age = status.SolvingSince.HasValue ? " for " + Duration(nowUtc - status.SolvingSince.Value) : "";
                return $"Grasshopper has been solving {doc}{age}, so this call stopped before reaching it. "
                     + $"Try again when the solve ends; {probe} A script that never finishes can't be stopped from here: "
                     + "tell the user, who can wait or close Rhino (unsaved work is lost).";
            }

            var silent = status.HeartbeatAgeMs.HasValue ? " for " + Duration(TimeSpan.FromMilliseconds(status.HeartbeatAgeMs.Value)) : "";
            if (status.UiWorkInProgress)
                return $"Grasshopper is still busy with an earlier call{silent}, so this call stopped before reaching it. "
                     + $"Try again when that finishes; {probe}";

            return $"Rhino hasn't responded{silent} and nothing is solving: a dialog is probably open in Rhino. "
                 + "This call stopped before reaching it. Ask the user to check Rhino rather than retrying.";
        }

        /// <summary><c>8 s</c>, <c>3 min 20 s</c>.</summary>
        public static string Duration(TimeSpan span)
        {
            var seconds = Math.Max(0, (int)Math.Round(span.TotalSeconds));
            if (seconds < 60)
                return seconds.ToString(CultureInfo.InvariantCulture) + " s";
            var rest = seconds % 60;
            return (seconds / 60).ToString(CultureInfo.InvariantCulture) + " min"
                + (rest > 0 ? " " + rest.ToString(CultureInfo.InvariantCulture) + " s" : "");
        }
    }
}
