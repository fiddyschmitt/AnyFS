using System.Text;

namespace LibOutlook.Tests;

[TestClass]
public class RtfCompressionTests
{
    /// <summary>[MS-OXRTFCP] 3.1: the specification's own compressed example.</summary>
    [TestMethod]
    public void Decompresses_the_specifications_example()
    {
        var compressed = Convert.FromHexString("2d0000002b0000004c5a4675f1c5c7a703000a00726370673132354232" + "0af32068656c09002062770 5b06c647d0a800fa0".Replace(" ", ""));
        var rtf = RtfCompression.DecompressToString(compressed);
        Assert.AreEqual("{\\rtf1\\ansi\\ansicpg1252\\pard hello world}\r\n", rtf);
    }

    [TestMethod]
    public void Returns_uncompressed_data_as_is()
    {
        var text = Encoding.ASCII.GetBytes("{\\rtf1\\ansi test}\r\n");
        var data = new byte[16 + text.Length];
        BitConverter.GetBytes(12 + text.Length).CopyTo(data, 0);
        BitConverter.GetBytes(text.Length).CopyTo(data, 4);
        Encoding.ASCII.GetBytes("MELA").CopyTo(data, 8);
        text.CopyTo(data, 16);
        CollectionAssert.AreEqual(text, RtfCompression.Decompress(data));
    }

    [TestMethod]
    public void Refuses_a_wrong_checksum()
    {
        var compressed = Convert.FromHexString("2d0000002b0000004c5a467500000000" + "03000a007263706731323542320af32068656c0900206277" + "05b06c647d0a800fa0");
        var ex = Assert.ThrowsExactly<PstException>(() => RtfCompression.Decompress(compressed));
        StringAssert.Contains(ex.Message, "checksum");
    }

    [TestMethod]
    public void Refuses_an_unknown_signature()
    {
        var data = new byte[16];
        BitConverter.GetBytes(12).CopyTo(data, 0);
        var ex = Assert.ThrowsExactly<PstException>(() => RtfCompression.Decompress(data));
        StringAssert.Contains(ex.Message, "signature");
    }
}
