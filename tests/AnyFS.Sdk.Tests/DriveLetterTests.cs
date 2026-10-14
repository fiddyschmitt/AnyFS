namespace AnyFS.Sdk.Tests;

[TestClass]
public class DriveLetterTests
{
    [TestMethod]
    public void NormalisesTheUsualSpellings()
    {
        Assert.AreEqual("X", DriveLetter.Normalise("x"));
        Assert.AreEqual("X", DriveLetter.Normalise(" X: "));
        Assert.AreEqual("X", DriveLetter.Normalise(@"x:\"));
        Assert.AreEqual("D", DriveLetter.Normalise("d:/"));
        Assert.IsNull(DriveLetter.Normalise("C"), "A to C are never offered");
        Assert.IsNull(DriveLetter.Normalise(""));
        Assert.IsNull(DriveLetter.Normalise(null));
        Assert.IsNull(DriveLetter.Normalise("XY"));
        Assert.IsNull(DriveLetter.Normalise("1"));
        Assert.AreEqual(@"X:\", DriveLetter.Root("X"));
    }

    [TestMethod]
    public void ValidatesOnlyWhatIsThere()
    {
        Assert.IsNull(DriveLetter.Validate(null, "Letter"));
        Assert.IsNull(DriveLetter.Validate("  ", "Letter"));
        Assert.IsNull(DriveLetter.Validate("y:", "Letter"));
        var message = DriveLetter.Validate("1", "Letter");
        Assert.IsNotNull(message);
        Assert.AreEqual("Letter", message.Property);
        Assert.IsTrue(message.IsError);
    }
}
