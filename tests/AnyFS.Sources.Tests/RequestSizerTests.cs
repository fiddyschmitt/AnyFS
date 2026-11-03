using AnyFS.Sources.Common;

namespace AnyFS.Sources.Tests;

[TestClass]
public class RequestSizerTests
{
    [TestMethod]
    public void MoreWhileRequestsAreQuickFewerWhenTheyTakeLongOrFail()
    {
        var sizer = new RequestSizer(smallest: 1, start: 10, largest: 100);
        Assert.AreEqual(10, sizer.Size);
        foreach (var next in new[] { 20, 40, 80, 100, 100 })
        {
            sizer.Took(TimeSpan.FromSeconds(1), sizer.Size);
            Assert.AreEqual(next, sizer.Size);
        }

        sizer.Took(TimeSpan.FromSeconds(10), 100);
        Assert.AreEqual(100, sizer.Size, "a fair time: as it is");
        sizer.Took(TimeSpan.FromSeconds(40), 100);
        Assert.AreEqual(50, sizer.Size);
        sizer.Failed(50);
        Assert.AreEqual(12, sizer.Size);
        sizer.Failed(2);
        Assert.AreEqual(1, sizer.Size, "never fewer than the smallest");
        sizer.Took(TimeSpan.FromSeconds(40), 1);
        Assert.AreEqual(1, sizer.Size);
        sizer.Took(TimeSpan.FromSeconds(1), 1);
        Assert.AreEqual(2, sizer.Size, "and up again");
    }
}
