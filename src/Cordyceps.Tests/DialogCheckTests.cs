using System;
using System.Collections.Generic;
using Cordyceps.Core;
using Xunit;

namespace Cordyceps.Tests
{
    public class DialogCheckTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 11, 13, 12, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void AMomentaryReading_IsNotADialog()
        {
            // Confirmed live: at the end of an 8 s solve the reading held for under 0.1 s.
            var check = new DialogCheck();
            Assert.False(check.Confirmed(true, T0));
            Assert.False(check.Confirmed(true, T0.AddMilliseconds(100)));
            Assert.False(check.Confirmed(false, T0.AddMilliseconds(200)));
        }

        [Fact]
        public void AReadingThatHolds_IsADialog()
        {
            var check = new DialogCheck();
            Assert.False(check.Confirmed(true, T0));
            Assert.False(check.Confirmed(true, T0.AddSeconds(1.9)));
            Assert.True(check.Confirmed(true, T0.AddSeconds(2)));
        }

        [Fact]
        public void AFlickeringReading_StartsAgain()
        {
            var check = new DialogCheck();
            Assert.False(check.Confirmed(true, T0));
            Assert.False(check.Confirmed(true, T0.AddSeconds(1.5)));
            Assert.False(check.Confirmed(false, T0.AddSeconds(1.6)));
            Assert.False(check.Confirmed(true, T0.AddSeconds(1.7)));
            Assert.False(check.Confirmed(true, T0.AddSeconds(3.2)));   // 1.5 s since it came back
            Assert.True(check.Confirmed(true, T0.AddSeconds(3.7)));
        }

        /// <summary>A fake host: a list of readings, one per poll, and a clock moved by the sleeps.</summary>
        private sealed class Host
        {
            private readonly Queue<HostStatus> _readings;
            private HostStatus _last = new HostStatus();
            public DateTime Now = T0;

            public Host(params HostStatus[] readings) { _readings = new Queue<HostStatus>(readings); }
            public HostStatus Derive() => _last = _readings.Count > 0 ? _readings.Dequeue() : _last;
            public void Sleep(TimeSpan span) => Now += span;
        }

        private static HostStatus Solving() => new HostStatus { Solving = true, Ui = UiLiveness.Blocked };
        private static HostStatus Gap() => new HostStatus { Ui = UiLiveness.Blocked, ModalInferred = true };
        private static HostStatus Free() => new HostStatus { Ui = UiLiveness.Responsive };

        [Fact]
        public void IsRealDialog_FalseWhenTheReadingClears_TrueWhenItHolds()
        {
            var cleared = new Host(Gap(), Free());
            Assert.False(DialogCheck.IsRealDialog(cleared.Derive, cleared.Sleep, () => cleared.Now));

            var dialog = new Host(Gap());   // the same reading at every poll
            Assert.True(DialogCheck.IsRealDialog(dialog.Derive, dialog.Sleep, () => dialog.Now));
            Assert.True(dialog.Now - T0 >= DialogCheck.Persist);
        }

        [Fact]
        public void UntilSolveEnds_WaitsThroughTheEndOfASolve_AndItsMomentaryDialog()
        {
            var host = new Host(Solving(), Solving(), Solving(), Gap(), Free());
            var (outcome, _) = SolveWait.UntilSolveEnds(host.Derive, TimeSpan.FromSeconds(15), host.Sleep, () => host.Now);
            Assert.Equal(SolveWait.Outcome.Free, outcome);
        }

        [Fact]
        public void UntilSolveEnds_StillSolvingAfterTheWait()
        {
            var host = new Host(Solving());   // solving at every poll
            var (outcome, status) = SolveWait.UntilSolveEnds(host.Derive, TimeSpan.FromSeconds(15), host.Sleep, () => host.Now);
            Assert.Equal(SolveWait.Outcome.StillSolving, outcome);
            Assert.True(status.Solving);
            Assert.True(host.Now - T0 >= TimeSpan.FromSeconds(15));
        }

        [Fact]
        public void UntilSolveEnds_ADialogThatHolds()
        {
            var host = new Host(Gap());
            var (outcome, _) = SolveWait.UntilSolveEnds(host.Derive, TimeSpan.FromSeconds(15), host.Sleep, () => host.Now);
            Assert.Equal(SolveWait.Outcome.Dialog, outcome);
            Assert.True(host.Now - T0 < TimeSpan.FromSeconds(3));   // believed after about 2 s, not after the whole wait
        }

        [Fact]
        public void UntilSolveEnds_FreeAtOnceWhenNothingIsSolving()
        {
            var host = new Host(Free());
            var (outcome, _) = SolveWait.UntilSolveEnds(host.Derive, TimeSpan.FromSeconds(15), host.Sleep, () => host.Now);
            Assert.Equal(SolveWait.Outcome.Free, outcome);
            Assert.Equal(T0, host.Now);
        }
    }
}
