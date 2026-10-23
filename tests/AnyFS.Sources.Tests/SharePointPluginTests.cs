using System.Buffers.Binary;
using AnyFS.Sources.SharePoint;

namespace AnyFS.Sources.Tests;

/// <summary>What the SharePoint type shows in the window.</summary>
[TestClass]
public class SharePointPluginTests
{
    [TestMethod]
    public void TheIconIsSharePointsLogoAsA64PixelPng()
    {
        using var icon = new SharePointPlugin().OpenIcon();
        Assert.IsNotNull(icon, "embedded in the plugin");
        var header = new byte[24];
        icon.ReadExactly(header);
        CollectionAssert.AreEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, header[..8], "a PNG: the window draws no SVG yet");
        Assert.AreEqual(64, BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(16)), "width");
        Assert.AreEqual(64, BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(20)), "height");
    }
}
