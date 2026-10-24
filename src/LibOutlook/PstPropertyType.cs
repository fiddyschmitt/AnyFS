namespace LibOutlook;

/// <summary>The MAPI property types ([MS-OXCDATA] 2.11.1) as a PST stores them in its property contexts and tables.</summary>
public enum PstPropertyType : ushort
{
    /// <summary>PtypUnspecified: the type is not given.</summary>
    Unspecified = 0x0000,

    /// <summary>PtypNull: no value.</summary>
    Null = 0x0001,

    /// <summary>PtypInteger16: a 16-bit signed integer.</summary>
    Integer16 = 0x0002,

    /// <summary>PtypInteger32: a 32-bit signed integer.</summary>
    Integer32 = 0x0003,

    /// <summary>PtypFloating32: a 32-bit float.</summary>
    Floating32 = 0x0004,

    /// <summary>PtypFloating64: a 64-bit float.</summary>
    Floating64 = 0x0005,

    /// <summary>PtypCurrency: a 64-bit integer of ten-thousandths of a unit, decoded to a <see cref="decimal"/>.</summary>
    Currency = 0x0006,

    /// <summary>PtypFloatingTime: an OLE date (days since 30 December 1899), decoded to a <see cref="DateTime"/>.</summary>
    FloatingTime = 0x0007,

    /// <summary>PtypErrorCode: a 32-bit HRESULT.</summary>
    ErrorCode = 0x000A,

    /// <summary>PtypBoolean: a single byte, zero for false.</summary>
    Boolean = 0x000B,

    /// <summary>PtypObject: an embedded object, which in a PST means a subnode holding it (an attached message, an OLE document).</summary>
    Object = 0x000D,

    /// <summary>PtypInteger64: a 64-bit signed integer.</summary>
    Integer64 = 0x0014,

    /// <summary>PtypString8: a string in the item's code page.</summary>
    String8 = 0x001E,

    /// <summary>PtypString: a UTF-16 string.</summary>
    String = 0x001F,

    /// <summary>PtypTime: a FILETIME, decoded to a UTC <see cref="DateTime"/>.</summary>
    Time = 0x0040,

    /// <summary>PtypGuid: a 16-byte GUID.</summary>
    Guid = 0x0048,

    /// <summary>PtypServerId: a server id, left as bytes.</summary>
    ServerId = 0x00FB,

    /// <summary>PtypRestriction: a restriction, left as bytes.</summary>
    Restriction = 0x00FD,

    /// <summary>PtypRuleAction: rule actions, left as bytes.</summary>
    RuleAction = 0x00FE,

    /// <summary>PtypBinary: bytes.</summary>
    Binary = 0x0102,

    /// <summary>PtypMultipleInteger16: an array of 16-bit integers.</summary>
    MultipleInteger16 = 0x1002,

    /// <summary>PtypMultipleInteger32: an array of 32-bit integers.</summary>
    MultipleInteger32 = 0x1003,

    /// <summary>PtypMultipleFloating32: an array of 32-bit floats.</summary>
    MultipleFloating32 = 0x1004,

    /// <summary>PtypMultipleFloating64: an array of 64-bit floats.</summary>
    MultipleFloating64 = 0x1005,

    /// <summary>PtypMultipleCurrency: an array of currency values.</summary>
    MultipleCurrency = 0x1006,

    /// <summary>PtypMultipleFloatingTime: an array of OLE dates.</summary>
    MultipleFloatingTime = 0x1007,

    /// <summary>PtypMultipleInteger64: an array of 64-bit integers.</summary>
    MultipleInteger64 = 0x1014,

    /// <summary>PtypMultipleString8: an array of strings in the item's code page.</summary>
    MultipleString8 = 0x101E,

    /// <summary>PtypMultipleString: an array of UTF-16 strings.</summary>
    MultipleString = 0x101F,

    /// <summary>PtypMultipleTime: an array of FILETIMEs.</summary>
    MultipleTime = 0x1040,

    /// <summary>PtypMultipleGuid: an array of GUIDs.</summary>
    MultipleGuid = 0x1048,

    /// <summary>PtypMultipleBinary: an array of byte arrays.</summary>
    MultipleBinary = 0x1102,
}
