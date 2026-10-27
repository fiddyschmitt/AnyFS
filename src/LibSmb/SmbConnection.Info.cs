using LibSmb.Protocol;
using Microsoft.Extensions.Logging;

namespace LibSmb;

internal sealed partial class SmbConnection
{
    /// <summary>The most bytes of directory entries one QUERY_DIRECTORY reply carries.</summary>
    private const int MaxDirectoryOutput = 1 << 20;

    // ----- QUERY_DIRECTORY -----

    /// <summary>
    /// QUERY_DIRECTORY ([MS-SMB2] 2.2.33): as many entries as fit in the client's buffer, then the rest on the calls after,
    /// then STATUS_NO_MORE_FILES. The search pattern is honoured: a client looks a child up by name with a pattern of that
    /// name (macOS does it for every entry, Windows for path resolution), which one stat answers, not a listing filtered.
    /// </summary>
    internal async ValueTask<(uint Status, Reply Reply)> QueryDirectoryAsync(Memory<byte> request, CancellationToken ct)
    {
        if (request.Length < 32)
        {
            return (Status.InvalidParameter, Reply.Error());
        }

        var infoClass = request.Span[2];
        var flags = request.Span[3];
        var outputLength = (int)Math.Min(Wire.U32(request.Span, 28), MaxDirectoryOutput);
        var pattern = Wire.Utf16(Wire.At(request.Span, Wire.U16(request.Span, 24), Wire.U16(request.Span, 26)));
        var open = FindOpen(FileId.Read(request.Span[8..24]));
        if (open is not { IsDirectory: true })
        {
            return (Status.InvalidParameter, Reply.Error());
        }

        await open.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if ((flags & (QueryDirectoryFlags.RestartScans | QueryDirectoryFlags.Reopen)) != 0)
            {
                open.DirectoryEntries = null;
                open.DirectoryPosition = 0;
            }

            var fs = open.Share.FileSystem;
            if (open.DirectoryEntries is null && SmbPattern.IsSingleName(pattern))
            {
                SmbEntry? entry = null;
                try
                {
                    entry = await fs.StatAsync(SmbPath.Join(open.Path, pattern), ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (IsFileSystemFailure(ex))
                {
                }

                open.DirectoryEntries = entry is null ? [] : [entry];
                open.DirectoryPosition = 0;
                Logger.LogTrace("smb: QUERY_DIRECTORY {Path} by name {Name}: {Found}", open.Path, pattern, entry is not null);
            }

            if (open.DirectoryEntries is null)
            {
                var entries = new List<SmbEntry>();
                try
                {
                    await foreach (var entry in fs.ListAsync(open.Path, ct).ConfigureAwait(false))
                    {
                        if (SmbPattern.Matches(pattern, entry.Name, fs.CaseInsensitive))
                        {
                            entries.Add(entry);
                        }
                    }
                }
                catch (Exception ex) when (IsFileSystemFailure(ex))
                {
                    // A directory that cannot be listed (held by another process, denied) must not fail the request
                    // with a generic error: the Windows shell's copy gives up on a whole recursive copy when its scan
                    // meets STATUS_UNSUCCESSFUL. Shown empty, it is skipped and the rest goes on.
                    Logger.LogInformation("smb: {Path} of {Share} cannot be listed, so it is shown empty: {Error}", open.Path, open.Share.Name, ex.Message);
                    entries.Clear();
                }

                open.DirectoryEntries = entries;
                open.DirectoryPosition = 0;
                Logger.LogTrace("smb: QUERY_DIRECTORY {Path} class {Class:X2} pattern '{Pattern}': {Count} entries", open.Path, infoClass, pattern, entries.Count);
            }

            if (open.DirectoryPosition >= open.DirectoryEntries.Count)
            {
                return (Status.NoMoreFiles, Reply.Error());
            }

            // SMB2_RETURN_SINGLE_ENTRY (Windows' FindFirstFile) asks for exactly one: packing more would have the client
            // keep the first and resume after it while the position here moved past them all, losing the ones between
            var remaining = open.DirectoryEntries.Count - open.DirectoryPosition;
            var take = (flags & QueryDirectoryFlags.ReturnSingleEntry) != 0 ? 1 : remaining;
            var (buffer, count) = Info.DirectoryEntries(open.DirectoryEntries.GetRange(open.DirectoryPosition, take), outputLength, infoClass, open.Share, open.Path, AppleReadDirAttr);
            if (count == 0)
            {
                return (Status.NoMoreFiles, Reply.Error());
            }

            open.DirectoryPosition += count;
            return (Status.Success, InfoResponse(buffer));
        }
        finally
        {
            open.Gate.Release();
        }
    }

    // ----- QUERY_INFO -----

    /// <summary>
    /// QUERY_INFO ([MS-SMB2] 2.2.37) of the file and file-system classes served. A result longer than the client's
    /// buffer is cut to it and answered STATUS_BUFFER_OVERFLOW: more bytes than were asked for make the Windows
    /// redirector reject the whole response (ERROR_BAD_NET_RESP).
    /// </summary>
    internal async ValueTask<(uint Status, Reply Reply)> QueryInfoAsync(Memory<byte> request, CancellationToken ct)
    {
        if (request.Length < 40)
        {
            return (Status.InvalidParameter, Reply.Error());
        }

        var infoType = request.Span[2];
        var infoClass = request.Span[3];
        var outputLength = (int)Math.Min(Wire.U32(request.Span, 4), Smb2.MaxIoSize);
        var open = FindOpen(FileId.Read(request.Span[24..40]));
        if (open is null)
        {
            return (Status.InvalidParameter, Reply.Error());
        }

        byte[] info;
        switch (infoType)
        {
            case InfoType.File:
            {
                var (attributes, size, modified, created, _) = await open.StatAsync(ct).ConfigureAwait(false);
                var isDirectory = (attributes & FileAttribute.Directory) != 0;
                switch (infoClass)
                {
                    case FileInfoClass.All:
                        info = Info.All(attributes, size, modified, created, open.Name, isDirectory, OpenFile.PathFileId(open.Share.Name, open.Path));
                        break;
                    case FileInfoClass.Standard:
                        info = Info.Standard(size, isDirectory);
                        break;
                    case FileInfoClass.Basic:
                        info = Info.Basic(attributes, modified, created);
                        break;
                    case FileInfoClass.Internal:
                        info = Info.Internal(OpenFile.PathFileId(open.Share.Name, open.Path));
                        break;
                    case FileInfoClass.Ea:
                        info = new byte[4]; // EaSize 0
                        break;
                    case FileInfoClass.NetworkOpen:
                        info = Info.NetworkOpen(attributes, size, modified, created);
                        break;
                    case FileInfoClass.AttributeTag:
                        info = Info.AttributeTag(attributes);
                        break;
                    default:
                        return (Status.NotSupported, Reply.Error());
                }

                break;
            }

            case InfoType.FileSystem:
                switch (infoClass)
                {
                    case FsInfoClass.FullSize:
                        info = Info.FsFullSize(await SpaceAsync(open.Share, ct).ConfigureAwait(false));
                        break;
                    case FsInfoClass.Size:
                        info = Info.FsSize(await SpaceAsync(open.Share, ct).ConfigureAwait(false));
                        break;
                    case FsInfoClass.Attribute:
                        info = Info.FsAttribute(open.Share.ReadOnly);
                        break;
                    case FsInfoClass.Volume:
                        info = Info.FsVolume(_server.ServerGuid, open.Share.Name);
                        break;
                    case FsInfoClass.Device:
                        info = Info.FsDevice();
                        break;
                    default:
                        return (Status.NotSupported, Reply.Error());
                }

                break;
            default:
                return (Status.NotSupported, Reply.Error());
        }

        return info.Length > outputLength
            ? (Status.BufferOverflow, InfoResponse(info.AsSpan(0, outputLength)))
            : (Status.Success, InfoResponse(info));
    }

    private async ValueTask<SmbSpace?> SpaceAsync(SmbShare share, CancellationToken ct)
    {
        try
        {
            return await share.FileSystem.GetSpaceAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsFileSystemFailure(ex))
        {
            return null;
        }
    }

    /// <summary>The QUERY_INFO or QUERY_DIRECTORY response ([MS-SMB2] 2.2.38, 2.2.34), which share a layout.</summary>
    private static Reply InfoResponse(ReadOnlySpan<byte> info)
    {
        var reply = Reply.Rent(8 + info.Length);
        Wire.Put16(reply.Body, 0, 9);
        Wire.Put16(reply.Body, 2, Smb2.HeaderSize + 8);
        Wire.Put32(reply.Body, 4, (uint)info.Length);
        info.CopyTo(reply.Body[8..]);
        return reply;
    }

    // ----- SET_INFO -----

    /// <summary>SET_INFO ([MS-SMB2] 2.2.39): delete on close, end of file, allocation, rename and times.</summary>
    internal async ValueTask<(uint Status, Reply Reply)> SetInfoAsync(Memory<byte> request, CancellationToken ct)
    {
        if (request.Length < 32)
        {
            return (Status.InvalidParameter, Reply.Error());
        }

        var infoType = request.Span[2];
        var infoClass = request.Span[3];
        var bufferLength = Wire.U32(request.Span, 4);
        var bufferOffset = Wire.U16(request.Span, 8) - Smb2.HeaderSize;
        var open = FindOpen(FileId.Read(request.Span[16..32]));
        if (open is null)
        {
            return (Status.InvalidParameter, Reply.Error());
        }

        if (bufferOffset < 0 || (long)bufferOffset + bufferLength > request.Length)
        {
            return (Status.InvalidParameter, Reply.Error());
        }

        if (infoType != InfoType.File)
        {
            return (Status.NotSupported, Reply.Error());
        }

        var buffer = request.Slice(bufferOffset, (int)bufferLength);
        await open.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var status = infoClass switch
            {
                FileInfoClass.Disposition => await SetDispositionAsync(open, buffer, ct).ConfigureAwait(false),
                FileInfoClass.EndOfFile => await SetEndOfFileAsync(open, buffer, ct).ConfigureAwait(false),
                FileInfoClass.Allocation => await SetAllocationAsync(open, ct).ConfigureAwait(false),
                FileInfoClass.Rename => await RenameAsync(open, buffer, ct).ConfigureAwait(false),
                FileInfoClass.Basic => await SetBasicAsync(open, buffer, ct).ConfigureAwait(false),

                // a class not handled must not pass for done
                _ => Status.InvalidInfoClass,
            };
            return (status, Status.IsError(status) ? Reply.Error() : Reply.Fixed(2));
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

    /// <summary>
    /// FileDispositionInformation: delete on close. A directory with anything in it cannot be marked: Windows answers
    /// STATUS_DIRECTORY_NOT_EMPTY here and its client relies on it; left to CLOSE, whose status Windows ignores, it
    /// told the client the directory was gone when it was not.
    /// </summary>
    private async ValueTask<uint> SetDispositionAsync(OpenFile open, Memory<byte> buffer, CancellationToken ct)
    {
        if (buffer.Length < 1)
        {
            return Status.Success;
        }

        var delete = buffer.Span[0] != 0;
        if (delete)
        {
            if (open.Share.ReadOnly)
            {
                return Status.MediaWriteProtected;
            }

            if (await ReadOnlyFileAsync(open, ct).ConfigureAwait(false))
            {
                return Status.AccessDenied;
            }

            if (open.IsDirectory && await HasEntriesAsync(open, ct).ConfigureAwait(false))
            {
                return Status.DirectoryNotEmpty;
            }
        }

        open.DeleteOnClose = delete;
        return Status.Success;
    }

    private static async ValueTask<bool> HasEntriesAsync(OpenFile open, CancellationToken ct)
    {
        try
        {
            await foreach (var _ in open.Share.FileSystem.ListAsync(open.Path, ct).ConfigureAwait(false))
            {
                return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return false;
    }

    /// <summary>FileEndOfFileInformation: truncate or extend.</summary>
    private async ValueTask<uint> SetEndOfFileAsync(OpenFile open, Memory<byte> buffer, CancellationToken ct)
    {
        if (buffer.Length < 8)
        {
            return Status.Success;
        }

        if (open.Share.ReadOnly)
        {
            return Status.MediaWriteProtected;
        }

        if (await ReadOnlyFileAsync(open, ct).ConfigureAwait(false))
        {
            return Status.AccessDenied;
        }

        if (open.File is not { } file)
        {
            return open.IsDirectory ? Status.InvalidParameter : Status.AccessDenied;
        }

        var length = Wire.U64(buffer.Span, 0);
        if (length > long.MaxValue)
        {
            return Status.InvalidParameter;
        }

        await file.SetLengthAsync((long)length, ct).ConfigureAwait(false);
        return Status.Success;
    }

    /// <summary>
    /// FileAllocationInformation reserves space and never changes the end of file; a plain file system has no allocation
    /// of its own to set, so it does nothing. Windows sends it before FileEndOfFileInformation when it sets a length, and
    /// refusing it surfaced as "the specified file length was too large for the file system" on every truncate.
    /// </summary>
    private async ValueTask<uint> SetAllocationAsync(OpenFile open, CancellationToken ct)
    {
        if (open.Share.ReadOnly)
        {
            return Status.MediaWriteProtected;
        }

        return await ReadOnlyFileAsync(open, ct).ConfigureAwait(false) ? Status.AccessDenied : Status.Success;
    }

    /// <summary>
    /// FileRenameInformation: a move to a path from the share's root. Without ReplaceIfExists an existing target is a
    /// collision (Windows relies on it to ask "replace or skip"); a name that differs from the open's own only in case is
    /// not one, since on a case-insensitive store the lookup finds the file itself and a case-only rename is what the
    /// client wants.
    /// </summary>
    private async ValueTask<uint> RenameAsync(OpenFile open, Memory<byte> buffer, CancellationToken ct)
    {
        if (open.Share.ReadOnly)
        {
            return Status.MediaWriteProtected;
        }

        if (await ReadOnlyFileAsync(open, ct).ConfigureAwait(false))
        {
            return Status.AccessDenied;
        }

        if (buffer.Length < 20)
        {
            return Status.Success;
        }

        var replace = buffer.Span[0] != 0;
        var nameLength = Wire.U32(buffer.Span, 16);
        if (20 + (long)nameLength > buffer.Length)
        {
            return Status.Success;
        }

        var target = SmbPath.FromSmbName(Wire.Utf16(buffer.Span.Slice(20, (int)nameLength)));
        var fs = open.Share.FileSystem;
        if (!replace && !string.Equals(target, open.Path, StringComparison.OrdinalIgnoreCase)
            && await fs.StatAsync(target, ct).ConfigureAwait(false) is not null)
        {
            return Status.ObjectNameCollision;
        }

        await fs.RenameAsync(open.Path, target, replace, ct).ConfigureAwait(false);
        open.Path = target;
        return Status.Success;
    }

    /// <summary>
    /// FileBasicInformation: the times set (a zero or -1 one left as it is). The attributes are not kept. A read-only share
    /// or file ignores the times rather than fail: clients set them as housekeeping after a write.
    /// </summary>
    private async ValueTask<uint> SetBasicAsync(OpenFile open, Memory<byte> buffer, CancellationToken ct)
    {
        if (buffer.Length < 32)
        {
            return Status.Success;
        }

        var created = Wire.FromFileTime(Wire.U64(buffer.Span, 0));
        var modified = Wire.FromFileTime(Wire.U64(buffer.Span, 16));
        if ((created is null && modified is null) || open.Share.ReadOnly || await ReadOnlyFileAsync(open, ct).ConfigureAwait(false))
        {
            return Status.Success;
        }

        try
        {
            await open.Share.FileSystem.SetTimesAsync(open.Path, created, modified, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsFileSystemFailure(ex))
        {
            Logger.LogDebug("smb: setting the times of {Path} of {Share}: {Error}", open.Path, open.Share.Name, ex.Message);
        }

        return Status.Success;
    }

    private static async ValueTask<bool> ReadOnlyFileAsync(OpenFile open, CancellationToken ct)
        => !open.IsDirectory && (await open.StatAsync(ct).ConfigureAwait(false)).ReadOnly;

    // ----- IOCTL -----

    /// <summary>
    /// IOCTL ([MS-SMB2] 2.2.31): FSCTL_VALIDATE_NEGOTIATE_INFO answered with what NEGOTIATE said, "not a DFS path" for DFS
    /// referrals; every other control code is not supported.
    /// </summary>
    internal (uint Status, Reply Reply) Ioctl(ReadOnlySpan<byte> body)
    {
        if (body.Length < 56)
        {
            return (Status.InvalidParameter, Reply.Error());
        }

        var code = Wire.U32(body, 4);
        if (code is Fsctl.DfsGetReferrals or Fsctl.DfsGetReferralsEx)
        {
            return (Status.NotFound, Reply.Error());
        }

        if (code != Fsctl.ValidateNegotiateInfo)
        {
            return (Status.NotSupported, Reply.Error());
        }

        var reply = Reply.Rent(48 + 24);
        var b = reply.Body;
        Wire.Put16(b, 0, 49);                         // StructureSize
        Wire.Put32(b, 4, code);
        body.Slice(8, 16).CopyTo(b[8..]);             // FileId, echoed
        Wire.Put32(b, 32, Smb2.HeaderSize + 48);      // OutputOffset
        Wire.Put32(b, 36, 24);                        // OutputCount
        var output = b[48..];
        Wire.Put32(output, 0, 0);                     // Capabilities
        _server.ServerGuid.CopyTo(output[4..]);
        Wire.Put16(output, 20, SecurityMode.SigningEnabled);
        Wire.Put16(output, 22, Dialect);
        return (Status.Success, reply);
    }
}
