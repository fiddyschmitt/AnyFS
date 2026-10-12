using AnyFS.Core.Runtime;
using AnyFS.Sdk;

namespace AnyFS.Core.Vfs;

/// <summary>A source's membership of a collection as the router sees it.</summary>
public sealed record CollectionMember(SourceHandle Source, string Path, bool ReadOnly);

/// <summary>
/// The synthetic levels of a collection's tree (spec 4.1): the segments of every member's source path,
/// matched case-insensitively, with the members attached where their paths end. Immutable; rebuilt
/// when memberships change.
/// </summary>
public sealed class SyntheticTree
{
    private SyntheticTree(Node root, IReadOnlyList<CollectionMember> members)
    {
        Root = root;
        Members = members;
    }

    public Node Root { get; }

    public IReadOnlyList<CollectionMember> Members { get; }

    public static SyntheticTree Empty { get; } = new(new Node("", ""), []);

    public static SyntheticTree Build(IEnumerable<CollectionMember> members)
    {
        var root = new Node("", "");
        var list = members.OrderBy(m => m.Source.Name, StringComparer.OrdinalIgnoreCase).ThenBy(m => m.Source.Id).ToList();
        foreach (var member in list)
        {
            var node = root;
            foreach (var segment in VfsPath.Split(member.Path))
            {
                if (!node.ChildMap.TryGetValue(segment, out var child))
                {
                    child = new Node(segment, VfsPath.Join(node.Path, segment));
                    node.ChildMap[segment] = child;
                }

                node = child;
            }

            node.AttachmentList.Add(new Attachment(member.Source, member.ReadOnly, node.Path));
        }

        return new SyntheticTree(root, list);
    }

    /// <summary>Walks as far as synthetic nodes go; <c>Depth</c> says how many segments were consumed.</summary>
    public (Node Node, int Depth) Resolve(string[] segments)
    {
        var node = Root;
        var depth = 0;
        while (depth < segments.Length && node.ChildMap.TryGetValue(segments[depth], out var child))
        {
            node = child;
            depth++;
        }

        return (node, depth);
    }

    public sealed class Node(string name, string path)
    {
        internal Dictionary<string, Node> ChildMap { get; } = new(StringComparer.OrdinalIgnoreCase);

        internal List<Attachment> AttachmentList { get; } = [];

        public string Name { get; } = name;

        /// <summary>The node's path from the collection root (normalised, '/'-separated).</summary>
        public string Path { get; } = path;

        public IEnumerable<Node> Children => ChildMap.Values.OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase);

        /// <summary>Sources whose path ends here, in name order (the order that decides duplicates).</summary>
        public IReadOnlyList<Attachment> Attachments => AttachmentList;

        public bool HasChild(string name) => ChildMap.ContainsKey(name);

        public Node? Child(string name) => ChildMap.GetValueOrDefault(name);
    }

    public sealed record Attachment(SourceHandle Source, bool ReadOnly, string MountPath);
}
