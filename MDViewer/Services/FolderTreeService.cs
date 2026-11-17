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
    /// <summary>
    /// Dependency, build and tool folders of programming-language ecosystems,
    /// skipped by exact name (case-insensitive) at any level below the root.
    /// Generic names (build, dist, out, target, vendor, packages, lib) are
    /// deliberately absent: they may hold the user's own documentation.
    /// </summary>
    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        // JavaScript / TypeScript
        "node_modules", "bower_components", "jspm_packages",
        ".next", ".nuxt", ".svelte-kit", ".angular", ".parcel-cache", ".turbo",
        // .NET
        "bin", "obj", ".vs",
        // Python
        "__pycache__", ".venv", "venv", ".tox", ".mypy_cache", ".pytest_cache", ".ruff_cache",
        // Java / Kotlin
        ".gradle", ".m2",
        // Version control
        ".git", ".svn", ".hg",
        // IDEs
        ".idea",
    };

    // Hidden, system and reparse-point (symlink / junction) entries are included.
    private static readonly EnumerationOptions EnumerationOptions = new()
    {
        IgnoreInaccessible = true,
        RecurseSubdirectories = false,
        AttributesToSkip = 0
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

        // Real (link-resolved) folders already scanned; guards against cycles
        // created by symlinks and junctions. Nodes and the index keep the
        // logical path under the root.
        var visitedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (ResolveRealPath(root) is { } realRoot)
            visitedTargets.Add(realRoot);

        var nodes = BuildChildren(root, null, index, visitedTargets);
        return new FolderTree(root, nodes, index);
    }

    /// <summary>
    /// Scans <paramref name="directory"/> with the same rules as <see cref="Build"/>
    /// (exclusions, pruning, natural order, link-cycle guard) and returns its
    /// children, parented to <paramref name="parent"/> and added to
    /// <paramref name="index"/>. Used by the Reversa views for folder subtrees.
    /// </summary>
    internal static List<FileTreeNode> BuildSubtree(
        string directory, FileTreeNode? parent, Dictionary<string, FileTreeNode> index)
    {
        var full = Path.GetFullPath(directory);
        var visitedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (ResolveRealPath(full) is { } realDir)
            visitedTargets.Add(realDir);
        return BuildChildren(full, parent, index, visitedTargets);
    }

    /// <summary>True for folder names skipped by every scan (dependencies, build, tools).</summary>
    internal static bool IsExcludedDirectory(string name) => ExcludedDirectories.Contains(name);

    /// <summary>Lists entries sorted like Windows Explorer; empty when unreadable.</summary>
    internal static List<string> EnumerateSorted(string directory, bool directories) =>
        SafeEnumerate(() => directories
            ? Directory.EnumerateDirectories(directory, "*", EnumerationOptions)
            : Directory.EnumerateFiles(directory, "*", EnumerationOptions));

    private static List<FileTreeNode> BuildChildren(
        string directory, FileTreeNode? parent, Dictionary<string, FileTreeNode> index,
        HashSet<string> visitedTargets)
    {
        var result = new List<FileTreeNode>();

        foreach (var subDir in SafeEnumerate(() => Directory.EnumerateDirectories(directory, "*", EnumerationOptions)))
        {
            var name = Path.GetFileName(subDir);
            if (ExcludedDirectories.Contains(name))
                continue;

            // Broken links resolve to null; already visited targets are skipped.
            if (ResolveRealPath(subDir) is not { } realPath || !visitedTargets.Add(realPath))
                continue;

            var node = new FileTreeNode(name, subDir, isDirectory: true, parent);
            foreach (var child in BuildChildren(subDir, node, index, visitedTargets))
                node.Children.Add(child);
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

    /// <summary>
    /// Returns the final target of a symlink / junction, or the normalized path
    /// itself for a regular folder. Null when the link is broken or unreadable.
    /// </summary>
    private static string? ResolveRealPath(string directory)
    {
        try
        {
            var info = new DirectoryInfo(directory);
            if (info.LinkTarget == null)
                return Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));

            var target = info.ResolveLinkTarget(returnFinalTarget: true);
            if (target == null || !target.Exists)
                return null;
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(target.FullName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
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
