namespace AnyFS.Core.Status;

/// <summary>Spec 6.5. Per source and per mount.</summary>
public enum ComponentState
{
    Disabled,
    Starting,
    Enumerating,
    Ready,
    Degraded,
    Failed,
    Stopping,
}

public enum StatusColour
{
    Grey,
    Green,
    Yellow,
    Red,
}

public sealed record ComponentStatus(ComponentState State, string Text = "", double? Progress = null)
{
    public static readonly ComponentStatus DisabledStatus = new(ComponentState.Disabled, "Disabled");

    public static readonly ComponentStatus ReadyStatus = new(ComponentState.Ready, "Ready");

    public StatusColour Colour => ColourOf(State);

    public bool IsUsable => State is ComponentState.Ready or ComponentState.Degraded or ComponentState.Enumerating;

    public static StatusColour ColourOf(ComponentState state) => state switch
    {
        ComponentState.Disabled => StatusColour.Grey,
        ComponentState.Ready => StatusColour.Green,
        ComponentState.Failed => StatusColour.Red,
        _ => StatusColour.Yellow,
    };

    public override string ToString() => Text.Length == 0 ? State.ToString() : $"{State}: {Text}";
}

/// <summary>A collection's circle is the worst of its sources and mounts; its text summarises.</summary>
public static class StatusRollup
{
    public static (StatusColour Colour, string Text) ForCollection(bool enabled, IReadOnlyList<ComponentStatus> sources, IReadOnlyList<ComponentStatus> mounts)
    {
        if (!enabled)
        {
            return (StatusColour.Grey, "Disabled");
        }

        var all = sources.Concat(mounts).Where(s => s.State != ComponentState.Disabled).ToList();
        if (all.Count == 0)
        {
            return (StatusColour.Grey, sources.Count == 0 ? "No sources" : "Nothing enabled");
        }

        var colour = all.Max(s => s.Colour);
        var parts = new List<string>();
        Summarise(parts, "source", sources);
        Summarise(parts, "mount", mounts);
        return (colour, string.Join(", ", parts));
    }

    private static void Summarise(List<string> parts, string noun, IReadOnlyList<ComponentStatus> statuses)
    {
        foreach (var group in statuses.Where(s => s.State != ComponentState.Disabled).GroupBy(s => s.State).OrderBy(g => g.Key))
        {
            var n = group.Count();
            var word = group.Key switch
            {
                ComponentState.Starting => "starting",
                ComponentState.Enumerating => "enumerating",
                ComponentState.Ready => "ready",
                ComponentState.Degraded => "degraded",
                ComponentState.Failed => "failed",
                ComponentState.Stopping => "stopping",
                _ => group.Key.ToString().ToLowerInvariant(),
            };
            parts.Add($"{n} {noun}{(n == 1 ? "" : "s")} {word}");
        }
    }
}
