using AnyFS.Core.Model;
using AnyFS.Core.Status;
using AnyFS.Core.Vfs;
using AnyFS.Sdk;
using Microsoft.Extensions.Logging;

namespace AnyFS.Core.Runtime;

/// <summary>A source's place in a collection at run time.</summary>
public sealed record CollectionSource(SourceHandle Handle, SourceMembership Membership, SourceDefinition Definition);

/// <summary>
/// One collection at run time: its router and its mounts. Mounts start when at least one source is
/// usable or after a grace period, whichever is first, so the tree isn't empty when the mount appears (spec 8.8).
/// Members and mounts can be added and removed while it runs (the window does that); the lists are
/// copy-on-write so readers never see a half-changed one.
/// </summary>
public sealed class CollectionRunner : ICollectionInfo
{
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private CollectionSource[] _sources;
    private MountRunner[] _mounts;

    public CollectionRunner(CollectionDefinition definition, CollectionVfs vfs, IReadOnlyList<CollectionSource> sources, IReadOnlyList<MountRunner> mounts, ILogger logger, TimeProvider? time = null)
    {
        Definition = definition;
        Vfs = vfs;
        _sources = sources.ToArray();
        _mounts = mounts.ToArray();
        _logger = logger;
        _time = time ?? TimeProvider.System;
        RefreshMembers();
    }

    public CollectionDefinition Definition { get; }

    public Guid Id => Definition.Id;

    public string Name => Definition.Name;

    public CollectionVfs Vfs { get; }

    public IReadOnlyList<CollectionSource> Sources => Volatile.Read(ref _sources);

    public IReadOnlyList<MountRunner> Mounts => Volatile.Read(ref _mounts);

    public TimeSpan MountStartGrace { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>True between <see cref="StartAsync"/> and <see cref="StopAsync"/> of an enabled collection: mounts added now start at once.</summary>
    public bool IsRunning { get; private set; }

    public (StatusColour Colour, string Text) Status => StatusRollup.ForCollection(
        Definition.Enabled,
        Sources.Where(s => s.Definition.Enabled).Select(s => s.Handle.EffectiveStatus).ToList(),
        Mounts.Select(m => m.Status).ToList());

    /// <summary>Rebuilds the router's tree from the enabled memberships (disabled sources are absent from the tree).</summary>
    public void RefreshMembers()
        => Vfs.Update(Sources.Where(s => s.Definition.Enabled).Select(s => new CollectionMember(s.Handle, s.Membership.Path, s.Membership.ReadOnly)));

    public void AddSource(CollectionSource source)
    {
        Volatile.Write(ref _sources, [.. Sources, source]);
        RefreshMembers();
    }

    public void RemoveSource(Guid sourceId)
    {
        Volatile.Write(ref _sources, Sources.Where(s => s.Handle.Id != sourceId).ToArray());
        RefreshMembers();
    }

    public void AddMount(MountRunner mount) => Volatile.Write(ref _mounts, [.. Mounts, mount]);

    public void RemoveMount(MountRunner mount) => Volatile.Write(ref _mounts, Mounts.Where(m => !ReferenceEquals(m, mount)).ToArray());

    public void ReplaceMount(MountRunner old, MountRunner replacement)
        => Volatile.Write(ref _mounts, Mounts.Select(m => ReferenceEquals(m, old) ? replacement : m).ToArray());

    /// <summary>The name changed: the router's name follows (mounts are restarted by the engine, spec 8.8).</summary>
    public void Rename(string name) => Vfs.Rename(name);

    public async Task StartAsync(CancellationToken ct)
    {
        if (!Definition.Enabled)
        {
            return;
        }

        IsRunning = true;
        await WaitForFirstUsableSourceAsync(ct).ConfigureAwait(false);
        await Task.WhenAll(Mounts.Select(m => StartMountAsync(m, ct))).ConfigureAwait(false);
    }

    /// <summary>Starts one mount against this collection's tree (a no-op while the collection isn't running).</summary>
    public Task StartMountAsync(MountRunner mount, CancellationToken ct)
        => IsRunning ? mount.StartAsync(Vfs.ForMount(mount.Definition.ReadOnly), ct) : Task.CompletedTask;

    public async Task RestartMountsAsync(CancellationToken ct)
    {
        foreach (var mount in Mounts)
        {
            await mount.StopAsync().ConfigureAwait(false);
        }

        await Task.WhenAll(Mounts.Select(m => StartMountAsync(m, ct))).ConfigureAwait(false);
    }

    public async Task StopAsync()
    {
        IsRunning = false;
        await Task.WhenAll(Mounts.Select(m => m.StopAsync())).ConfigureAwait(false);
    }

    private async Task WaitForFirstUsableSourceAsync(CancellationToken ct)
    {
        var enabled = Sources.Where(s => s.Definition.Enabled).ToList();
        if (enabled.Count == 0)
        {
            return;
        }

        var started = _time.GetTimestamp();
        while (!enabled.Any(s => s.Handle.IsUsable || s.Handle.Status.State == ComponentState.Failed && !s.Handle.Status.Text.Contains("retrying", StringComparison.Ordinal)))
        {
            if (_time.GetElapsedTime(started) >= MountStartGrace)
            {
                _logger.LogInformation("collection {Collection}: no source ready after {Grace}; starting mounts anyway", Name, MountStartGrace);
                break;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), _time, ct).ConfigureAwait(false);
        }
    }
}
