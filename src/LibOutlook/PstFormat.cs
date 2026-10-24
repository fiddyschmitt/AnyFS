namespace LibOutlook;

/// <summary>The on-disk variants of an Outlook data file ([MS-PST] 2.2.2.6: wVer in the header).</summary>
public enum PstFormat
{
    /// <summary>Outlook 97 to 2002 (wVer 14 or 15): 32-bit offsets, 512-byte pages, 2 GB at most.</summary>
    Ansi,

    /// <summary>Outlook 2003 onwards (wVer 23): 64-bit offsets, 512-byte pages. Every PST made since, and OSTs before Outlook 2013.</summary>
    Unicode,

    /// <summary>Outlook 2013 onwards OST files (wVer 36 and up): 64-bit offsets, 4 KiB pages, 512-byte block alignment, blocks that may be zlib-compressed.</summary>
    Unicode4K,
}

/// <summary>How a file's data blocks are obfuscated ([MS-PST] 2.2.2.6, bCryptMethod).</summary>
public enum PstEncoding : byte
{
    /// <summary>Stored as is (every OST; a PST made with "no encryption").</summary>
    None = 0,

    /// <summary>"Compressible encryption": a fixed byte substitution ([MS-PST] 5.1). The default for a PST.</summary>
    Permute = 1,

    /// <summary>"High encryption": a rotor-style cipher keyed by the block's id ([MS-PST] 5.2).</summary>
    Cyclic = 2,

    /// <summary>Windows Information Protection (bCryptMethod 0x10): the content cannot be read without the key.</summary>
    WindowsInformationProtection = 0x10,
}

/// <summary>Something in the file is not as [MS-PST] says, or a read of a live file kept returning torn pages.</summary>
public sealed class PstException : IOException
{
    /// <summary>Creates the exception with a message.</summary>
    public PstException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and a cause.</summary>
    public PstException(string message, Exception inner)
        : base(message, inner)
    {
    }
}
