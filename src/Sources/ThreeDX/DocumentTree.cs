using AnyFS.Sdk;

namespace AnyFS.Sources.ThreeDX;

/// <summary>A file of a document as 3DX lists it (<c>relateddata.files</c>): its id, title, size (null when 3DX gives none) and dates.</summary>
internal sealed record DxFile(string Id, string Title, long? Size, DateTimeOffset? Created, DateTimeOffset? Modified);

/// <summary>A document as 3DX describes it (<c>resources/v1/modeler/documents</c>): one revision, with its files.</summary>
internal sealed record DxDocument(string Id, string Name, string Title, string Revision, string Type, DateTimeOffset? Created, DateTimeOffset? Modified, IReadOnlyList<DxFile> Files);

/// <summary>What a name in a document's folder is: one of the document's files, or a file the source makes.</summary>
internal enum FolderFileKind
{
    Document,
    Link,
    Metadata,
}

/// <summary>A file in a document's folder: the name it shows as, and what it is.</summary>
internal sealed record FolderFile(string Name, FolderFileKind Kind, DxFile? File)
{
    /// <summary>The Internet shortcut to the document, in every folder when the source shows the files it makes.</summary>
    public static readonly FolderFile Link = new(ThreeDXNames.LinkFile, FolderFileKind.Link, null);

    /// <summary>The document's metadata, in every folder when the source shows the files it makes.</summary>
    public static readonly FolderFile Metadata = new(ThreeDXNames.MetadataFile, FolderFileKind.Metadata, null);

    /// <summary>What the content is cached under: the file and its version, so a newer one is never served from an older spool.</summary>
    public string ContentKey => $"{File?.Id}:{File?.Modified?.UtcTicks}:{File?.Size}";
}

/// <summary>
/// A document's folder: the document, the name it shows as, and its files, the document's own and (when the source shows
/// them) the two it makes. Entries are made when asked for, so a tree of hundreds of thousands of documents holds no more
/// than the names.
/// </summary>
internal sealed class DocumentFolder(DxDocument document, string name, FolderFile[] files, Uri? space)
{
    public DxDocument Document => document;

    public string Name => name;

    /// <summary>The folder's entry: dated as the document.</summary>
    public VfsEntry Entry => VfsEntry.Directory(name, document.Modified, document.Created);

    public int FileCount => files.Length + (space is null ? 0 : 2);

    /// <summary>The folder's files: the document's, then the shortcut and the metadata when the source makes them.</summary>
    public IEnumerable<FolderFile> Files
    {
        get
        {
            foreach (var file in files)
            {
                yield return file;
            }

            if (space is not null)
            {
                yield return FolderFile.Link;
                yield return FolderFile.Metadata;
            }
        }
    }

    /// <summary>The file of that name, in either case; null when there is none.</summary>
    public FolderFile? File(string fileName)
    {
        foreach (var file in files)
        {
            if (string.Equals(file.Name, fileName, StringComparison.OrdinalIgnoreCase))
            {
                return file;
            }
        }

        if (space is not null)
        {
            if (string.Equals(fileName, ThreeDXNames.LinkFile, StringComparison.OrdinalIgnoreCase))
            {
                return FolderFile.Link;
            }

            if (string.Equals(fileName, ThreeDXNames.MetadataFile, StringComparison.OrdinalIgnoreCase))
            {
                return FolderFile.Metadata;
            }
        }

        return null;
    }

    /// <summary>A file's entry: a document file dated as 3DX dates it (else as its document), the source's own as the document.</summary>
    public VfsEntry EntryOf(FolderFile file) => file.Kind switch
    {
        FolderFileKind.Document => VfsEntry.File(file.Name, file.File!.Size ?? 0, file.File.Modified ?? document.Modified, file.File.Created ?? document.Created, etag: file.ContentKey),
        FolderFileKind.Link => VfsEntry.File(file.Name, DocumentTree.Link(space!, document.Id).Length, document.Modified, document.Created),
        _ => VfsEntry.File(file.Name, DocumentTree.MetadataPlaceholderLength, document.Modified, document.Created),
    };
}

/// <summary>
/// The tree a 3DX source serves (spec 7.6), made from the documents 3DX lists: a folder per document at the root, named as
/// <see cref="ThreeDXNames.DocumentFolder"/> says, holding the document's files and, when the source shows them, an Internet
/// shortcut to the document in 3DX and its metadata. Names two documents (or two files of one) share are told apart by a
/// number, as Mount3DX does: every document of the name gets one, in the order they were made, with the document's type;
/// every file of the name gets one. Immutable: a refresh makes a new tree and the source swaps it in whole, so a listing
/// never sees half of one; a document fetched afresh replaces its own folder and shares the rest (<see cref="With"/>).
/// Made for the hundreds of thousands of documents a platform holds (200,000 at the user's): names and references only,
/// lookups by name and by id through two dictionaries the trees after a refreshed folder share.
/// </summary>
internal sealed class DocumentTree
{
    /// <summary>The size a listing gives the metadata file before it has been made: it is made when read, at two requests to 3DX.</summary>
    public const long MetadataPlaceholderLength = 1;

    private readonly DxDocument[] _documents;
    private readonly DocumentFolder[] _folders;
    private readonly Dictionary<string, int> _byName;
    private readonly Dictionary<string, int> _byId;

    private DocumentTree(DxDocument[] documents, DocumentFolder[] folders, Dictionary<string, int> byName, Dictionary<string, int> byId, Uri? space, DateTimeOffset taken, long fileCount)
    {
        _documents = documents;
        _folders = folders;
        _byName = byName;
        _byId = byId;
        Space = space;
        Taken = taken;
        FileCount = fileCount;
    }

    /// <summary>An empty tree, before any documents are known.</summary>
    public static DocumentTree Empty { get; } = Build([], space: null, DateTimeOffset.MinValue);

    /// <summary>The documents the tree was made from, as 3DX listed them (what the source saves and builds again from).</summary>
    public IReadOnlyList<DxDocument> Documents => _documents;

    /// <summary>When 3DX was asked for the documents.</summary>
    public DateTimeOffset Taken { get; }

    /// <summary>The 3DSpace service the shortcuts lead to; null when the folders hold no files the source makes.</summary>
    public Uri? Space { get; }

    /// <summary>The documents' folders, as the root lists them.</summary>
    public IReadOnlyList<DocumentFolder> Folders => _folders;

    public long FileCount { get; }

    public DocumentFolder? Folder(string name) => _byName.TryGetValue(name, out var i) ? _folders[i] : null;

    /// <summary>A document's folder by the document's id (3DX's ids are hex: either case).</summary>
    public DocumentFolder? ById(string id) => _byId.TryGetValue(id, out var i) ? _folders[i] : null;

    /// <param name="space">The 3DSpace service the documents are on, when each folder is to hold the document's <see cref="ThreeDXNames.LinkFile"/> (to it) and <see cref="ThreeDXNames.MetadataFile"/>; null for neither.</param>
    public static DocumentTree Build(IReadOnlyList<DxDocument> documents, Uri? space, DateTimeOffset taken)
    {
        // the same document twice (3DSearch can list one on two pages while the index changes) shows once
        var byId = new Dictionary<string, int>(documents.Count, StringComparer.OrdinalIgnoreCase);
        var unique = new List<DxDocument>(documents.Count);
        foreach (var document in documents)
        {
            if (byId.TryAdd(document.Id, unique.Count))
            {
                unique.Add(document);
            }
        }

        var all = unique.ToArray();
        var names = FolderNames(all);
        var folders = new DocumentFolder[all.Length];
        var byName = new Dictionary<string, int>(all.Length, StringComparer.OrdinalIgnoreCase);
        var fileCount = 0L;
        for (var i = 0; i < all.Length; i++)
        {
            folders[i] = new DocumentFolder(all[i], names[i], FilesOf(all[i], space), space);
            byName.TryAdd(names[i], i);
            fileCount += folders[i].FileCount;
        }

        return new DocumentTree(all, folders, byName, byId, space, taken, fileCount);
    }

    /// <summary>
    /// A copy of the tree with one document as 3DX now describes it (or without it, when it is gone): a folder listed afresh.
    /// When its folder keeps its name (the name, revision and title unchanged, and where it is numbered, its type and date
    /// too) only that folder is new and the rest is shared; otherwise the tree is made again.
    /// </summary>
    public DocumentTree With(string id, DxDocument? document)
    {
        if (document is not null && _byId.TryGetValue(id, out var index) && document.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
        {
            var old = _folders[index];
            var oldBase = ThreeDXNames.DocumentFolder(old.Document.Name, old.Document.Revision, old.Document.Title);
            var newBase = ThreeDXNames.DocumentFolder(document.Name, document.Revision, document.Title);
            var sameNumber = old.Name == oldBase || (document.Type == old.Document.Type && document.Created == old.Document.Created);
            if (string.Equals(oldBase, newBase, StringComparison.Ordinal) && sameNumber)
            {
                var folder = new DocumentFolder(document, old.Name, FilesOf(document, Space), Space);
                var documents = (DxDocument[])_documents.Clone();
                var folders = (DocumentFolder[])_folders.Clone();
                documents[index] = document;
                folders[index] = folder;
                return new DocumentTree(documents, folders, _byName, _byId, Space, Taken, FileCount - old.FileCount + folder.FileCount);
            }
        }

        var list = _documents.Where(d => !d.Id.Equals(id, StringComparison.OrdinalIgnoreCase)).ToList();
        if (document is not null)
        {
            var at = Array.FindIndex(_documents, d => d.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            list.Insert(at < 0 ? list.Count : at, document);
        }

        return Build(list, Space, Taken);
    }

    /// <summary>The folder name of each document, by index: its own, or numbered where documents share it.</summary>
    private static string[] FolderNames(DxDocument[] documents)
    {
        var names = new string[documents.Length];
        var counts = new Dictionary<string, int>(documents.Length, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < documents.Length; i++)
        {
            names[i] = ThreeDXNames.DocumentFolder(documents[i].Name, documents[i].Revision, documents[i].Title);
            counts[names[i]] = counts.GetValueOrDefault(names[i]) + 1;
        }

        if (counts.Count == names.Length)
        {
            return names;
        }

        var used = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        foreach (var group in Enumerable.Range(0, names.Length).Where(i => counts[names[i]] > 1).GroupBy(i => names[i], StringComparer.OrdinalIgnoreCase))
        {
            var number = 1;
            foreach (var i in group.OrderBy(i => documents[i].Created ?? DateTimeOffset.MaxValue).ThenBy(i => documents[i].Id, StringComparer.OrdinalIgnoreCase))
            {
                string candidate;
                do
                {
                    candidate = ThreeDXNames.NumberedFolder(names[i], number++, documents[i].Type);
                }
                while (!used.Add(candidate));

                names[i] = candidate;
            }
        }

        return names;
    }

    /// <summary>A document's files under the names they show as: their titles, numbered where two share one or where one takes a name the source's own files have.</summary>
    private static FolderFile[] FilesOf(DxDocument document, Uri? space)
    {
        var count = document.Files.Count;
        var names = new string[count];
        var clash = false;
        for (var i = 0; i < count; i++)
        {
            names[i] = ThreeDXNames.FileName(document.Files[i].Title);
            clash |= space is not null && (names[i].Equals(ThreeDXNames.LinkFile, StringComparison.OrdinalIgnoreCase) || names[i].Equals(ThreeDXNames.MetadataFile, StringComparison.OrdinalIgnoreCase));
            for (var j = 0; j < i && !clash; j++)
            {
                clash = names[i].Equals(names[j], StringComparison.OrdinalIgnoreCase);
            }
        }

        if (clash)
        {
            Number(document, names, space is not null);
        }

        var files = new FolderFile[count];
        for (var i = 0; i < count; i++)
        {
            files[i] = new FolderFile(names[i], FolderFileKind.Document, document.Files[i]);
        }

        return files;
    }

    private static void Number(DxDocument document, string[] names, bool generated)
    {
        var used = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        if (generated)
        {
            used.Add(ThreeDXNames.LinkFile);
            used.Add(ThreeDXNames.MetadataFile);
        }

        foreach (var group in Enumerable.Range(0, names.Length).GroupBy(i => names[i], StringComparer.OrdinalIgnoreCase)
                     .Where(g => g.Count() > 1 || generated && (g.Key.Equals(ThreeDXNames.LinkFile, StringComparison.OrdinalIgnoreCase) || g.Key.Equals(ThreeDXNames.MetadataFile, StringComparison.OrdinalIgnoreCase))))
        {
            var number = 1;
            foreach (var i in group.OrderBy(i => document.Files[i].Created ?? DateTimeOffset.MaxValue).ThenBy(i => document.Files[i].Id, StringComparer.OrdinalIgnoreCase))
            {
                string candidate;
                do
                {
                    candidate = ThreeDXNames.NumberedFile(names[i], number++);
                }
                while (!used.Add(candidate));

                names[i] = candidate;
            }
        }
    }

    /// <summary>
    /// The Internet shortcut that opens a document in 3DSpace (<c>common/emxTree.jsp?objectId=</c>), as Windows writes one:
    /// what <see cref="ThreeDXNames.LinkFile"/> holds.
    /// </summary>
    public static byte[] Link(Uri space, string documentId)
        => System.Text.Encoding.UTF8.GetBytes($"[InternetShortcut]\r\nURL={new Uri(space, "common/emxTree.jsp?objectId=" + Uri.EscapeDataString(documentId)).AbsoluteUri}\r\n");
}
