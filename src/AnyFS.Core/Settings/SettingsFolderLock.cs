using AnyFS.Core.Runtime;

namespace AnyFS.Core.Settings;

/// <summary>Another AnyFS runs on the same folders; the message is for the person who started this one.</summary>
public sealed class SettingsInUseException(string message) : Exception(message);

/// <summary>
/// One AnyFS at a time on a state folder, and so on the settings beside it. Each copy keeps its sources, collections,
/// secrets and stats in memory and writes each file whole: with two, the one that wrote last won, and a session one copy
/// had just renewed was gone at the other's next save (the review of 21-Nov-2026, R7f). The window and
/// <c>anyfs serve</c> take this before they read anything, and hold it until they have stopped; <c>anyfs status</c>,
/// which only reads, does not. It is a file held open and shared with no one, so it goes with the process however that
/// ends.
/// </summary>
public sealed class SettingsFolderLock : IDisposable
{
    private readonly FileStream _held;

    private SettingsFolderLock(FileStream held) => _held = held;

    public static string FileOf(SettingsPaths paths) => Path.Combine(paths.State, "anyfs.lock");

    /// <summary>Takes the folder, or throws <see cref="SettingsInUseException"/> saying which AnyFS has it.</summary>
    public static SettingsFolderLock Take(SettingsPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        Directory.CreateDirectory(paths.State);
        try
        {
            return new SettingsFolderLock(new FileStream(FileOf(paths), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && File.Exists(FileOf(paths)))
        {
            // held by another: a sharing violation on Windows (access denied once that one is closing), a lock refused elsewhere
            var (snapshot, running) = StatusFileReader.Read(paths);
            var who = running && snapshot is not null ? $" (process {snapshot.Pid}, started {snapshot.StartedAt.ToLocalTime():HH:mm})" : "";
            throw new SettingsInUseException($"AnyFS is already running with these settings{who}: {paths.Settings}. Two at once would each overwrite what the other saves. Use the one that is running, or start this one with another folder (--settings-dir).");
        }
    }

    public void Dispose() => _held.Dispose();
}
