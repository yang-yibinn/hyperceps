using System;

namespace Cordyceps.Core
{
    /// <summary>
    /// Hyperceps: believe a modal dialog only when the reading persists. <see cref="HostStatus.ModalInferred"/>
    /// means "the UI thread hasn't stamped a heartbeat for 5 s and nothing is solving". That is also true
    /// for a moment at the end of every solve longer than 5 s: <c>SolutionEnd</c> clears "solving" before
    /// the queued heartbeat lands (confirmed live: under 0.1 s, Nov 13, 2026). A real dialog keeps the
    /// heartbeat from landing for as long as it is open; after a solve the heartbeat lands within a
    /// fraction of a second. So a dialog is believed once the reading has held, unbroken, for
    /// <see cref="Persist"/>. Host-free: time is passed in, so it is unit-tested.
    /// </summary>
    public sealed class DialogCheck
    {
        /// <summary>How long the dialog reading must hold. The heartbeat lands every second.</summary>
        public static readonly TimeSpan Persist = TimeSpan.FromSeconds(2);

        private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

        private readonly TimeSpan _persist;
        private DateTime? _since;

        public DialogCheck(TimeSpan? persist = null)
        {
            _persist = persist ?? Persist;
        }

        /// <summary>One reading. True once the dialog reading has held without a break for the whole persist time.</summary>
        public bool Confirmed(bool modalInferred, DateTime nowUtc)
        {
            if (!modalInferred)
            {
                _since = null;
                return false;
            }
            if (_since == null) _since = nowUtc;
            return nowUtc - _since.Value >= _persist;
        }

        /// <summary>
        /// For a call that has just read a dialog: keep reading until it clears (false) or has held for
        /// the persist time (true). Ends within about <see cref="Persist"/>.
        /// </summary>
        public static bool IsRealDialog(Func<HostStatus> derive, Action<TimeSpan> sleep, Func<DateTime> clock, TimeSpan? persist = null)
        {
            var check = new DialogCheck(persist);
            while (true)
            {
                var status = derive();
                if (status == null || !status.ModalInferred) return false;
                if (check.Confirmed(true, clock())) return true;
                sleep(PollInterval);
            }
        }
    }

    /// <summary>
    /// Hyperceps: wait for a running solve to end before a call that mustn't start during one (sweep),
    /// as gh_batch does, instead of refusing at once. After a slow sweep the restored value re-solves
    /// after the answer, so the agent's next sweep usually arrives during a solve.
    /// </summary>
    public static class SolveWait
    {
        public enum Outcome
        {
            /// <summary>Nothing is solving and no dialog: go ahead.</summary>
            Free,
            /// <summary>Still solving when the wait ran out.</summary>
            StillSolving,
            /// <summary>A dialog, read without a break for <see cref="DialogCheck.Persist"/>.</summary>
            Dialog
        }

        private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

        /// <summary>
        /// Wait up to <paramref name="maxWait"/> for the solve to end. A dialog reading has to persist to
        /// count; one that is still unconfirmed when the wait runs out is read on until it clears or
        /// is confirmed (at most <see cref="DialogCheck.Persist"/> more).
        /// </summary>
        public static (Outcome Outcome, HostStatus Status) UntilSolveEnds(Func<HostStatus> derive, TimeSpan maxWait,
            Action<TimeSpan> sleep, Func<DateTime> clock)
        {
            var start = clock();
            var dialog = new DialogCheck();
            while (true)
            {
                var status = derive() ?? new HostStatus();
                var now = clock();
                if (dialog.Confirmed(status.ModalInferred, now))
                    return (Outcome.Dialog, status);
                if (!status.Solving && !status.ModalInferred)
                    return (Outcome.Free, status);
                if (status.Solving && now - start >= maxWait)
                    return (Outcome.StillSolving, status);
                sleep(PollInterval);
            }
        }
    }
}
