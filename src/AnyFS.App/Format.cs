namespace AnyFS.App;

/// <summary>Number formatting shared by the window and the command line.</summary>
public static class Format
{
    public static string Bytes(long n)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double v = n;
        var u = 0;
        while (v >= 1024 && u < units.Length - 1)
        {
            v /= 1024;
            u++;
        }

        return u == 0 ? $"{n} B" : $"{v:0.#} {units[u]}";
    }

    /// <summary>"1,204+ files" for a lower bound, "318 files" when exact, "1 file".</summary>
    public static string Files(long files, bool complete)
        => $"{files:N0}{(complete ? "" : "+")} {(files == 1 && complete ? "file" : "files")}";
}
