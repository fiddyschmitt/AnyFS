using System.Text;
using AnyFS.Core.Runtime;
using AnyFS.Core.Stats;
using AnyFS.Core.Status;
using AnyFS.Core.Vfs;
using AnyFS.Sdk;
using AnyFS.Sdk.Testing;
using Microsoft.Extensions.Logging.Abstractions;

namespace AnyFS.Core.Tests;

internal static class Handles
{
    public static async Task<SourceHandle> ReadyAsync(string name, MemorySource source)
    {
        var handle = new SourceHandle(Guid.NewGuid(), name, "memory");
        await source.InitialiseAsync(new TestSourceContext(name), CancellationToken.None);
        handle.Current = source;
        handle.Status = ComponentStatus.ReadyStatus;
        return handle;
    }

    public static SourceHandle Starting(string name)
        => new(Guid.NewGuid(), name, "memory") { Status = new ComponentStatus(ComponentState.Starting, "Starting") };

    public static CollectionVfs Vfs(string name = "ben", StatsCollector? stats = null)
        => new(Guid.NewGuid(), name, stats, NullLogger.Instance);

    public static CollectionVfs Vfs(string name, params CollectionMember[] members)
    {
        var vfs = Vfs(name);
        vfs.Update(members);
        return vfs;
    }

    /// <summary>Names in listing order, directories marked with a trailing slash.</summary>
    public static async Task<List<string>> NamesAsync(IVfsOperations ops, string path)
    {
        var names = new List<string>();
        await foreach (var e in ops.ListAsync(path, CancellationToken.None))
        {
            names.Add(e.IsDirectory ? e.Name + "/" : e.Name);
        }

        return names;
    }

    public static async Task<string> ReadTextAsync(IVfsOperations ops, string path)
    {
        await using var file = await ops.OpenAsync(path, VfsOpenMode.Read, CancellationToken.None);
        var buffer = new byte[file.Length];
        var n = await file.ReadFullyAsync(0, buffer, CancellationToken.None);
        return Encoding.UTF8.GetString(buffer, 0, n);
    }

    public static async Task WriteTextAsync(IVfsOperations ops, string path, string text, VfsOpenMode mode = VfsOpenMode.Create)
    {
        await using var file = await ops.OpenAsync(path, mode, CancellationToken.None);
        await file.WriteAsync(0, Encoding.UTF8.GetBytes(text), CancellationToken.None);
        await file.FlushAsync(CancellationToken.None);
    }

    public static async Task<VfsException> ExpectAsync(VfsError error, Func<Task> action)
    {
        var ex = await Assert.ThrowsExactlyAsync<VfsException>(action);
        Assert.AreEqual(error, ex.Error, ex.Message);
        return ex;
    }
}
