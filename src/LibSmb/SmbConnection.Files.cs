using LibSmb.Protocol;
using Microsoft.Extensions.Logging;

namespace LibSmb;

internal sealed partial class SmbConnection
{
    /// <summary>The fixed part of a READ response, which the data follows.</summary>
    private const int ReadResponseFixed = 16;

    // ----- CREATE -----

    /// <summary>
    /// CREATE ([MS-SMB2] 2.2.13): opens or creates a file or directory on the tree's share as the disposition and options
    /// say, refusing what a read-only share or a read-only file must not do, and answers an Apple create context.
    /// </summary>
    internal async ValueTask<(uint Status, Reply Reply)> CreateAsync(Memory<byte> request, uint treeId, CancellationToken ct)
    {
        if (request.Length < 56)
        {
            return (Status.InvalidParameter, Reply.Error());
        }

        var (shareStatus, share) = ShareOf(treeId);
        if (share is null)
        {
            return (shareStatus, Reply.Error());
        }

        if (OpenCount >= MaxOpenFiles)
        {
            return (Status.InsufficientResources, Reply.Error());
        }

        var (desiredAccess, disposition, options, path, appleQuery) = ParseCreate(request.Span);
        var deleteOnClose = (options & CreateOptions.DeleteOnClose) != 0;
        var fs = share.FileSystem;

        SmbEntry? entry;
        try
        {
            entry = await fs.StatAsync(path, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsFileSystemFailure(ex))
        {
            return (Errors.StatusOf(ex), Reply.Error());
        }

        var exists = entry is not null;
        switch (disposition)
        {
            case Disposition.Open or Disposition.Overwrite when !exists:
                return (Status.ObjectNameNotFound, Reply.Error());
            case Disposition.Create when exists:
                return (Status.ObjectNameCollision, Reply.Error());
        }

        var mode = OpenMode(desiredAccess, disposition);

        // a file that exists is opened, not created: an OPEN_IF must not reach the file system as a create (which one may
        // take for "create or truncate", and a read-only path refuses)
        if (exists)
        {
            mode &= ~(SmbOpenMode.Create | SmbOpenMode.Exclusive);
        }

        var writes = (mode & (SmbOpenMode.Write | SmbOpenMode.Truncate)) != 0;

        // a read-only share refuses every open that would change something: write access, truncation, creating what is
        // missing (a file or a directory), deleting on close
        if (share.ReadOnly)
        {
            var creates = !exists && ((mode & SmbOpenMode.Create) != 0 || (options & CreateOptions.DirectoryFile) != 0);
            if (writes || creates || deleteOnClose)
            {
                return (Status.MediaWriteProtected, Reply.Error());
            }

            // what passed changes nothing; an OPEN_IF of an existing file must not reach the file system as a create
            mode = SmbOpenMode.Read;
        }

        // a file that is read-only itself refuses opens that would write to it or delete it, as Windows does with its own
        // read-only files; the share stays writable
        if (entry is not null && Info.IsReadOnlyFile(entry) && (writes || deleteOnClose))
        {
            return (Status.AccessDenied, Reply.Error());
        }

        uint action;
        Func<FileId, OpenFile> open;
        try
        {
            if (entry is { IsDirectory: true })
            {
                if ((options & CreateOptions.NonDirectoryFile) != 0)
                {
                    return (Status.FileIsADirectory, Reply.Error());
                }

                action = CreateAction.Opened;
                open = id => new OpenFile(id, share, path, null, isDirectory: true, canWrite: !share.ReadOnly);
            }
            else if (entry is null && (options & CreateOptions.DirectoryFile) != 0)
            {
                await fs.CreateDirectoryAsync(path, ct).ConfigureAwait(false);
                action = CreateAction.Created;
                open = id => new OpenFile(id, share, path, null, isDirectory: true, canWrite: true);
            }
            else if (entry is not null && (options & CreateOptions.DirectoryFile) != 0)
            {
                // a directory was asked for (FILE_DIRECTORY_FILE) and the name is a file
                return (Status.NotADirectory, Reply.Error());
            }
            else if (exists && !Access.TouchesData(desiredAccess) && (mode & (SmbOpenMode.Write | SmbOpenMode.Truncate)) == 0)
            {
                // an open to look at the file, rename or delete it: no data to reach, so nothing opened in the file system,
                // whose handle would stand in the way (a Windows volume refuses to rename a folder while a file in it is open,
                // and Windows' client may still have a file in a folder open for its attributes as it renames the folder)
                action = CreateAction.Opened;
                open = id => new OpenFile(id, share, path, null, isDirectory: false, canWrite: false);
            }
            else
            {
                var file = await fs.OpenAsync(path, mode, ct).ConfigureAwait(false);
                action = !exists ? CreateAction.Created : (mode & SmbOpenMode.Truncate) != 0 ? CreateAction.Overwritten : CreateAction.Opened;
                open = id => new OpenFile(id, share, path, file, isDirectory: false, canWrite: (mode & SmbOpenMode.Write) != 0);
            }
        }
        catch (Exception ex) when (IsFileSystemFailure(ex))
        {
            return (Errors.StatusOf(ex), Reply.Error());
        }

        var fileId = AddOpen(open);
        Logger.LogTrace("smb: {Remote}: {Path} of {Share} opened as {FileId} (access {Access:X8}, disposition {Disposition}, options {Options:X8})", Remote, path, share.Name, fileId.Volatile, desiredAccess, disposition, options);
        var opened = FindOpen(fileId)!;
        opened.DeleteOnClose = deleteOnClose;
        return (Status.Success, await CreateResponseAsync(action, opened, AppleContext(appleQuery, share), ct).ConfigureAwait(false));
    }

    /// <summary>A CREATE request's access, disposition, options and path, and its Apple create context's data when it has one.</summary>
    private static (uint DesiredAccess, uint Disposition, uint Options, string Path, byte[]? AppleQuery) ParseCreate(ReadOnlySpan<byte> body)
    {
        var path = SmbPath.FromSmbName(Wire.Utf16(Wire.At(body, Wire.U16(body, 44), Wire.U16(body, 46))));
        var appleQuery = Apple.CreateContexts(body) is { } contexts && contexts.TryGetValue(Apple.ContextName, out var query) ? query : null;
        return (Wire.U32(body, 24), Wire.U32(body, 36), Wire.U32(body, 40), path, appleQuery);
    }

    /// <summary>The create context answering an Apple server query, and ReadDirAttr on for the connection when the client asked for it.</summary>
    private byte[]? AppleContext(byte[]? query, SmbShare share)
    {
        if (query is null || Apple.ServerQueryResponse(query, share.FileSystem.CaseInsensitive, out var readDirAttr) is not { } answer)
        {
            return null;
        }

        if (readDirAttr)
        {
            AppleReadDirAttr = true;
        }

        return Apple.ContextBlob(Apple.ContextName, answer);
    }

    /// <summary>
    /// How a CREATE's access and disposition open a file: for writing when the access includes it, creating when the
    /// disposition does (exclusively for FILE_CREATE), truncating for the overwriting ones.
    /// </summary>
    internal static SmbOpenMode OpenMode(uint desiredAccess, uint disposition)
    {
        var mode = Access.Writes(desiredAccess) ? SmbOpenMode.Write : SmbOpenMode.Read;
        return mode | disposition switch
        {
            Disposition.Create => SmbOpenMode.Create | SmbOpenMode.Exclusive,
            Disposition.OpenIf => SmbOpenMode.Create,
            Disposition.Overwrite => SmbOpenMode.Truncate,
            Disposition.OverwriteIf or Disposition.Supersede => SmbOpenMode.Create | SmbOpenMode.Truncate,
            _ => SmbOpenMode.Read,
        };
    }

    /// <summary>The CREATE response ([MS-SMB2] 2.2.14), with the create contexts that answer the request's (Apple's) when there are any.</summary>
    private static async ValueTask<Reply> CreateResponseAsync(uint action, OpenFile open, byte[]? contexts, CancellationToken ct)
    {
        var (attributes, size, modified, created, _) = await open.StatAsync(ct).ConfigureAwait(false);

        // the create contexts go after the 88 fixed bytes (offset 152 from the header, 8-aligned), the padding byte gone
        var reply = Reply.Rent(contexts is null ? 89 : 88 + contexts.Length);
        var b = reply.Body;
        Wire.Put16(b, 0, 89);
        Wire.Put32(b, 4, action);
        Info.WriteTimes(b[8..], modified, created);
        Wire.Put64(b, 40, (ulong)size);   // AllocationSize
        Wire.Put64(b, 48, (ulong)size);   // EndOfFile
        Wire.Put32(b, 56, attributes);
        open.Id.Write(b[64..]);
        if (contexts is not null)
        {
            Wire.Put32(b, 80, Smb2.HeaderSize + 88);
            Wire.Put32(b, 84, (uint)contexts.Length);
            contexts.CopyTo(b[88..]);
        }

        return reply;
    }

    // ----- CLOSE -----

    /// <summary>
    /// CLOSE ([MS-SMB2] 2.2.15): the file closed, or a delete-on-close carried out. With POSTQUERY_ATTRIB the reply has
    /// the file's final attributes (macOS always asks, and logs "did not return close attributes" otherwise), except for
    /// a delete-on-close: that file ceases to exist, and attributes for it make a caching client (macOS smbfs) believe
    /// the file it just deleted is still there.
    /// </summary>
    internal async ValueTask<(uint Status, Reply Reply)> CloseAsync(Memory<byte> request, CancellationToken ct)
    {
        if (request.Length < 24)
        {
            return (Status.InvalidParameter, Reply.Error());
        }

        var flags = Wire.U16(request.Span, 2);
        var open = RemoveOpen(FileId.Read(request.Span[8..24]));
        var reply = Reply.Fixed(60);
        if (open is null)
        {
            return (Status.Success, reply);
        }

        await open.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if ((flags & CloseFlags.PostQueryAttributes) != 0 && !open.DeleteOnClose)
            {
                var (attributes, size, modified, created, _) = await open.StatAsync(ct).ConfigureAwait(false);
                Info.WriteCloseAttributes(reply.Body, attributes, size, modified, created);
            }

            if (open.File is { } file)
            {
                // a failed close means a buffered write did not land: said, not passed off as success
                await file.DisposeAsync().ConfigureAwait(false);
            }

            if (open.DeleteOnClose)
            {
                await open.Share.FileSystem.DeleteAsync(open.Path, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (IsFileSystemFailure(ex))
        {
            reply.Dispose();
            return (Errors.StatusOf(ex), Reply.Error());
        }
        finally
        {
            open.Gate.Release();
        }

        return (Status.Success, reply);
    }

    // ----- READ, WRITE, FLUSH -----

    /// <summary>
    /// READ ([MS-SMB2] 2.2.19): the data read straight into the reply, behind its fixed part. A length over MaxReadSize is
    /// refused rather than allocated; a read wholly at or past the end is STATUS_END_OF_FILE.
    /// </summary>
    internal async ValueTask<(uint Status, Reply Reply)> ReadAsync(Memory<byte> request, CancellationToken ct)
    {
        if (request.Length < 48)
        {
            return (Status.InvalidParameter, Reply.Error());
        }

        var length = Wire.U32(request.Span, 4);
        var offset = Wire.U64(request.Span, 8);
        var open = FindOpen(FileId.Read(request.Span[16..32]));
        if (open is null || open.IsDirectory || length > Smb2.MaxIoSize || offset > long.MaxValue)
        {
            return (Status.InvalidParameter, Reply.Error());
        }

        var reply = Reply.Rent(ReadResponseFixed + (int)length);
        var read = 0;
        if (length > 0)
        {
            await open.Gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var file = await open.FileToReadAsync(ct).ConfigureAwait(false);
                var target = reply.Buffer.AsMemory(Reply.Headroom + Smb2.HeaderSize + ReadResponseFixed, (int)length);
                while (read < target.Length)
                {
                    var n = await file.ReadAsync((long)offset + read, target[read..], ct).ConfigureAwait(false);
                    if (n <= 0)
                    {
                        break;
                    }

                    read += n;
                }
            }
            catch (Exception ex) when (IsFileSystemFailure(ex))
            {
                reply.Dispose();
                return (Errors.StatusOf(ex), Reply.Error());
            }
            finally
            {
                open.Gate.Release();
            }

            if (read == 0)
            {
                reply.Dispose();
                return (Status.EndOfFile, Reply.Error());
            }
        }

        reply.Shrink(ReadResponseFixed + read);
        var b = reply.Body;
        Wire.Put16(b, 0, 17);
        b[2] = Smb2.HeaderSize + ReadResponseFixed; // DataOffset, from the header
        Wire.Put32(b, 4, (uint)read);               // DataLength
        return (Status.Success, reply);
    }

    /// <summary>WRITE ([MS-SMB2] 2.2.21).</summary>
    internal async ValueTask<(uint Status, Reply Reply)> WriteAsync(Memory<byte> request, CancellationToken ct)
    {
        if (request.Length < 48)
        {
            return (Status.InvalidParameter, Reply.Error());
        }

        var dataOffset = Wire.U16(request.Span, 2) - Smb2.HeaderSize;
        var length = Wire.U32(request.Span, 4);
        var offset = Wire.U64(request.Span, 8);
        var open = FindOpen(FileId.Read(request.Span[16..32]));
        if (open?.File is not { } file)
        {
            return (open is { IsDirectory: false } ? Status.AccessDenied : Status.InvalidParameter, Reply.Error());
        }

        if (open.Share.ReadOnly)
        {
            return (Status.MediaWriteProtected, Reply.Error());
        }

        if (!open.CanWrite)
        {
            return (Status.AccessDenied, Reply.Error());
        }

        if (length > Smb2.MaxIoSize || dataOffset < 0 || (long)dataOffset + length > request.Length || offset > long.MaxValue)
        {
            return (Status.InvalidParameter, Reply.Error());
        }

        await open.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await file.WriteAsync((long)offset, request.Slice(dataOffset, (int)length), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsFileSystemFailure(ex))
        {
            return (Errors.StatusOf(ex), Reply.Error());
        }
        finally
        {
            open.Gate.Release();
        }

        var reply = Reply.Rent(17);
        Wire.Put16(reply.Body, 0, 17);
        Wire.Put32(reply.Body, 4, length); // Count
        return (Status.Success, reply);
    }

    /// <summary>FLUSH ([MS-SMB2] 2.2.17).</summary>
    internal async ValueTask<(uint Status, Reply Reply)> FlushAsync(Memory<byte> request, CancellationToken ct)
    {
        if (request.Length < 24)
        {
            return (Status.InvalidParameter, Reply.Error());
        }

        if (FindOpen(FileId.Read(request.Span[8..24])) is { File: { } file } open)
        {
            await open.Gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await file.FlushAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsFileSystemFailure(ex))
            {
                return (Errors.StatusOf(ex), Reply.Error());
            }
            finally
            {
                open.Gate.Release();
            }
        }

        return (Status.Success, Reply.Fixed(4));
    }

    /// <summary>
    /// A failure of the file system that answers one request with an error status. Cancellation (the connection or the
    /// server going) is not one: it ends the connection.
    /// </summary>
    private bool IsFileSystemFailure(Exception ex)
    {
        if (ex is OperationCanceledException)
        {
            return false;
        }

        if (ex is not (IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException))
        {
            Logger.LogWarning(ex, "smb: {Remote}: a file system call failed unexpectedly", Remote);
        }
        else if (Errors.StatusOf(ex) is not (Status.ObjectNameNotFound or Status.ObjectPathNotFound) and var status)
        {
            // what a lookup of a name that isn't there says is routine; anything else, the log says what it was (a client
            // shows STATUS_UNSUCCESSFUL as no more than "failed")
            Logger.LogDebug(ex, "smb: {Remote}: a file system call failed (status 0x{Status:X8}): {Error}", Remote, status, ex.Message);
        }

        return true;
    }
}
