using AnyFS.Sdk;

namespace AnyFS.Sources.Tests;

/// <summary>
/// A client's pass over a source's whole tree, as a search over every file makes one (FileLocator Pro's): every folder listed,
/// every entry stat'd. For the tests that say a source answers such a pass from what it keeps, once its first walk is done,
/// and asks its server nothing (the user, 6-Nov-2026: "Always answer from cache except for the initial walk").
/// </summary>
internal static class ListEverything
{
    /// <summary>Lists every folder below <paramref name="path"/> and stats each entry: how many folders it listed.</summary>
    public static async Task<int> OfAsync(ISource source, string path = "", Func<string, bool>? leaveOut = null)
    {
        var folders = 0;
        var pending = new Stack<string>();
        pending.Push(path);
        while (pending.TryPop(out var folder))
        {
            if (leaveOut?.Invoke(folder) == true)
            {
                continue;
            }

            folders++;
            await foreach (var entry in source.ListAsync(folder, CancellationToken.None))
            {
                var child = VfsPath.Join(folder, entry.Name);
                await source.StatAsync(child, CancellationToken.None);
                if (entry.IsDirectory)
                {
                    pending.Push(child);
                }
            }
        }

        return folders;
    }

    /// <summary>The requests made since <paramref name="before"/> of them, as one line, for an assertion's message.</summary>
    public static string Since(IEnumerable<string> requests, int before) => string.Join("; ", requests.Skip(before).Take(20));
}
