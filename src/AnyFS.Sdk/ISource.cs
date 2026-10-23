using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;

namespace AnyFS.Sdk;

/// <summary>A type of source (Mirror, SSHFS...). One instance per process; it creates <see cref="ISource"/>s.</summary>
public interface ISourcePlugin
{
    /// <summary>Stable id stored in sources.json: "mirror", "sshfs".</summary>
    string TypeId { get; }

    string DisplayName { get; }

    /// <summary>One line, shown in the Add dialog.</summary>
    string Description { get; }

    /// <summary>First segment of the default source path: "files", "sshfs", "3dx".</summary>
    string TypeFolder { get; }

    /// <summary>
    /// The type's icon, or null for the default (a letter on a coloured square). A PNG with a transparent background, about
    /// 64 pixels square, is drawn alone in the letter's place; an SVG is accepted but shows as the letter until the window
    /// can draw SVG.
    /// </summary>
    Stream? OpenIcon();

    /// <summary>
    /// The settings POCO. The host reads its public properties by reflection (settings form, JSON); the
    /// annotation keeps them in trimmed builds of built-in plugins.
    /// </summary>
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicConstructors)]
    Type SettingsType { get; }

    object CreateDefaultSettings();

    ISource CreateSource(object settings);

    /// <summary>
    /// True when walking the whole tree is quick for this kind of source (local disks), so "Count files on
    /// start" is ticked by default for new sources of it. Slow or metered sources (SSH, web APIs) keep the
    /// default false. Optional: the default is false.
    /// </summary>
    bool IsQuickToCount => false;

    /// <summary>
    /// A name for a source made from its settings as they stand (the server's name in its address), or null for none
    /// yet. The Add source dialog keeps the Name box, and with it the default path, filled with it until the person
    /// types a name of their own; editing a source whose name is still the one this made does the same. It must be a
    /// valid path segment (no <c>\ / : * ? " &lt; &gt; |</c>, no leading or trailing dot or space). Optional: the
    /// default suggests nothing.
    /// </summary>
    string? SuggestName(object settings) => null;

    /// <summary>
    /// Tidies the settings before they are stored, when a source is added and when it is edited: change the object in
    /// place (the SharePoint source sorts its site addresses). The source is then created from what was stored, so it
    /// sees the same. Optional: the default leaves the settings as they are.
    /// </summary>
    void NormaliseSettings(object settings)
    {
    }
}

/// <summary>
/// A source that counts its own tree when "Count files on start" is on, instead of the host's generic
/// directory-by-directory walk: it knows a faster way (Mirror walks every configured path at once).
/// Update <see cref="ISource.Counters"/> as you go (the tile shows the running total); the host sets
/// the state to <see cref="EnumerationState.Complete"/> when the task finishes.
/// </summary>
public interface ICountable
{
    Task CountFilesAsync(CancellationToken ct);
}

/// <summary>What a source can do. Whether it is <em>allowed</em> to is decided per collection and per mount.</summary>
public sealed record SourceCapabilities(bool CanWrite = false, bool CaseInsensitive = false)
{
    public static readonly SourceCapabilities ReadOnly = new();

    public static readonly SourceCapabilities Writable = new(CanWrite: true);
}

public enum EnumerationState
{
    Unknown,
    InProgress,
    Complete,
}

/// <summary>How big the tree is, as far as the source knows ("files it's serving", not "files served").</summary>
public sealed class SourceCounters
{
    private long _files;
    private long _directories;
    private int _state;

    public long FilesKnown => Interlocked.Read(ref _files);

    public long DirectoriesKnown => Interlocked.Read(ref _directories);

    public EnumerationState State => (EnumerationState)Volatile.Read(ref _state);

    public void Set(long files, long directories, EnumerationState state)
    {
        Interlocked.Exchange(ref _files, files);
        Interlocked.Exchange(ref _directories, directories);
        Volatile.Write(ref _state, (int)state);
    }

    public void Add(long files, long directories)
    {
        Interlocked.Add(ref _files, files);
        Interlocked.Add(ref _directories, directories);
    }

    public void SetState(EnumerationState state) => Volatile.Write(ref _state, (int)state);
}

public enum ComponentHealth
{
    Ready,
    /// <summary>Working, but something's wrong: a root unreachable, a refresh failed.</summary>
    Degraded,
    Failed,
}

/// <summary>What the host gives a source for its whole life, starting with <see cref="ISource.InitialiseAsync"/>.</summary>
public interface ISourceContext
{
    ILogger Logger { get; }

    Guid SourceId { get; }

    string SourceName { get; }

    /// <summary>Per-source scratch space that survives restarts (spool files). Created on first use.</summary>
    string CacheDirectory { get; }

    /// <summary>Per-source state (host keys, indexes). Created on first use.</summary>
    string StateDirectory { get; }

    ISecretStore Secrets { get; }

    /// <summary>Shown while the source is Starting: "Logging in...", with an optional 0..1 fraction.</summary>
    void ReportProgress(string message, double? fraction = null);

    /// <summary>Changes the source's health after it has started (a root went away: Degraded; came back: Ready).</summary>
    void ReportHealth(ComponentHealth health, string? detail = null);

    /// <summary>Asks the user something (PIN, 2FA code). Headless without a console: fails with "Needs input".</summary>
    Task<PromptResult> PromptAsync(PromptRequest request, CancellationToken ct);
}

/// <summary>A configured source instance. Created from settings, initialised once, then called concurrently.</summary>
public interface ISource : IVfsOperations, IAsyncDisposable
{
    SourceCapabilities Capabilities { get; }

    SourceCounters Counters { get; }

    /// <summary>
    /// Called once per start. Return quickly or report progress; throw <see cref="SourceFailedException"/>
    /// (permanent when settings are wrong) or any exception to fail, which the host retries with backoff.
    /// </summary>
    Task InitialiseAsync(ISourceContext context, CancellationToken ct);
}
