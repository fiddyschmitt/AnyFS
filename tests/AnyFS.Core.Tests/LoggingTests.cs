using System.Text;
using AnyFS.Core.Logging;
using Microsoft.Extensions.Logging;

namespace AnyFS.Core.Tests;

[TestClass]
public class LoggingTests
{
    private string _root = "";

    [TestInitialize]
    public void Init()
    {
        _root = Path.Combine(Path.GetTempPath(), "anyfs-tests", "log-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
        }
    }

    [TestMethod]
    public void RollingStreamRotatesAndKeepsFive()
    {
        var path = Path.Combine(_root, "anyfs.log");
        var line = Encoding.ASCII.GetBytes(new string('x', 99) + "\n");
        using (var stream = new RollingLogStream(path, maxBytes: 1000, keepFiles: 5))
        {
            for (var i = 0; i < 100; i++)
            {
                stream.Write(line);
            }

            stream.Flush();
        }

        var files = Directory.GetFiles(_root).Select(Path.GetFileName).OrderBy(f => f).ToList();
        CollectionAssert.AreEqual(new[] { "anyfs.1.log", "anyfs.2.log", "anyfs.3.log", "anyfs.4.log", "anyfs.log" }, files);
        Assert.IsTrue(new FileInfo(path).Length <= 1000);
        Assert.AreEqual(1000, new FileInfo(Path.Combine(_root, "anyfs.1.log")).Length, "a rolled file holds a full ten lines");
        Assert.AreEqual(Path.Combine(_root, "anyfs.3.log"), RollingLogStream.RolledName(path, 3));
        Assert.AreEqual(path, RollingLogStream.RolledName(path, 0));
    }

    [TestMethod]
    public void RollingStreamAppendsToAnExistingFile()
    {
        var path = Path.Combine(_root, "anyfs.log");
        File.WriteAllText(path, "old\n");
        using (var stream = new RollingLogStream(path, 1000, 5))
        {
            stream.Write("new\n"u8);
        }

        Assert.AreEqual("old\nnew\n", File.ReadAllText(path));
    }

    [TestMethod]
    public void FactoryWritesPrefixedLinesToTheFile()
    {
        var path = Path.Combine(_root, "sub", "anyfs.log");
        using (var factory = LoggingSetup.Create(LogLevel.Debug, path, console: false))
        {
            var logger = factory.CreateLogger("AnyFS.Test");
            logger.LogInformation("hello {Name}", "world");
            logger.LogDebug("debug line");
            logger.LogTrace("trace line is filtered");
            factory.CreateLogger("Microsoft.Something").LogInformation("framework noise is filtered");
            try
            {
                throw new InvalidOperationException("boom");
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "with exception");
            }
        }

        var text = File.ReadAllText(path);
        StringAssert.Contains(text, "INFO  AnyFS.Test hello world");
        StringAssert.Contains(text, "DEBUG AnyFS.Test debug line");
        StringAssert.Contains(text, "WARN  AnyFS.Test with exception");
        StringAssert.Contains(text, "InvalidOperationException: boom");
        Assert.IsFalse(text.Contains("trace line", StringComparison.Ordinal));
        Assert.IsFalse(text.Contains("framework noise", StringComparison.Ordinal));
        StringAssert.Matches(text, new System.Text.RegularExpressions.Regex(@"^\d{4}-\d\d-\d\d \d\d:\d\d:\d\d\.\d{3} "));
    }

    [TestMethod]
    public void LevelParsing()
    {
        Assert.AreEqual(LogLevel.Trace, LoggingSetup.ParseLevel("trace"));
        Assert.AreEqual(LogLevel.Debug, LoggingSetup.ParseLevel("DEBUG"));
        Assert.AreEqual(LogLevel.Information, LoggingSetup.ParseLevel("info"));
        Assert.AreEqual(LogLevel.Warning, LoggingSetup.ParseLevel("warn"));
        Assert.AreEqual(LogLevel.Error, LoggingSetup.ParseLevel("error"));
        Assert.AreEqual(LogLevel.Information, LoggingSetup.ParseLevel(null));
        Assert.AreEqual(LogLevel.Information, LoggingSetup.ParseLevel("bogus"));
        Assert.IsTrue(LoggingSetup.IsValidLevel("warning"));
        Assert.IsFalse(LoggingSetup.IsValidLevel("bogus"));
    }
}
