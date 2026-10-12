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
/// </summary>
public sealed class CollectionRunner : ICollectionInfo
{
    private readonly ILogger _logger;
    private readonly TimeProvider _time;

    public CollectionRunner(CollectionDefinition definition, CollectionVfs vfs, IReadOnlyList<CollectionSource> sources, IReadOnlyList<MountRunner> mounts, ILogger logger, TimeProvider? time = null)
    {
        Definition = definition;
        Vfs = vfs;
        Sources = sources;
        Mounts = mounts;
        _logger = logger;
        _time = time ?? TimeProvider.System;
        RefreshMembers();
    }

    public CollectionDefinition Definition { get; }

    public Guid Id => Definition.Id;

    public string Name => Definition.Name;

    public CollectionVfs Vfs { get; }

    public IReadOnlyList<CollectionSource> Sources { get; }

    public IReadOnlyList<MountRunner> Mounts { get; }

    public TimeSpan MountStartGrace { get; set; } = TimeSpan.FromSeconds(5);

    public (StatusColour Colour, string Text) Status => StatusRollup.ForCollection(
        Definition.Enabled,
        Sources.Where(s => s.Definition.Enabled).Select(s => s.Handle.EffectiveStatus).ToList(),
        Mounts.Select(m => m.Status).ToList());

    /// <summary>Rebuilds the router's tree from the enabled memberships (disabled sources are absent from the tree).</summary>
    public void RefreshMembers()
        => Vfs.Update(Sources.Where(s => s.Definition.Enabled).Select(s => new CollectionMember(s.Handle, s.Membership.Path, s.Membership.ReadOnly)));

    public async Task StartAsync(CancellationToken ct)
    {
        if (!Definition.Enabled)
        {
            return;
        }

        await WaitForFirstUsableSourceAsync(ct).ConfigureAwait(false);
        await Task.WhenAll(Mounts.Select(m => m.StartAsync(Vfs.ForMount(m.Definition.ReadOnly), ct))).ConfigureAwait(false);
    }

    public Task StopAsync() => Task.WhenAll(Mounts.Select(m => m.StopAsync()));

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
