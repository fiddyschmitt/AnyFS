using AnyFS.Sdk.Testing;

namespace AnyFS.Sdk.Tests;

/// <summary>
/// What a source says of its health (<see cref="SourceBase"/>): an upstream a call could not reach, until one reaches
/// it; and whatever the source holds against itself by name, each until that is over.
/// </summary>
[TestClass]
public class SourceHealthTests
{
    private sealed class Source : SourceBase
    {
        public override async IAsyncEnumerable<VfsEntry> ListAsync(string path, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield break;
        }

        public override Task<IVfsFile> OpenAsync(string path, VfsOpenMode mode, CancellationToken ct) => throw VfsException.NotFound(path);

        public void Unreachable(string detail) => MarkDegraded(detail);

        public void Reached() => MarkHealthy();

        public void Hold(string name, string? detail) => HoldDegraded(name, detail);

        public void Hold(string? detail) => HoldDegraded(detail);
    }

    private static async Task<(Source Source, TestSourceContext Context)> StartedAsync()
    {
        var source = new Source();
        var context = new TestSourceContext();
        await source.InitialiseAsync(context, CancellationToken.None);
        return (source, context);
    }

    [TestMethod]
    public async Task EachThingHeldAgainstASourceIsSaidUntilItIsItselfOver()
    {
        // there was one such thing at a time: writes that waited took the place of sites that had not opened, and the
        // first to end cleared the tile of the other
        var (source, context) = await StartedAsync();
        using (context)
        {
            source.Hold("uploads", "1 file waits to be sent");
            source.Hold("sites", "2 of 10 sites could not be opened");
            Assert.AreEqual((ComponentHealth.Degraded, "1 file waits to be sent; 2 of 10 sites could not be opened"), context.HealthReports[^1]);

            source.Hold("uploads", null);
            Assert.AreEqual((ComponentHealth.Degraded, "2 of 10 sites could not be opened"), context.HealthReports[^1]);

            source.Hold("sites", null);
            Assert.AreEqual(ComponentHealth.Ready, context.LastHealth);
        }
    }

    [TestMethod]
    public async Task WhatIsHeldOutlastsACallThatSucceedsAndAnUpstreamOutOfReachComesFirst()
    {
        var (source, context) = await StartedAsync();
        using (context)
        {
            source.Hold("list", "the list is a day old");
            source.Unreachable("the server did not answer");
            Assert.AreEqual((ComponentHealth.Degraded, "the server did not answer; the list is a day old"), context.HealthReports[^1]);

            source.Reached();
            Assert.AreEqual((ComponentHealth.Degraded, "the list is a day old"), context.HealthReports[^1], "a call that got through does not make the list fresh");
        }
    }

    [TestMethod]
    public async Task AThingThatEndsWhileTheUpstreamIsOutOfReachLeavesTheTileToThatAlone()
    {
        // it stayed in what the tile said: nothing was reported when it ended
        var (source, context) = await StartedAsync();
        using (context)
        {
            source.Unreachable("the server did not answer");
            source.Hold("uploads", "1 file waits to be sent");
            source.Hold("uploads", null);
            Assert.AreEqual((ComponentHealth.Degraded, "the server did not answer"), context.HealthReports[^1]);

            source.Reached();
            Assert.AreEqual(ComponentHealth.Ready, context.LastHealth);
        }
    }

    [TestMethod]
    public async Task NothingIsSaidTwice()
    {
        var (source, context) = await StartedAsync();
        using (context)
        {
            source.Reached();
            source.Hold("uploads", null);
            Assert.AreEqual(0, context.HealthReports.Count, "ready from the start, and nothing to say of it");

            source.Unreachable("the server did not answer");
            source.Unreachable("it still does not");
            source.Hold("uploads", "1 file waits to be sent");
            source.Hold("uploads", "1 file waits to be sent");
            source.Hold("uploads", "2 files wait to be sent");
            CollectionAssert.AreEqual(
                new[] { "the server did not answer", "the server did not answer; 1 file waits to be sent", "the server did not answer; 2 files wait to be sent" },
                context.HealthReports.Select(r => r.Detail).ToArray(),
                "the first word of an upstream out of reach stands; a thing held is said again when it changes");
        }
    }

    [TestMethod]
    public async Task TheOneThingHeldWithoutANameIsHeldAsBefore()
    {
        var (source, context) = await StartedAsync();
        using (context)
        {
            source.Hold("3 files wait to be sent");
            source.Hold("uploads", "the library is read-only");
            Assert.AreEqual((ComponentHealth.Degraded, "3 files wait to be sent; the library is read-only"), context.HealthReports[^1]);

            source.Hold(null);
            Assert.AreEqual((ComponentHealth.Degraded, "the library is read-only"), context.HealthReports[^1]);
        }
    }
}
