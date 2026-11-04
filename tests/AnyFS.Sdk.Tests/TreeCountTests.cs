using System.Collections.Concurrent;
using AnyFS.Sdk.Testing;

namespace AnyFS.Sdk.Tests;

[TestClass]
public class TreeCountTests
{
    /// <summary>A tree four folders wide and three deep (84 folders), each holding two files, listed with a delay that stands in for a round trip.</summary>
    private sealed class SlowTree
    {
        private int _inFlight;

        public int MostInFlight;

        public readonly HashSet<string> Listed = [];

        public async Task<(long Files, IReadOnlyList<string> Folders)> ListAsync(string dir, CancellationToken ct)
        {
            var now = Interlocked.Increment(ref _inFlight);
            InterlockedMax(ref MostInFlight, now);
            try
            {
                await Task.Delay(20, ct);
                lock (Listed)
                {
                    Assert.IsTrue(Listed.Add(dir), $"{dir} listed twice");
                }

                var depth = dir.Length == 0 ? 0 : dir.Split('/').Length;
                IReadOnlyList<string> folders = depth < 3 ? ["a", "b", "c", "d"] : [];
                return (2, folders);
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }

        private static void InterlockedMax(ref int target, int value)
        {
            int seen;
            while ((seen = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, seen) != seen)
            {
            }
        }
    }

    private static async Task UntilAsync(Func<bool> done, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!done())
        {
            Assert.IsTrue(DateTime.UtcNow < deadline, what);
            await Task.Delay(5);
        }
    }

    [TestMethod]
    public async Task EveryFolderIsCountedOnceWithSeveralListingsInFlight()
    {
        var tree = new SlowTree();
        var counters = new SourceCounters();
        await TreeCount.CountAsync(tree.ListAsync, counters, parallelism: 8, skipped: null, CancellationToken.None);
        Assert.AreEqual(84, counters.DirectoriesKnown, "4 + 16 + 64 folders under the root");
        Assert.AreEqual(85 * 2, counters.FilesKnown, "two files in the root and in every folder");
        Assert.AreEqual(85, tree.Listed.Count);
        Assert.IsTrue(tree.MostInFlight > 1, "listings overlapped");
        Assert.IsTrue(tree.MostInFlight <= 8, $"never more than asked for: {tree.MostInFlight}");

        var one = new SlowTree();
        await TreeCount.CountAsync(one.ListAsync, new SourceCounters(), parallelism: 1, skipped: null, CancellationToken.None);
        Assert.AreEqual(1, one.MostInFlight, "one at a time when asked");
    }

    [TestMethod]
    public async Task AFolderThatCannotBeListedIsSkippedWithItsSubtree()
    {
        var skipped = new List<string>();
        var counters = new SourceCounters();
        await TreeCount.CountAsync(
            (dir, _) => dir switch
            {
                "" => Task.FromResult((1L, (IReadOnlyList<string>)["open", "locked"])),
                "locked" => throw VfsException.AccessDenied("locked"),
                "open" => Task.FromResult((3L, (IReadOnlyList<string>)["inner"])),
                _ => Task.FromResult((5L, (IReadOnlyList<string>)[])),
            },
            counters, parallelism: 4, skipped: (dir, _) => { lock (skipped) { skipped.Add(dir); } }, CancellationToken.None);
        CollectionAssert.AreEqual(new[] { "locked" }, skipped);
        Assert.AreEqual(1 + 3 + 5, counters.FilesKnown);
        Assert.AreEqual(3, counters.DirectoriesKnown, "open, locked and inner were seen; nothing under locked");
    }

    [TestMethod]
    public async Task AnUnexpectedErrorStopsTheWalk()
    {
        var listed = 0;
        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => TreeCount.CountAsync(
            async (dir, ct) =>
            {
                Interlocked.Increment(ref listed);
                if (dir == "a")
                {
                    throw new InvalidOperationException("broken");
                }

                await Task.Delay(10, ct);
                return (0L, dir.Length < 6 ? (IReadOnlyList<string>)[dir.Length == 0 ? "a" : dir + "/x", dir + "y"] : []);
            },
            new SourceCounters(), parallelism: 2, skipped: null, CancellationToken.None));
        Assert.AreEqual("broken", ex.Message);
        Assert.IsTrue(listed < 20, $"the walk stopped instead of finishing the tree: {listed} listings");
    }

    [TestMethod]
    public async Task ItStopsWhenCancelled()
    {
        using var cts = new CancellationTokenSource();
        var tree = new SlowTree();
        var walk = TreeCount.CountAsync(tree.ListAsync, new SourceCounters(), parallelism: 4, skipped: null, cts.Token);
        await Task.Delay(30);
        await cts.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => walk);
        Assert.IsTrue(tree.Listed.Count < 85, "it did not run to the end");
    }

    [TestMethod]
    public void TheFolderACountIsInIsTheOneItWentIntoFirstOfThoseStillGoing()
    {
        var counters = new SourceCounters();
        Assert.IsNull(counters.CurrentFolder, "no count going");
        var first = counters.EnterFolder("first");
        var second = counters.EnterFolder("second");
        var third = counters.EnterFolder("third");
        Assert.AreEqual("first", counters.CurrentFolder);
        second.Dispose();
        Assert.AreEqual("first", counters.CurrentFolder, "one done meanwhile changes nothing");
        first.Dispose();
        first.Dispose();
        Assert.AreEqual("third", counters.CurrentFolder, "the oldest left");
        third.Dispose();
        Assert.IsNull(counters.CurrentFolder, "none left");
        using (counters.EnterFolder(""))
        {
            Assert.AreEqual("", counters.CurrentFolder, "the root is a folder too");
        }

        Assert.IsNull(counters.CurrentFolder);
    }

    [TestMethod]
    public async Task ManyThreadsGoingInAndOutOfFoldersLeaveNothingBehind()
    {
        var counters = new SourceCounters();
        var stop = false;
        var reader = Task.Run(() =>
        {
            while (!Volatile.Read(ref stop))
            {
                _ = counters.CurrentFolder;
            }
        });
        await Task.WhenAll(Enumerable.Range(0, 32).Select(thread => Task.Run(() =>
        {
            for (var i = 0; i < 20_000; i++)
            {
                using (counters.EnterFolder($"t{thread}"))
                {
                    Assert.IsNotNull(counters.CurrentFolder, "at least this one");
                }
            }
        })));
        Volatile.Write(ref stop, true);
        await reader;
        Assert.IsNull(counters.CurrentFolder);
    }

    [TestMethod]
    public async Task TheCountSaysWhichFolderItHasBeenInLongest()
    {
        var gates = new Dictionary<string, TaskCompletionSource> { [""] = new(), ["a"] = new(), ["b"] = new() };
        var asked = new ConcurrentDictionary<string, bool>();
        var counters = new SourceCounters();
        var walk = TreeCount.CountAsync(
            async (dir, ct) =>
            {
                asked[dir] = true;
                await gates[dir].Task.WaitAsync(ct);
                return (1L, dir.Length == 0 ? (IReadOnlyList<string>)["a", "b"] : []);
            },
            counters, parallelism: 2, skipped: null, CancellationToken.None);
        await UntilAsync(() => asked.ContainsKey(""), "the root asked for");
        Assert.AreEqual("", counters.CurrentFolder, "the root, while it is listed");
        gates[""].SetResult();
        await UntilAsync(() => asked.ContainsKey("a") && asked.ContainsKey("b"), "both folders asked for");

        // whichever of the two a worker went into first stays while the other ends; the total goes on
        var longest = counters.CurrentFolder;
        Assert.IsTrue(longest is "a" or "b", $"in '{longest}'");
        gates[longest == "a" ? "b" : "a"].SetResult();
        await UntilAsync(() => counters.FilesKnown == 2, "the other folder counted");
        Assert.AreEqual(longest, counters.CurrentFolder, "the slow one is what shows");
        gates[longest!].SetResult();
        await walk;
        Assert.IsNull(counters.CurrentFolder, "the count is over");
    }

    [TestMethod]
    public async Task ACountThatStopsIsInNoFolder()
    {
        var skipped = new SourceCounters();
        await TreeCount.CountAsync(
            (dir, _) => dir.Length == 0 ? Task.FromResult((0L, (IReadOnlyList<string>)["locked"])) : throw VfsException.AccessDenied(dir),
            skipped, parallelism: 2, skipped: null, CancellationToken.None);
        Assert.IsNull(skipped.CurrentFolder, "a folder that could not be listed");

        var broken = new SourceCounters();
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => TreeCount.CountAsync((_, _) => throw new InvalidOperationException("broken"), broken, parallelism: 2, skipped: null, CancellationToken.None));
        Assert.IsNull(broken.CurrentFolder, "a walk that broke");

        using var cts = new CancellationTokenSource();
        var cancelled = new SourceCounters();
        var tree = new SlowTree();
        var walk = TreeCount.CountAsync(tree.ListAsync, cancelled, parallelism: 4, skipped: null, cts.Token);
        await UntilAsync(() => cancelled.CurrentFolder is not null, "in a folder");
        await cts.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => walk);
        Assert.IsNull(cancelled.CurrentFolder, "a walk cancelled");
    }

    [TestMethod]
    public async Task ASourcesOwnListingIsWalked()
    {
        var plugin = new MemoryPlugin();
        await using var source = (MemorySource)plugin.CreateSource(new MemoryPluginSettings { Files = "a.txt=1;b/c.txt=2;b/d/e.txt=3;f/g.txt=4" });
        using var context = new TestSourceContext("memory");
        await source.InitialiseAsync(context, CancellationToken.None);
        var counters = new SourceCounters();
        await TreeCount.CountAsync(source, counters, TreeCount.DefaultParallelism, skipped: null, CancellationToken.None);
        Assert.AreEqual(4, counters.FilesKnown);
        Assert.AreEqual(3, counters.DirectoriesKnown, "b, b/d and f");
    }
}
