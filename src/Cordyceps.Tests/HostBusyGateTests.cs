using System;
using System.Collections.Generic;
using Cordyceps.Core;
using Xunit;

namespace Cordyceps.Tests
{
    public class HostBusyGateTests
    {
        private static readonly DateTime Now = new DateTime(2026, 10, 16, 1, 30, 0, DateTimeKind.Utc);

        private static HostStatus Idle() => new HostStatus { Ui = UiLiveness.Responsive };

        private static HostStatus Solving(TimeSpan age, UiLiveness ui = UiLiveness.Blocked) => new HostStatus
        {
            Ui = ui,
            Solving = true,
            SolvingSince = Now - age,
            SolvingDocumentName = "unnamed"
        };

        [Fact]
        public void IsBusy_IdleHost_IsFree()
        {
            Assert.False(HostBusyGate.IsBusy(Idle(), Now));
            Assert.False(HostBusyGate.IsBusy(new HostStatus { Ui = UiLiveness.Unknown }, Now));
            Assert.False(HostBusyGate.IsBusy(null, Now));
        }

        [Fact]
        public void IsBusy_BlockedOrOurOwnWork_IsBusy()
        {
            Assert.True(HostBusyGate.IsBusy(new HostStatus { Ui = UiLiveness.Blocked }, Now));
            Assert.True(HostBusyGate.IsBusy(new HostStatus { Ui = UiLiveness.Responsive, UiWorkInProgress = true }, Now));
            Assert.True(HostBusyGate.IsBusy(Solving(TimeSpan.FromMinutes(5)), Now));
        }

        [Fact]
        public void IsBusy_SolveThatJustStarted_IsBusyBeforeTheHeartbeatGoesStale()
        {
            Assert.True(HostBusyGate.IsBusy(Solving(TimeSpan.FromSeconds(1), UiLiveness.Responsive), Now));
        }

        [Fact]
        public void IsBusy_OldSolvingFlagOnAResponsiveHost_IsFree()
        {
            // A missed end-of-solve event must not refuse every call from then on.
            Assert.False(HostBusyGate.IsBusy(Solving(TimeSpan.FromMinutes(3), UiLiveness.Responsive), Now));
        }

        [Fact]
        public void WaitUntilFree_ReturnsOnceTheSolveEnds()
        {
            var t = Now;
            var states = new Queue<HostStatus>(new[] { Solving(TimeSpan.FromSeconds(2)), Solving(TimeSpan.FromSeconds(3)), Idle() });
            int sleeps = 0;
            HostBusyGate.WaitUntilFree(() => states.Dequeue(), HostBusyGate.MaxWait, d => { sleeps++; t += d; }, () => t);
            Assert.Equal(2, sleeps);
        }

        [Fact]
        public void WaitUntilFree_StillBusyAfterTheWait_SaysWhatIsRunning()
        {
            var t = Now;
            var ex = Assert.Throws<HostBusyException>(() => HostBusyGate.WaitUntilFree(
                () => new HostStatus { Ui = UiLiveness.Blocked, Solving = true, SolvingSince = Now - TimeSpan.FromSeconds(185), SolvingDocumentName = "unnamed" },
                TimeSpan.FromSeconds(15), d => t += d, () => t));

            Assert.StartsWith("Grasshopper has been solving 'unnamed' for 3 min 20 s, so this call stopped before reaching it.", ex.Message);
            Assert.Contains("gh_inspect(action='connection')", ex.Message);
            Assert.Contains("close Rhino", ex.Message);
        }

        [Fact]
        public void Message_EarlierCallAndDialog_SayWhichItIs()
        {
            var earlier = HostBusyGate.Message(new HostStatus { Ui = UiLiveness.Blocked, UiWorkInProgress = true, HeartbeatAgeMs = 20000 }, Now);
            Assert.StartsWith("Grasshopper is still busy with an earlier call for 20 s", earlier);

            var dialog = HostBusyGate.Message(new HostStatus { Ui = UiLiveness.Blocked, HeartbeatAgeMs = 42000 }, Now);
            Assert.StartsWith("Rhino hasn't responded for 42 s and nothing is solving: a dialog is probably open", dialog);
        }

        [Theory]
        [InlineData(8, "8 s")]
        [InlineData(60, "1 min")]
        [InlineData(200, "3 min 20 s")]
        public void Duration_IsShortAndPlain(int seconds, string expected)
        {
            Assert.Equal(expected, HostBusyGate.Duration(TimeSpan.FromSeconds(seconds)));
        }
    }
}
