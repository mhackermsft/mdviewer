using System.Collections.ObjectModel;
using System.IO;
using MDViewer.Models;
using MDViewer.Services;

namespace MDViewer.ViewModels;

/// <summary>
/// Folder side of the window: the root folder and its tree, the Reversa views
/// (feature 010), the "Open files" section and the tree selection. Holds no
/// WPF controls; the window binds <see cref="Items"/> and mirrors the rest.
/// Read-only towards the disk: nothing here writes to the folder.
/// </summary>
public sealed class FolderViewModel
{
    private FolderTree? _folderTree;
    private FileTreeNode? _selectedNode;

    /// <summary>Full path of the open root folder; null when none is open.</summary>
    public string? RootFolder { get; private set; }

    /// <summary>The Files view of the root folder; null until the first scan is applied.</summary>
    public FolderTree? FolderTree => _folderTree;

    /// <summary>The Reversa install of the root folder; null for a plain folder.</summary>
    public ReversaProject? Reversa { get; private set; }

    public TreeViewMode ViewMode { get; private set; } = TreeViewMode.Files;

    /// <summary>
    /// Markdown files opened outside the folder tree (menu, drag and drop,
    /// links). Session only, never persisted; survives opening another folder.
    /// </summary>
    public FileTreeNode OpenFilesSection { get; } =
        new("Open files", string.Empty, isDirectory: true, parent: null) { IsSection = true, IsExpanded = true };

    /// <summary>Top-level tree items: the "Open files" section (when not empty), then the active view's nodes.</summary>
    public ObservableCollection<FileTreeNode> Items { get; } = new();

    public FileTreeNode? SelectedNode => _selectedNode;

    /// <summary>The tree shown by the active view; Files outside Reversa folders.</summary>
    public FolderTree? CurrentViewTree => ViewMode switch
    {
        TreeViewMode.Skills when Reversa != null => Reversa.Skills,
        TreeViewMode.Flow when Reversa != null => Reversa.Flow,
        _ => _folderTree,
    };

    /// <summary>Header text: the folder's own name, or the full path for a drive root.</summary>
    public string? FolderDisplayName => RootFolder == null
        ? null
        : Path.GetFileName(Path.TrimEndingDirectorySeparator(RootFolder)) is { Length: > 0 } name ? name : RootFolder;

    #region Folder

    /// <summary>
    /// Starts a new root folder: no tree yet, no Reversa, Files view, nothing
    /// selected. The "Open files" section is kept; <see cref="ApplyScan"/>
    /// later drops the files that turn out to be part of the tree.
    /// </summary>
    public void Open(string folder)
    {
        RootFolder = Path.GetFullPath(folder);
        _folderTree = null;
        Reversa = null;
        ViewMode = TreeViewMode.Files;
        SyncSelection(null);
    }

    /// <summary>Reads the folder tree and the Reversa install. Slow (disk I/O): call off the UI thread.</summary>
    public static (FolderTree Tree, ReversaProject? Reversa) Scan(string root) =>
        (FolderTreeService.Build(root), DetectReversa(root));

    /// <summary>
    /// Installs a <see cref="Scan"/> result of <paramref name="root"/>, keeping
    /// the expansion state of every view. Returns false, changing nothing, when
    /// another folder was opened while scanning.
    /// </summary>
    public bool ApplyScan(string root, FolderTree tree, ReversaProject? reversa)
    {
        if (!string.Equals(root, RootFolder, StringComparison.OrdinalIgnoreCase)) return false;

        // Expansion by FullPath (synthetic "reversa:" keys included), so a
        // refresh keeps the tree as the user left it.
        RestoreExpansion(tree, CaptureExpansion(_folderTree));
        RestoreExpansion(reversa?.Skills, CaptureExpansion(Reversa?.Skills));
        RestoreExpansion(reversa?.Flow, CaptureExpansion(Reversa?.Flow));

        // Files that are now part of the tree leave the "Open files" section.
        foreach (var openFile in OpenFilesSection.Children.ToList())
        {
            if (tree.Find(openFile.FullPath) != null)
                OpenFilesSection.Children.Remove(openFile);
        }

        // The section's nodes survive the rebind: drop the old selection flag,
        // or the regenerated item would be re-selected (and reopened) later.
        if (_selectedNode != null)
            _selectedNode.IsSelected = false;
        _selectedNode = null;

        _folderTree = tree;
        Reversa = reversa;
        if (reversa == null)
            ViewMode = TreeViewMode.Files;

        Rebind();
        return true;
    }

    /// <summary>True when <paramref name="fullPath"/> is a document of the Files view.</summary>
    public bool IsInTree(string fullPath) => _folderTree?.Find(fullPath) != null;

    #endregion

    #region Open files

    /// <summary>
    /// Records a file opened by the user: one outside the folder tree joins
    /// the "Open files" section (no duplicates, in opening order). Returns its full path.
    /// </summary>
    public string TrackOpenedFile(string filePath)
    {
        var fullPath = Path.GetFullPath(filePath);
        if (!IsInTree(fullPath))
            AddOpenFile(fullPath);
        return fullPath;
    }

    public FileTreeNode? FindOpenFile(string filePath)
    {
        string fullPath;
        try { fullPath = Path.GetFullPath(filePath); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        return OpenFilesSection.Children.FirstOrDefault(n =>
            string.Equals(n.FullPath, fullPath, StringComparison.OrdinalIgnoreCase));
    }

    public bool IsOpenFile(FileTreeNode node) => ReferenceEquals(node.Parent, OpenFilesSection);

    /// <summary>Removes a file from the "Open files" section (hiding it when empty).</summary>
    public void RemoveOpenFile(string filePath)
    {
        if (FindOpenFile(filePath) is not { } node) return;

        if (ReferenceEquals(node, _selectedNode))
            _selectedNode = null;
        node.IsSelected = false;
        OpenFilesSection.Children.Remove(node);
        if (OpenFilesSection.Children.Count == 0)
            Rebind();
    }

    private void AddOpenFile(string fullPath)
    {
        if (FindOpenFile(fullPath) != null) return;

        OpenFilesSection.Children.Add(
            new FileTreeNode(Path.GetFileName(fullPath), fullPath, isDirectory: false, OpenFilesSection));
        OpenFilesSection.IsExpanded = true;
        if (OpenFilesSection.Children.Count == 1)
            Rebind();
    }

    #endregion

    #region Selection

    /// <summary>
    /// Selects the node of <paramref name="filePath"/> (expanding its parent
    /// folders), looking in the active view and then in the "Open files"
    /// section, or clears the selection when the file is in neither.
    /// </summary>
    public void SyncSelection(string? filePath)
    {
        var node = filePath != null ? CurrentViewTree?.Find(filePath) ?? FindOpenFile(filePath) : null;
        if (ReferenceEquals(node, _selectedNode)) return;

        if (_selectedNode != null)
            _selectedNode.IsSelected = false;

        _selectedNode = node;
        if (node == null) return;

        for (var parent = node.Parent; parent != null; parent = parent.Parent)
            parent.IsExpanded = true;
        node.IsSelected = true;
    }

    /// <summary>The user selected <paramref name="node"/> in the tree.</summary>
    public void OnNodeSelected(FileTreeNode node) => _selectedNode = node;

    #endregion

    #region Views and Flow

    /// <summary>
    /// Shows another view of the same folder, re-syncing the selection to
    /// <paramref name="currentFile"/>. Returns false when nothing changed (same
    /// view, or a Reversa view of a plain folder).
    /// </summary>
    public bool SwitchView(TreeViewMode mode, string? currentFile)
    {
        if (mode == ViewMode) return false;
        if (mode != TreeViewMode.Files && Reversa == null) return false;

        if (_selectedNode != null)
            _selectedNode.IsSelected = false;
        _selectedNode = null;

        ViewMode = mode;
        Rebind();
        SyncSelection(currentFile);
        return true;
    }

    /// <summary>True in the Flow view while <paramref name="currentFile"/> is a Flow document (footer and Prev/Next shown).</summary>
    public bool IsFlowActive(string? currentFile) =>
        ViewMode == TreeViewMode.Flow && FlowNavigator.IndexOf(Reversa?.FlowDocuments, currentFile) >= 0;

    public (string? Prev, string? Next) FlowNeighbors(string? path) =>
        FlowNavigator.Neighbors(Reversa?.FlowDocuments, path);

    /// <summary>Tree label of a Flow document (e.g. "addenda/003-x.md"), else its file name.</summary>
    public string FlowLabel(string path) =>
        Reversa?.Flow.Find(path)?.Name ?? Path.GetFileName(path);

    /// <summary>Labels for the page footer of <paramref name="path"/>.</summary>
    public (string? PrevLabel, string? NextLabel) BuildFlowFooter(string path)
    {
        var (prev, next) = FlowNeighbors(path);
        return (prev != null ? FlowLabel(prev) : null, next != null ? FlowLabel(next) : null);
    }

    #endregion

    /// <summary>Recomposes <see cref="Items"/> from the section and the active view.</summary>
    private void Rebind()
    {
        Items.Clear();
        if (OpenFilesSection.Children.Count > 0)
            Items.Add(OpenFilesSection);
        if (CurrentViewTree is { } tree)
        {
            foreach (var node in tree.Nodes)
                Items.Add(node);
        }
    }

    /// <summary>Detection never fails the folder scan: a broken Reversa install is a plain folder.</summary>
    private static ReversaProject? DetectReversa(string root)
    {
        try { return ReversaProjectService.Detect(root); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static Dictionary<string, bool> CaptureExpansion(FolderTree? tree)
    {
        var state = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        if (tree == null) return state;
        foreach (var node in tree.AllNodes())
        {
            if (node.IsDirectory)
                state.TryAdd(node.FullPath, node.IsExpanded);
        }
        return state;
    }

    private static void RestoreExpansion(FolderTree? tree, Dictionary<string, bool> state)
    {
        if (tree == null || state.Count == 0) return;
        foreach (var node in tree.AllNodes())
        {
            if (node.IsDirectory && state.TryGetValue(node.FullPath, out var expanded))
                node.IsExpanded = expanded;
        }
    }
}
