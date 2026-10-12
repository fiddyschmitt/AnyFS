using System.Text;
using AnyFS.Sdk.Testing;

namespace AnyFS.Sdk.Tests;

/// <summary>The contract suite run against the in-memory fake, which also proves the suite itself works.</summary>
[TestClass]
public class MemorySourceContractTests : SourceContractTests
{
    protected override async Task<SourceContractSetup> SetUpAsync()
    {
        var contents = Encoding.ASCII.GetBytes("the quick brown fox jumps over the lazy dog, twice over");
        var source = new MemorySource(canWrite: true)
            .AddFile("docs/contract.txt", contents)
            .AddFile("readme.txt", "hi")
            .AddDirectory("scratch");
        await source.InitialiseAsync(new TestSourceContext("memory"), CancellationToken.None);
        return new SourceContractSetup(source, "docs", "docs/contract.txt", contents, WritableDirectory: "scratch");
    }
}

[TestClass]
public class ReadOnlyMemorySourceContractTests : SourceContractTests
{
    protected override async Task<SourceContractSetup> SetUpAsync()
    {
        var contents = Encoding.ASCII.GetBytes("read-only contents, sixteen bytes or more");
        var source = new MemorySource(canWrite: false).AddFile("file.bin", contents);
        await source.InitialiseAsync(new TestSourceContext("memory-ro"), CancellationToken.None);
        return new SourceContractSetup(source, "", "file.bin", contents);
    }
}

[TestClass]
public class MemorySourceTests
{
    [TestMethod]
    public async Task CaseInsensitiveLookups()
    {
        var source = new MemorySource(caseInsensitive: true).AddFile("Docs/Readme.TXT", "x");
        Assert.IsNotNull(await source.StatAsync("docs/readme.txt", CancellationToken.None));
        Assert.IsTrue(source.Capabilities.CaseInsensitive);
    }

    [TestMethod]
    public async Task HooksForHostTests()
    {
        var source = new MemorySource();
        var seen = false;
        source.OnInitialise = (ctx, ct) =>
        {
            seen = true;
            ctx.ReportProgress("hello", 0.5);
            return Task.CompletedTask;
        };
        using var context = new TestSourceContext();
        await source.InitialiseAsync(context, CancellationToken.None);
        Assert.IsTrue(seen);
        Assert.AreEqual(1, source.InitialiseCalls);
        Assert.AreEqual(("hello", (double?)0.5), context.Progress.Single());
        await source.DisposeAsync();
        Assert.IsTrue(source.Disposed);
    }
}
