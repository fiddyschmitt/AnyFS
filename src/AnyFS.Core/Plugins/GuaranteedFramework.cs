namespace AnyFS.Core.Plugins;

/// <summary>
/// The framework assemblies the trimmed host keeps whole for plugins (spec 14.1, decision N4), as
/// src/AnyFS.App/AnyFS.App.csproj roots them (a test holds the two lists together) and
/// docs/plugin-authoring.md lists them. <c>anyfs plugin check</c> scans a plugin's references against
/// this: a framework assembly outside it is missing from the host, and a plugin can't ship one.
/// </summary>
public static class GuaranteedFramework
{
    public static readonly IReadOnlyList<string> Assemblies =
    [
        "System.Private.CoreLib",
        "System.Runtime",
        "System.Collections",
        "System.Collections.Concurrent",
        "System.Linq",
        "System.Memory",
        "System.Threading",
        "System.Threading.Tasks",
        "System.IO.Compression",
        "System.IO.Pipelines",
        "System.Net.Http",
        "System.Net.Primitives",
        "System.Text.Json",
        "System.Text.RegularExpressions",
        "System.Security.Cryptography",
        "System.Diagnostics.Process",
        "System.ComponentModel.Primitives",
        "Microsoft.Extensions.Logging.Abstractions",
    ];

    private static readonly HashSet<string> Set = new(Assemblies, StringComparer.OrdinalIgnoreCase);

    public static bool Contains(string assemblyName) => Set.Contains(assemblyName);

    /// <summary>A .NET framework assembly (as opposed to a NuGet package's), by name.</summary>
    public static bool IsFrameworkAssembly(string assemblyName)
        => assemblyName.StartsWith("System.", StringComparison.Ordinal)
           || assemblyName is "System" or "mscorlib" or "netstandard" or "WindowsBase" or "Microsoft.CSharp" or "Microsoft.VisualBasic" or "Microsoft.VisualBasic.Core"
           || assemblyName.StartsWith("Microsoft.Win32.", StringComparison.Ordinal);
}
