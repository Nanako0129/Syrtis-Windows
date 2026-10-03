namespace TokenBar.Core.Tests;

// The quota lane's gate. A synchronous schedule makes "during a flight"
// deterministic: the run itself issues the requests while it is in flight.
public class CoalescingRunTests
{
    [Fact]
    public void RequestsDuringAFlightBecomeExactlyOneFollowUp()
    {
        var runs = 0;
        CoalescingRun? lane = null;
        lane = new CoalescingRun(work => work(), () =>
        {
            runs++;
            if (runs == 1)
            {
                // e.g. the Allow click and the store's Changed path.
                lane!.Request();
                lane.Request();
                lane.Request();
            }
        });

        lane.Request();

        Assert.Equal(2, runs);
    }

    [Fact]
    public void NoRequestDuringAFlightMeansNoFollowUp()
    {
        var runs = 0;
        var lane = new CoalescingRun(work => work(), () => runs++);

        lane.Request();
        Assert.Equal(1, runs);

        // The lane is idle again: a later request starts a fresh run.
        lane.Request();
        Assert.Equal(2, runs);
    }
}
