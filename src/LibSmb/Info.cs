using LibSmb.Protocol;

namespace LibSmb;

/// <summary>
/// The file and file-system information structures of [MS-FSCC] the server answers with. One timestamp is kept per
/// entry besides its creation time, so last access, last write and change all carry the modification time.
/// </summary>
internal static class Info
{
    private const int BytesPerSector = 512;
    private const int SectorsPerUnit = 8;

    /// <summary>A file that is read-only itself (directories carry no such flag).</summary>
    public static bool IsReadOnlyFile(SmbEntry entry) => !entry.IsDirectory && entry.ReadOnly;

    public static uint Attributes(SmbEntry entry)
        => entry.IsDirectory ? FileAttribute.Directory : entry.ReadOnly ? FileAttribute.ReadOnly : FileAttribute.Normal;

    /// <summary>FileBasicInformation (2.4.7).</summary>
    public static byte[] Basic(uint attributes, DateTimeOffset? modified, DateTimeOffset? created)
    {
        var b = new byte[40];
        WriteBasic(b, attributes, modified, created);
        return b;
    }

    /// <summary>FileStandardInformation (2.4.41).</summary>
    public static byte[] Standard(long size, bool isDirectory)
    {
        var b = new byte[24];
        WriteStandard(b, size, isDirectory);
        return b;
    }

    /// <summary>FileInternalInformation (2.4.22): the file id.</summary>
    public static byte[] Internal(ulong fileId)
    {
        var b = new byte[8];
        Wire.Put64(b, 0, fileId);
        return b;
    }

    /// <summary>FileNetworkOpenInformation (2.4.29).</summary>
    public static byte[] NetworkOpen(uint attributes, long size, DateTimeOffset? modified, DateTimeOffset? created)
    {
        var b = new byte[56];
        WriteTimes(b, modified, created);
        Wire.Put64(b, 32, (ulong)size); // AllocationSize
        Wire.Put64(b, 40, (ulong)size); // EndOfFile
        Wire.Put32(b, 48, attributes);
        return b;
    }

    /// <summary>FileAttributeTagInformation (2.4.6): the attributes, no reparse tag.</summary>
    public static byte[] AttributeTag(uint attributes)
    {
        var b = new byte[8];
        Wire.Put32(b, 0, attributes);
        return b;
    }

    /// <summary>
    /// FileAllInformation (2.4.2): basic, standard, internal, then EA, access, position, mode and alignment left zero, and
    /// the name, which keeps the buffer at or above the 101 bytes clients (cifs) insist on.
    /// </summary>
    public static byte[] All(uint attributes, long size, DateTimeOffset? modified, DateTimeOffset? created, string name, bool isDirectory, ulong fileId)
    {
        var nameBytes = name.Length == 0 ? new byte[2] : Wire.Utf16(name);
        var b = new byte[100 + nameBytes.Length];
        WriteBasic(b, attributes, modified, created);
        WriteStandard(b.AsSpan(40), size, isDirectory);
        Wire.Put64(b, 64, fileId);
        Wire.Put32(b, 96, (uint)nameBytes.Length);
        nameBytes.CopyTo(b, 100);
        return b;
    }

    /// <summary>FileFsFullSizeInformation (2.5.4).</summary>
    public static byte[] FsFullSize(SmbSpace? space)
    {
        var b = new byte[32];
        var (total, free) = Units(space);
        Wire.Put64(b, 0, total);   // TotalAllocationUnits
        Wire.Put64(b, 8, free);    // CallerAvailableAllocationUnits
        Wire.Put64(b, 16, free);   // ActualAvailableAllocationUnits
        Wire.Put32(b, 24, SectorsPerUnit);
        Wire.Put32(b, 28, BytesPerSector);
        return b;
    }

    /// <summary>FileFsSizeInformation (2.5.8).</summary>
    public static byte[] FsSize(SmbSpace? space)
    {
        var b = new byte[24];
        var (total, free) = Units(space);
        Wire.Put64(b, 0, total);
        Wire.Put64(b, 8, free);
        Wire.Put32(b, 16, SectorsPerUnit);
        Wire.Put32(b, 20, BytesPerSector);
        return b;
    }

    /// <summary>FileFsAttributeInformation (2.5.1): case-preserved names, a read-only volume when the share is, and a file system name.</summary>
    public static byte[] FsAttribute(bool readOnly)
    {
        const string fileSystemName = "LibSmb";
        var b = new byte[12 + Wire.Utf16Length(fileSystemName)];
        var attributes = 0x00000002u; // FILE_CASE_PRESERVED_NAMES
        if (readOnly)
        {
            attributes |= 0x00080000; // FILE_READ_ONLY_VOLUME
        }

        Wire.Put32(b, 0, attributes);
        Wire.Put32(b, 4, 255); // MaximumComponentNameLength
        Wire.Put32(b, 8, (uint)Wire.Utf16Length(fileSystemName));
        Wire.PutUtf16(b, 12, fileSystemName);
        return b;
    }

    /// <summary>FileFsVolumeInformation (2.5.9): a serial number from the server's GUID and the share's name as the label.</summary>
    public static byte[] FsVolume(ReadOnlySpan<byte> serverGuid, string label)
    {
        var b = new byte[18 + Wire.Utf16Length(label)];
        Wire.Put32(b, 8, Wire.U32(serverGuid, 0)); // VolumeSerialNumber
        Wire.Put32(b, 12, (uint)Wire.Utf16Length(label));
        Wire.PutUtf16(b, 18, label);
        return b;
    }

    /// <summary>FileFsDeviceInformation (2.5.10): a disk.</summary>
    public static byte[] FsDevice()
    {
        var b = new byte[8];
        Wire.Put32(b, 0, 0x00000007); // FILE_DEVICE_DISK
        return b;
    }

    /// <summary>The fixed size of a directory enumeration class's entry and where its name starts; false for a class not served.</summary>
    public static bool DirectoryLayout(byte infoClass, out int fixedLength)
    {
        fixedLength = infoClass switch
        {
            FileInfoClass.Directory => 64,
            FileInfoClass.FullDirectory => 68,
            FileInfoClass.BothDirectory => 94,
            FileInfoClass.Names => 12,
            FileInfoClass.IdBothDirectory => 104,
            FileInfoClass.IdFullDirectory => 80,
            _ => 0,
        };
        return fixedLength != 0;
    }

    /// <summary>
    /// Directory entries in a class's layout, chained, as many as fit in <paramref name="maxLength"/> (always one at least,
    /// so an enumeration moves on). <paramref name="apple"/>: a macOS client that negotiated ReadDirAttr reads max access,
    /// fork size and Finder info from each FileIdBothDirectoryInformation entry (Apple.cs).
    /// </summary>
    public static (byte[] Buffer, int Count) DirectoryEntries(IReadOnlyList<SmbEntry> entries, int maxLength, byte infoClass, SmbShare share, string directory, bool apple)
    {
        if (!DirectoryLayout(infoClass, out var fixedLength))
        {
            infoClass = FileInfoClass.Directory;
            fixedLength = 64;
        }

        var buffer = new List<byte[]>();
        var length = 0;
        foreach (var entry in entries)
        {
            var name = Wire.Utf16(entry.Name);
            var padded = Wire.RoundUp(fixedLength + name.Length, 8);
            if (buffer.Count > 0 && length + padded > maxLength)
            {
                break;
            }

            var e = new byte[padded];
            Wire.Put32(e, 0, (uint)padded); // NextEntryOffset (cleared on the last one below)
            if (infoClass == FileInfoClass.Names)
            {
                Wire.Put32(e, 8, (uint)name.Length);
            }
            else
            {
                WriteTimes(e.AsSpan(8), entry.Modified, entry.Created);
                var size = entry.IsDirectory ? 0 : entry.Length;
                Wire.Put64(e, 40, (ulong)size); // EndOfFile
                Wire.Put64(e, 48, (ulong)size); // AllocationSize
                Wire.Put32(e, 56, Attributes(entry));
                Wire.Put32(e, 60, (uint)name.Length);
                if (infoClass == FileInfoClass.IdFullDirectory)
                {
                    Wire.Put64(e, 72, OpenFile.PathFileId(share.Name, SmbPath.Join(directory, entry.Name)));
                }
                else if (infoClass == FileInfoClass.IdBothDirectory)
                {
                    Wire.Put64(e, 96, OpenFile.PathFileId(share.Name, SmbPath.Join(directory, entry.Name)));
                    if (apple)
                    {
                        Apple.ReadDirAttr(e, share.ReadOnly || IsReadOnlyFile(entry), entry.IsDirectory);
                    }
                }
            }

            name.CopyTo(e, fixedLength);
            buffer.Add(e);
            length += padded;
        }

        var result = new byte[length];
        var at = 0;
        for (var i = 0; i < buffer.Count; i++)
        {
            var e = buffer[i];
            if (i == buffer.Count - 1)
            {
                Wire.Put32(e, 0, 0); // the last entry ends the chain
            }

            e.CopyTo(result, at);
            at += e.Length;
        }

        return (result, buffer.Count);
    }

    /// <summary>The CLOSE response ([MS-SMB2] 2.2.16) with the final attributes the client asked for, and the flag saying so.</summary>
    public static void WriteCloseAttributes(Span<byte> body, uint attributes, long size, DateTimeOffset? modified, DateTimeOffset? created)
    {
        Wire.Put16(body, 2, CloseFlags.PostQueryAttributes);
        WriteTimes(body[8..], modified, created);
        Wire.Put64(body, 40, (ulong)size); // AllocationSize
        Wire.Put64(body, 48, (ulong)size); // EndOfFile
        Wire.Put32(body, 56, attributes);
    }

    /// <summary>Creation, last access, last write and change times, in that order, at the start of <paramref name="b"/>.</summary>
    public static void WriteTimes(Span<byte> b, DateTimeOffset? modified, DateTimeOffset? created)
    {
        var write = Wire.FileTime(modified);
        Wire.Put64(b, 0, created is null ? write : Wire.FileTime(created));
        Wire.Put64(b, 8, write);
        Wire.Put64(b, 16, write);
        Wire.Put64(b, 24, write);
    }

    private static void WriteBasic(Span<byte> b, uint attributes, DateTimeOffset? modified, DateTimeOffset? created)
    {
        WriteTimes(b, modified, created);
        Wire.Put32(b, 32, attributes);
    }

    private static void WriteStandard(Span<byte> b, long size, bool isDirectory)
    {
        Wire.Put64(b, 0, (ulong)size); // AllocationSize
        Wire.Put64(b, 8, (ulong)size); // EndOfFile
        Wire.Put32(b, 16, 1);          // NumberOfLinks
        if (isDirectory)
        {
            b[21] = 1;                 // Directory
        }
    }

    /// <summary>
    /// A volume's size in allocation units. One whose space is unknown is a large volume, 1 TiB with 512 GiB free, as
    /// portable-smb-server had it: with nothing free, a client takes the share for full and refuses to copy to it.
    /// </summary>
    private static (ulong Total, ulong Free) Units(SmbSpace? space)
    {
        const long unit = BytesPerSector * SectorsPerUnit;
        var (total, free) = space is { } s ? (s.TotalBytes, s.FreeBytes) : (1L << 40, 1L << 39);
        return ((ulong)Math.Max(0, total / unit), (ulong)Math.Max(0, free / unit));
    }
}
