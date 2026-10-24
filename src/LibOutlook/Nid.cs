namespace LibOutlook;

/// <summary>Node ids ([MS-PST] 2.2.2.1): the low five bits give the node's type; a few ids are fixed.</summary>
internal static class NodeId
{
    public const uint MessageStore = 0x21;
    public const uint NameToIdMap = 0x61;
    public const uint RootFolder = 0x122;

    /// <summary>A message's recipient table, a subnode of the message.</summary>
    public const uint RecipientTable = 0x692;

    /// <summary>A message's attachment table, a subnode of the message.</summary>
    public const uint AttachmentTable = 0x671;

    public const uint TypeHid = 0x00;
    public const uint TypeInternal = 0x01;
    public const uint TypeNormalFolder = 0x02;
    public const uint TypeSearchFolder = 0x03;
    public const uint TypeNormalMessage = 0x04;
    public const uint TypeAttachment = 0x05;
    public const uint TypeAssocMessage = 0x08;
    public const uint TypeHierarchyTable = 0x0D;
    public const uint TypeContentsTable = 0x0E;
    public const uint TypeAssocContentsTable = 0x0F;
    public const uint TypeSearchContentsTable = 0x10;

    public static uint TypeOf(uint nid) => nid & 0x1F;

    /// <summary>The same node index with another type: a folder's tables share its index ([MS-PST] 2.4.4).</summary>
    public static uint WithType(uint nid, uint type) => (nid & ~0x1Fu) | type;

    public static bool IsFolder(uint nid) => TypeOf(nid) is TypeNormalFolder or TypeSearchFolder;

    public static bool IsMessage(uint nid) => TypeOf(nid) is TypeNormalMessage or TypeAssocMessage;
}
