using AnyFS.Sdk.Testing;
using AnyFS.Sources.Common;
using Microsoft.Extensions.Logging.Abstractions;

namespace AnyFS.Sources.Tests;

/// <summary>
/// Where a session keeps a sign-in and where it looks for one (<see cref="KeptSignIn"/>): a source in its own store, a
/// dialog's Test for the source the dialog starts, and nothing of one account's where another's is looked for.
/// </summary>
[TestClass]
public class KeptSignInTests
{
    private static readonly Uri Server = new("https://server.test/");

    private static KeptSignIn Test(string product, FakeTime? time = null) => new(null, "sign-in", product, NullLogger.Instance, time);

    private static KeptSignIn Source(TestSourceContext context, string product, FakeTime? time = null) => new(context.Secrets, "sign-in", product, NullLogger.Instance, time);

    // each test under a product of its own: what a Test leaves is the process's, not the test's
    private static string Product([System.Runtime.CompilerServices.CallerMemberName] string test = "") => test;

    [TestMethod]
    public void WhatTheTestLeavesGoesToTheSourceStartedFromItAndToThatOneAlone()
    {
        using var first = new TestSourceContext();
        using var second = new TestSourceContext();
        Test(Product()).Keep(Server, "the web session", "the test's");

        CollectionAssert.AreEqual(new[] { "the test's" }, Source(first, Product()).Find(Server, "the web session").ToArray());
        Assert.AreEqual(0, Source(second, Product()).Find(Server, "the web session").Count, "taken: the first source's from then on");
    }

    [TestMethod]
    public void ASourceKeepsItsOwnWhereNoOtherLooks()
    {
        using var mine = new TestSourceContext();
        using var other = new TestSourceContext();
        Source(mine, Product()).Keep(Server, "the account a", "a's");

        // every sign-in and renewal of a source was left where a Test leaves its own, for whoever started next
        Assert.AreEqual(0, Source(other, Product()).Find(Server, "the account a").Count);
        CollectionAssert.AreEqual(new[] { "a's" }, Source(mine, Product()).Find(Server, "the account a").ToArray(), "at its own next start");
    }

    [TestMethod]
    public void ATestLooksNowhereAndTriesTheSettingsAsTyped()
    {
        using var running = new TestSourceContext();
        Source(running, Product()).Keep(Server, "the account a", "a's");
        Test(Product()).Keep(Server, "the account a", "an earlier test's");

        Assert.AreEqual(0, Test(Product()).Find(Server, "the account a").Count);
    }

    [TestMethod]
    public void WhatIsLeftGoesOnlyToTheSameServerProductAndSignIn()
    {
        using var context = new TestSourceContext();
        Test(Product()).Keep(Server, "the account a", "a's");

        Assert.AreEqual(0, Source(context, Product()).Find(Server, "the account b").Count, "another account of the server");
        Assert.AreEqual(0, Source(context, Product()).Find(Server, "the web session").Count, "another way of signing in");
        Assert.AreEqual(0, Source(context, Product()).Find(new Uri("https://other.test/"), "the account a").Count, "another server");
        Assert.AreEqual(0, Source(context, Product() + "-other").Find(Server, "the account a").Count, "another product at the address");
        CollectionAssert.AreEqual(new[] { "a's" }, Source(context, Product()).Find(Server, "the account a").ToArray());
    }

    [TestMethod]
    public void WhatTheTestLeftIsTriedBeforeWhatTheSourceKept()
    {
        using var context = new TestSourceContext();
        Source(context, Product()).Keep(Server, "the web session", "kept at the last run");
        Test(Product()).Keep(Server, "the web session", "the test's");

        CollectionAssert.AreEqual(new[] { "the test's", "kept at the last run" }, Source(context, Product()).Find(Server, "the web session").ToArray());
    }

    [TestMethod]
    public void WhatTheTestLeftWaitsAQuarterOfAnHour()
    {
        var time = new FakeTime();
        using var context = new TestSourceContext();
        Test(Product(), time).Keep(Server, "the web session", "the test's");

        time.Advance(KeptSignIn.HandedOverFor + TimeSpan.FromSeconds(1));
        Assert.AreEqual(0, Source(context, Product(), time).Find(Server, "the web session").Count);
    }
}
