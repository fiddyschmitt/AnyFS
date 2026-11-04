using System.Collections.Concurrent;

namespace AnyFS.Sdk.Testing;

/// <summary>
/// A <see cref="IFolderWatcher"/> that remembers what a count told it, one line a thing in the order they came: "in docs",
/// "listed docs 3", "kept docs", "failed docs: why", "done docs". Set it on a source's counters to see which folders its
/// count goes through and how each went, as the host's Operations tab would show them.
/// </summary>
public sealed class RecordingFolderWatcher : IFolderWatcher
{
    private readonly ConcurrentQueue<string> _lines = new();

    public IReadOnlyList<string> Lines => [.. _lines];

    /// <summary>Stands for the root in the lines, whose path is empty.</summary>
    private static string Shown(string folder) => folder.Length == 0 ? "(root)" : folder;

    public IWatchedFolder Entered(string folder)
    {
        _lines.Enqueue($"in {Shown(folder)}");
        return new Watched(this, Shown(folder));
    }

    private sealed class Watched(RecordingFolderWatcher watcher, string folder) : IWatchedFolder
    {
        private long _entries;
        private bool _kept;
        private bool _failed;

        public void Listed(long entries) => Interlocked.Add(ref _entries, entries);

        public void Kept() => _kept = true;

        public void Failed(Exception error, CancellationToken ct)
        {
            _failed = true;
            watcher._lines.Enqueue($"failed {folder}: {error.Message}");
        }

        public void Done()
        {
            if (_kept)
            {
                watcher._lines.Enqueue($"kept {folder}");
            }
            else if (!_failed)
            {
                watcher._lines.Enqueue($"listed {folder} {Interlocked.Read(ref _entries)}");
            }

            watcher._lines.Enqueue($"done {folder}");
        }
    }
}
