namespace AnyFS.Sources.GitLab;

/// <summary>One entry of a repository folder at a commit, as GitLab lists it: a blob's size (the LFS object's for a file in LFS).</summary>
/// <param name="N">The name in git.</param>
/// <param name="K">'b' a file, 't' a folder, 'c' a submodule, 'l' a symbolic link.</param>
/// <param name="S">The blob's or tree's id.</param>
/// <param name="L">The file's length.</param>
/// <param name="F">Whether the file is in Git LFS.</param>
internal sealed record TreeItem(string N, char K, string S, long L, bool F);
