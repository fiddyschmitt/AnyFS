using AnyFS.Sdk.Testing;
using AnyFS.Sources.Common;

namespace AnyFS.Sources.Tests;

/// <summary>
/// The sign-in a browser session just read from a profile, taken by the other sources of the host as they start
/// (<see cref="JustReadSignIns"/>): each launched the browser for itself, one after another.
/// </summary>
[TestClass]
public class JustReadSignInsTests
{
    private static readonly IReadOnlyList<SessionCookie> Cookies = [new SessionCookie("FedAuth", "value", "contoso.sharepoint.com", "/", true, true)];

    [TestMethod]
    public void WhatOneLaunchReadServesTheNextStartOfTheHostForAMinute()
    {
        var time = new FakeTime();
        var read = new JustReadSignIns(time: time);
        read.Remember(@"C:\profiles\contoso.sharepoint.com", "contoso.sharepoint.com", Cookies);

        time.Advance(TimeSpan.FromSeconds(59));
        Assert.AreSame(Cookies, read.Find(@"C:\profiles\contoso.sharepoint.com", "contoso.sharepoint.com"));
        Assert.AreSame(Cookies, read.Find(@"C:\profiles\contoso.sharepoint.com", "contoso.sharepoint.com"), "for every source that starts in the minute");

        time.Advance(TimeSpan.FromSeconds(2));
        Assert.IsNull(read.Find(@"C:\profiles\contoso.sharepoint.com", "contoso.sharepoint.com"), "after it, the browser is asked again");
    }

    [TestMethod]
    public void ItIsTheProfilesAndTheHostsAlone()
    {
        var read = new JustReadSignIns(time: new FakeTime());
        read.Remember(@"C:\profiles\contoso.sharepoint.com", "contoso.sharepoint.com", Cookies);

        Assert.IsNull(read.Find(@"C:\profiles\fabrikam.sharepoint.com", "contoso.sharepoint.com"), "another profile is another account");
        Assert.IsNull(read.Find(@"C:\profiles\contoso.sharepoint.com", "contoso-my.sharepoint.com"), "another host has cookies of its own");
    }

    [TestMethod]
    public void ALaterReadTakesThePlaceOfAnEarlierOneAndNothingReadIsNothingKept()
    {
        var read = new JustReadSignIns(time: new FakeTime());
        IReadOnlyList<SessionCookie> renewed = [new SessionCookie("FedAuth", "renewed", "contoso.sharepoint.com", "/", true, true)];
        read.Remember("profile", "host", Cookies);
        read.Remember("profile", "host", renewed);
        Assert.AreSame(renewed, read.Find("profile", "host"), "a renewal's reading is the one the next start takes");

        read.Remember("other", "host", []);
        Assert.IsNull(read.Find("other", "host"));
    }
}
