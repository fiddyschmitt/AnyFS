namespace LibOutlook.Tests;

/// <summary>Where the test files are.</summary>
internal static class Fixtures
{
    /// <summary>A file shipped with the tests (tests/LibOutlook.Tests/fixtures).</summary>
    public static string Path(string name) => System.IO.Path.Combine(AppContext.BaseDirectory, "fixtures", name);

    /// <summary>
    /// The 4 KiB-page OST captured from the lab mailbox, when it is on this machine: the path in LIBOUTLOOK_TEST_OST, or
    /// the capture next to the repository ("AnyFS local\spikes\Spike13.OstProbe\fixtures\anyfs-tester-clean.ost"). Null
    /// when neither is there; the tests that need it are then inconclusive.
    /// </summary>
    public static string? Ost()
    {
        var env = Environment.GetEnvironmentVariable("LIBOUTLOOK_TEST_OST");
        if (!string.IsNullOrEmpty(env))
        {
            return File.Exists(env) ? env : null;
        }

        return LabCapture("anyfs-tester-clean.ost");
    }

    /// <summary>The OST copied after Outlook quit, whatever LIBOUTLOOK_TEST_OST says, when the capture is on this machine.</summary>
    public static string? CleanOst() => LabCapture("anyfs-tester-clean.ost");

    /// <summary>The same OST copied while Outlook had it open (through a mapped view), when the capture is on this machine.</summary>
    public static string? LiveOst() => LabCapture("anyfs-tester-live.ost");

    private static string? LabCapture(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(System.IO.Path.Combine(dir.FullName, "AnyFS.slnx")))
        {
            dir = dir.Parent;
        }

        if (dir?.Parent is null)
        {
            return null;
        }

        var candidate = System.IO.Path.Combine(dir.Parent.FullName, "AnyFS local", "spikes", "Spike13.OstProbe", "fixtures", name);
        return File.Exists(candidate) ? candidate : null;
    }
}
