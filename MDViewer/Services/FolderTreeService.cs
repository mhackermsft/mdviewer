using System.IO;
using MDViewer.Models;

namespace MDViewer.Services;

/// <summary>
/// The scanned contents of an opened folder: Markdown files and the
/// sub-folders that (directly or indirectly) contain them.
/// </summary>
public sealed class FolderTree
{
    private readonly Dictionary<string, FileTreeNode> _index;

    internal FolderTree(string rootPath, IReadOnlyList<FileTreeNode> nodes, Dictionary<string, FileTreeNode> index)
    {
        RootPath = rootPath;
        Nodes = nodes;
        _index = index;
    }

    public string RootPath { get; }
    public IReadOnlyList<FileTreeNode> Nodes { get; }

    public FileTreeNode? Find(string path)
    {
        try
        {
            return _index.TryGetValue(Path.GetFullPath(path), out var node) ? node : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>First Markdown file in tree display order (depth-first).</summary>
    public FileTreeNode? FirstFile() => AllNodes().FirstOrDefault(n => !n.IsDirectory);

    public IEnumerable<FileTreeNode> AllNodes()
    {
        var stack = new Stack<FileTreeNode>(Nodes.Reverse());
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            yield return node;
            for (var i = node.Children.Count - 1; i >= 0; i--)
                stack.Push(node.Children[i]);
        }
    }
}

public static class FolderTreeService
{
    private static readonly HashSet<string> ExcludedDirectories =
        new(StringComparer.OrdinalIgnoreCase) { "node_modules" };

    private static readonly EnumerationOptions EnumerationOptions = new()
    {
        IgnoreInaccessible = true,
        RecurseSubdirectories = false,
        AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint
    };

    public static bool IsMarkdownFile(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Equals(".md", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".markdown", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Scans <paramref name="rootPath"/> recursively. Folders are listed before
    /// files and both are sorted like Windows Explorer. Folders without any
    /// Markdown file underneath are omitted.
    /// </summary>
    public static FolderTree Build(string rootPath)
    {
        var root = Path.GetFullPath(rootPath);
        var index = new Dictionary<string, FileTreeNode>(StringComparer.OrdinalIgnoreCase);
        var nodes = BuildChildren(root, null, index);
        return new FolderTree(root, nodes, index);
    }

    private static List<FileTreeNode> BuildChildren(
        string directory, FileTreeNode? parent, Dictionary<string, FileTreeNode> index)
    {
        var result = new List<FileTreeNode>();

        foreach (var subDir in SafeEnumerate(() => Directory.EnumerateDirectories(directory, "*", EnumerationOptions)))
        {
            var name = Path.GetFileName(subDir);
            if (name.StartsWith('.') || ExcludedDirectories.Contains(name))
                continue;

            var node = new FileTreeNode(name, subDir, isDirectory: true, parent);
            node.Children.AddRange(BuildChildren(subDir, node, index));
            if (node.Children.Count == 0)
                continue;

            index[subDir] = node;
            result.Add(node);
        }

        foreach (var file in SafeEnumerate(() => Directory.EnumerateFiles(directory, "*", EnumerationOptions)))
        {
            if (!IsMarkdownFile(file))
                continue;

            var node = new FileTreeNode(Path.GetFileName(file), file, isDirectory: false, parent);
            index[file] = node;
            result.Add(node);
        }

        return result;
    }

    private static List<string> SafeEnumerate(Func<IEnumerable<string>> enumerate)
    {
        try
        {
            var items = enumerate().ToList();
            items.Sort((a, b) => NativeMethods.StrCmpLogicalW(Path.GetFileName(a), Path.GetFileName(b)));
            return items;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new List<string>();
        }
    }
}
