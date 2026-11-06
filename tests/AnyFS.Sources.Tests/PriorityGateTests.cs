using AnyFS.Sources.Common;

namespace AnyFS.Sources.Tests;

/// <summary>The gate a source's requests go through: a client's takes the next free slot ahead of a walk's, however many of those wait.</summary>
[TestClass]
public class PriorityGateTests
{
    [TestMethod]
    public async Task AnUrgentRequestTakesTheNextSlotAheadOfThoseWaitingBeforeIt()
    {
        var gate = new PriorityGate(1);
        await gate.WaitAsync(urgent: false, CancellationToken.None);
        var walk1 = gate.WaitAsync(urgent: false, CancellationToken.None);
        var walk2 = gate.WaitAsync(urgent: false, CancellationToken.None);
        var client = gate.WaitAsync(urgent: true, CancellationToken.None);
        Assert.IsFalse(walk1.IsCompleted || walk2.IsCompleted || client.IsCompleted, "the slot is taken");

        gate.Release();
        await client.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsFalse(walk1.IsCompleted || walk2.IsCompleted, "the client's went first, though it asked last");

        gate.Release();
        await walk1.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsFalse(walk2.IsCompleted, "then the walk's, oldest first");
        gate.Release();
        await walk2.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public async Task AFreeSlotGoesToWhoeverAsksAndAWaiterThatGaveUpPassesItsTurnOn()
    {
        var gate = new PriorityGate(2);
        Assert.IsTrue(gate.WaitAsync(urgent: false, CancellationToken.None).IsCompleted, "free: the walk's goes at once");
        Assert.IsTrue(gate.WaitAsync(urgent: true, CancellationToken.None).IsCompleted);

        using var gaveUp = new CancellationTokenSource();
        var cancelled = gate.WaitAsync(urgent: true, gaveUp.Token);
        var next = gate.WaitAsync(urgent: false, CancellationToken.None);
        gaveUp.Cancel();
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => cancelled);

        gate.Release();
        await next.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsTrue(next.IsCompletedSuccessfully, "the slot passed the one that gave up");
    }
}
