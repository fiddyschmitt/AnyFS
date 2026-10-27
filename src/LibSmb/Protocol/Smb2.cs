namespace LibSmb.Protocol;

/// <summary>SMB2 wire constants, as [MS-SMB2] 2.2 names them.</summary>
internal static class Smb2
{
    public const int HeaderSize = 64;

    /// <summary>The largest message accepted over Direct TCP: a sanity bound, far above what is negotiated.</summary>
    public const int MaxMessageSize = 16 * 1024 * 1024;

    /// <summary>The largest READ or WRITE payload, advertised as MaxReadSize and MaxWriteSize.</summary>
    public const int MaxIoSize = 1 << 20;

    public static ReadOnlySpan<byte> Magic => [0xFE, (byte)'S', (byte)'M', (byte)'B'];

    public static bool IsSmb2(ReadOnlySpan<byte> message) => message.Length >= HeaderSize && message[..4].SequenceEqual(Magic);

    /// <summary>A legacy SMB1 negotiate (0xFF 'SMB', command 0x72), which a client may open with to learn of SMB2.</summary>
    public static bool IsSmb1Negotiate(ReadOnlySpan<byte> message)
        => message.Length >= 5 && message[0] == 0xFF && message[1] == (byte)'S' && message[2] == (byte)'M' && message[3] == (byte)'B' && message[4] == 0x72;
}

/// <summary>SMB2 commands ([MS-SMB2] 2.2.1).</summary>
internal enum Command : ushort
{
    Negotiate = 0x0000,
    SessionSetup = 0x0001,
    Logoff = 0x0002,
    TreeConnect = 0x0003,
    TreeDisconnect = 0x0004,
    Create = 0x0005,
    Close = 0x0006,
    Flush = 0x0007,
    Read = 0x0008,
    Write = 0x0009,
    Lock = 0x000A,
    Ioctl = 0x000B,
    Cancel = 0x000C,
    Echo = 0x000D,
    QueryDirectory = 0x000E,
    ChangeNotify = 0x000F,
    QueryInfo = 0x0010,
    SetInfo = 0x0011,
    OplockBreak = 0x0012,
}

/// <summary>SMB2 header flags ([MS-SMB2] 2.2.1.2).</summary>
[Flags]
internal enum HeaderFlags : uint
{
    None = 0,
    ServerToRedirector = 0x00000001,
    AsyncCommand = 0x00000002,
    RelatedOperations = 0x00000004,
    Signed = 0x00000008,
}

/// <summary>SMB2 dialect revisions ([MS-SMB2] 2.2.3).</summary>
internal static class Dialect
{
    public const ushort Smb202 = 0x0202;
    public const ushort Smb210 = 0x0210;
    public const ushort Smb300 = 0x0300;
    public const ushort Smb302 = 0x0302;
    public const ushort Smb311 = 0x0311;

    /// <summary>The SMB2 wildcard, the answer to an SMB1 negotiate that offers SMB2: negotiate again in SMB2.</summary>
    public const ushort Wildcard = 0x02FF;

    /// <summary>What is implemented, most preferred first: up to 3.0.2 (3.1.1, with its negotiate contexts and pre-authentication integrity, is not offered).</summary>
    public static readonly ushort[] Supported = [Smb302, Smb300, Smb210, Smb202];

    public static bool IsSmb3(ushort dialect) => dialect is Smb300 or Smb302;
}

/// <summary>NTSTATUS values ([MS-ERREF] 2.3.1) the server answers with.</summary>
internal static class Status
{
    public const uint Success = 0x00000000;
    public const uint Pending = 0x00000103;
    public const uint BufferOverflow = 0x80000005;
    public const uint NoMoreFiles = 0x80000006;
    public const uint Unsuccessful = 0xC0000001;
    public const uint NotImplemented = 0xC0000002;
    public const uint InvalidInfoClass = 0xC0000003;
    public const uint InvalidParameter = 0xC000000D;
    public const uint NoSuchFile = 0xC000000F;
    public const uint InvalidDeviceRequest = 0xC0000010;
    public const uint EndOfFile = 0xC0000011;
    public const uint MoreProcessingRequired = 0xC0000016;
    public const uint AccessDenied = 0xC0000022;
    public const uint ObjectNameInvalid = 0xC0000033;
    public const uint ObjectNameNotFound = 0xC0000034;
    public const uint ObjectNameCollision = 0xC0000035;
    public const uint ObjectPathNotFound = 0xC000003A;
    public const uint SharingViolation = 0xC0000043;
    public const uint LogonFailure = 0xC000006D;
    public const uint InsufficientResources = 0xC000009A;
    public const uint MediaWriteProtected = 0xC00000A2;
    public const uint FileIsADirectory = 0xC00000BA;
    public const uint NotSupported = 0xC00000BB;
    public const uint NetworkNameDeleted = 0xC00000C9;
    public const uint BadNetworkName = 0xC00000CC;
    public const uint DirectoryNotEmpty = 0xC0000101;
    public const uint NotADirectory = 0xC0000103;
    public const uint FileClosed = 0xC0000128;
    public const uint UserSessionDeleted = 0xC0000203;
    public const uint NotFound = 0xC0000225;
    public const uint DiskFull = 0xC000007F;

    /// <summary>A warning or an error: the client takes the response as failed.</summary>
    public static bool IsError(uint status) => status >= 0xC0000000;
}

/// <summary>NEGOTIATE SecurityMode ([MS-SMB2] 2.2.4).</summary>
internal static class SecurityMode
{
    public const ushort SigningEnabled = 0x0001;
    public const ushort SigningRequired = 0x0002;
}

/// <summary>SESSION_SETUP response SessionFlags ([MS-SMB2] 2.2.6).</summary>
internal static class SessionFlags
{
    public const ushort IsGuest = 0x0001;
    public const ushort IsNull = 0x0002;
}

/// <summary>TREE_CONNECT response ShareType ([MS-SMB2] 2.2.10).</summary>
internal static class ShareType
{
    public const byte Disk = 0x01;
    public const byte Pipe = 0x02;
}

/// <summary>Access masks ([MS-SMB2] 2.2.13.1).</summary>
internal static class Access
{
    public const uint FileWriteData = 0x00000002;
    public const uint FileAppendData = 0x00000004;
    public const uint Delete = 0x00010000;
    public const uint GenericAll = 0x10000000;
    public const uint GenericWrite = 0x40000000;
    public const uint MaximumAllowed = 0x02000000;

    /// <summary>FILE_ALL_ACCESS: what a writable share grants.</summary>
    public const uint All = 0x001F01FF;

    /// <summary>FILE_GENERIC_READ | FILE_GENERIC_EXECUTE: what a read-only share grants.</summary>
    public const uint ReadExecute = 0x001200A9;

    /// <summary>Whether an access mask asks to write (MAXIMUM_ALLOWED does not: such an open reads).</summary>
    public static bool Writes(uint access) => (access & (GenericWrite | GenericAll | FileWriteData | FileAppendData)) != 0;

    public const uint FileReadData = 0x00000001;
    public const uint FileExecute = 0x00000020;
    public const uint GenericRead = 0x80000000;
    public const uint GenericExecute = 0x20000000;

    /// <summary>Whether an access mask reaches a file's data (to read, write or run it); one that doesn't only looks at the file, renames or deletes it.</summary>
    public static bool TouchesData(uint access)
        => (access & (FileReadData | FileWriteData | FileAppendData | FileExecute | GenericRead | GenericWrite | GenericExecute | GenericAll | MaximumAllowed)) != 0;
}

/// <summary>CREATE dispositions ([MS-SMB2] 2.2.13).</summary>
internal static class Disposition
{
    public const uint Supersede = 0;
    public const uint Open = 1;
    public const uint Create = 2;
    public const uint OpenIf = 3;
    public const uint Overwrite = 4;
    public const uint OverwriteIf = 5;
}

/// <summary>CREATE options ([MS-SMB2] 2.2.13).</summary>
internal static class CreateOptions
{
    public const uint DirectoryFile = 0x00000001;
    public const uint NonDirectoryFile = 0x00000040;
    public const uint DeleteOnClose = 0x00001000;
}

/// <summary>CREATE response CreateAction ([MS-SMB2] 2.2.14).</summary>
internal static class CreateAction
{
    public const uint Opened = 1;
    public const uint Created = 2;
    public const uint Overwritten = 3;
}

/// <summary>File attributes ([MS-FSCC] 2.6).</summary>
internal static class FileAttribute
{
    public const uint ReadOnly = 0x00000001;
    public const uint Directory = 0x00000010;
    public const uint Normal = 0x00000080;
}

/// <summary>QUERY_INFO and SET_INFO InfoType ([MS-SMB2] 2.2.37).</summary>
internal static class InfoType
{
    public const byte File = 0x01;
    public const byte FileSystem = 0x02;
    public const byte Security = 0x03;
    public const byte Quota = 0x04;
}

/// <summary>File information classes ([MS-FSCC] 2.4).</summary>
internal static class FileInfoClass
{
    public const byte Directory = 0x01;
    public const byte FullDirectory = 0x02;
    public const byte BothDirectory = 0x03;
    public const byte Basic = 0x04;
    public const byte Standard = 0x05;
    public const byte Internal = 0x06;
    public const byte Ea = 0x07;
    public const byte Rename = 0x0A;
    public const byte Names = 0x0C;
    public const byte Disposition = 0x0D;
    public const byte All = 0x12;
    public const byte Allocation = 0x13;
    public const byte EndOfFile = 0x14;
    public const byte NetworkOpen = 0x22;
    public const byte AttributeTag = 0x23;
    public const byte IdBothDirectory = 0x25;
    public const byte IdFullDirectory = 0x26;
}

/// <summary>File system information classes ([MS-FSCC] 2.5).</summary>
internal static class FsInfoClass
{
    public const byte Volume = 0x01;
    public const byte Size = 0x03;
    public const byte Device = 0x04;
    public const byte Attribute = 0x05;
    public const byte FullSize = 0x07;
}

/// <summary>QUERY_DIRECTORY flags ([MS-SMB2] 2.2.33).</summary>
internal static class QueryDirectoryFlags
{
    public const byte RestartScans = 0x01;
    public const byte ReturnSingleEntry = 0x02;
    public const byte IndexSpecified = 0x04;
    public const byte Reopen = 0x10;
}

/// <summary>CLOSE flags ([MS-SMB2] 2.2.15).</summary>
internal static class CloseFlags
{
    /// <summary>The client wants the file's final attributes back in the CLOSE response.</summary>
    public const ushort PostQueryAttributes = 0x0001;
}

/// <summary>IOCTL control codes ([MS-FSCC] 2.3).</summary>
internal static class Fsctl
{
    /// <summary>Sent by Windows after an SMB 3.x connect with signing, to detect a tampered NEGOTIATE; unanswered, it disconnects.</summary>
    public const uint ValidateNegotiateInfo = 0x00140204;

    /// <summary>Sent (by cifs) to resolve DFS paths: there is no DFS here.</summary>
    public const uint DfsGetReferrals = 0x00060194;

    public const uint DfsGetReferralsEx = 0x000601B0;
}
