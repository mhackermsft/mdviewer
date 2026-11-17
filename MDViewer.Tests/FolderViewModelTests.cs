using MDViewer.Models;
using MDViewer.ViewModels;

namespace MDViewer.Tests;

public sealed class FolderViewModelTests : IDisposable
{
    private readonly TempFolder _tmp = new();
    private readonly TempFolder _outside = new();

    public void Dispose()
    {
        _tmp.Dispose();
        _outside.Dispose();
    }

    /// <summary>Opens <paramref name="folder"/> and applies its scan, as the window does.</summary>
    private static FolderViewModel OpenAndScan(string folder, FolderViewModel? vm = null)
    {
        vm ??= new FolderViewModel();
        vm.Open(folder);
        Rescan(vm);
        return vm;
    }

    private static void Rescan(FolderViewModel vm)
    {
        var (tree, reversa) = FolderViewModel.Scan(vm.RootFolder!);
        Assert.True(vm.ApplyScan(vm.RootFolder!, tree, reversa));
    }

    private void ReversaFolder()
    {
        _tmp.File(".reversa/state.json", "{\"version\":\"1.3.6\"}");
        _tmp.File(".claude/skills/reversa/SKILL.md", "---\nname: reversa\n---\n");
        _tmp.File("_reversa_sdd/inventory.md");
        _tmp.File("_reversa_sdd/architecture.md");
    }

    private static List<string> Names(IEnumerable<FileTreeNode> nodes) => nodes.Select(n => n.Name).ToList();

    #region Folder

    [Fact]
    public void Open_starts_empty_in_the_files_view()
    {
        _tmp.File("a.md");
        var vm = new FolderViewModel();

        vm.Open(_tmp.Root + Path.DirectorySeparatorChar);

        Assert.Equal(Path.GetFullPath(_tmp.Root + Path.DirectorySeparatorChar), vm.RootFolder);
        Assert.Null(vm.FolderTree);
        Assert.Equal(TreeViewMode.Files, vm.ViewMode);
        Assert.Empty(vm.Items);
    }

    [Fact]
    public void Scan_binds_the_folder_nodes()
    {
        _tmp.File("b.md");
        _tmp.File("docs/a.md");

        var vm = OpenAndScan(_tmp.Root);

        Assert.Equal(new[] { "docs", "b.md" }, Names(vm.Items));
        Assert.Null(vm.Reversa);
        Assert.True(vm.IsInTree(_tmp.PathOf("docs/a.md")));
    }

    [Fact]
    public void Stale_scan_of_a_previous_folder_is_ignored()
    {
        var first = _tmp.Dir("first");
        _tmp.File("first/a.md");
        var second = _tmp.Dir("second");
        var vm = new FolderViewModel();

        vm.Open(first);
        var (tree, reversa) = FolderViewModel.Scan(first);
        vm.Open(second);

        Assert.False(vm.ApplyScan(first, tree, reversa));
        Assert.Null(vm.FolderTree);
        Assert.Empty(vm.Items);
    }

    [Fact]
    public void Refresh_keeps_folders_expanded_as_the_user_left_them()
    {
        _tmp.File("open/a.md");
        _tmp.File("closed/b.md");
        var vm = OpenAndScan(_tmp.Root);
        vm.FolderTree!.Find(_tmp.PathOf("open"))!.IsExpanded = true;

        Rescan(vm);

        Assert.True(vm.FolderTree!.Find(_tmp.PathOf("open"))!.IsExpanded);
        Assert.False(vm.FolderTree!.Find(_tmp.PathOf("closed"))!.IsExpanded);
    }

    [Theory]
    [InlineData("C:\\", "C:\\")]
    [InlineData("C:\\work\\notes\\", "notes")]
    public void Display_name_is_the_folder_name_or_the_drive(string folder, string expected)
    {
        var vm = new FolderViewModel();
        vm.Open(folder);
        Assert.Equal(expected, vm.FolderDisplayName);
    }

    #endregion

    #region Open files

    [Fact]
    public void File_outside_the_tree_joins_the_open_files_section_once()
    {
        _tmp.File("a.md");
        var external = _outside.File("ext.md");
        var vm = OpenAndScan(_tmp.Root);

        vm.TrackOpenedFile(external);
        vm.TrackOpenedFile(external);

        Assert.Same(vm.OpenFilesSection, vm.Items[0]);
        Assert.Equal(new[] { "ext.md" }, Names(vm.OpenFilesSection.Children));
        Assert.True(vm.IsOpenFile(vm.OpenFilesSection.Children[0]));
    }

    [Fact]
    public void File_of_the_tree_does_not_join_the_section()
    {
        var inside = _tmp.File("a.md");
        var vm = OpenAndScan(_tmp.Root);

        Assert.Equal(inside, vm.TrackOpenedFile(inside));

        Assert.Empty(vm.OpenFilesSection.Children);
        Assert.DoesNotContain(vm.OpenFilesSection, vm.Items);
    }

    [Fact]
    public void Removing_the_last_open_file_hides_the_section()
    {
        _tmp.File("a.md");
        var external = _outside.File("ext.md");
        var vm = OpenAndScan(_tmp.Root);
        vm.TrackOpenedFile(external);
        vm.SyncSelection(external);

        vm.RemoveOpenFile(external);

        Assert.DoesNotContain(vm.OpenFilesSection, vm.Items);
        Assert.Null(vm.SelectedNode);
    }

    [Fact]
    public void Opening_a_folder_that_contains_an_open_file_moves_it_into_the_tree()
    {
        _tmp.File("a.md");
        var external = _outside.File("ext.md");
        var vm = OpenAndScan(_tmp.Root);
        vm.TrackOpenedFile(external);

        OpenAndScan(_outside.Root, vm);

        Assert.Empty(vm.OpenFilesSection.Children);
        Assert.Equal(new[] { "ext.md" }, Names(vm.Items));
    }

    [Fact]
    public void Invalid_path_is_not_an_open_file()
    {
        var vm = new FolderViewModel();
        Assert.Null(vm.FindOpenFile("bad\0path.md"));
    }

    #endregion

    #region Selection

    [Fact]
    public void Sync_selection_selects_the_node_and_expands_its_parents()
    {
        var deep = _tmp.File("x/y/deep.md");
        var vm = OpenAndScan(_tmp.Root);

        vm.SyncSelection(deep);

        var node = vm.SelectedNode!;
        Assert.Equal(deep, node.FullPath);
        Assert.True(node.IsSelected);
        Assert.True(node.Parent!.IsExpanded);
        Assert.True(node.Parent!.Parent!.IsExpanded);
    }

    [Fact]
    public void Sync_selection_to_an_unknown_file_clears_it()
    {
        var a = _tmp.File("a.md");
        var vm = OpenAndScan(_tmp.Root);
        vm.SyncSelection(a);
        var previous = vm.SelectedNode!;

        vm.SyncSelection(_outside.PathOf("nowhere.md"));

        Assert.Null(vm.SelectedNode);
        Assert.False(previous.IsSelected);
    }

    [Fact]
    public void Rescan_drops_the_selection_flag_of_surviving_section_nodes()
    {
        _tmp.File("a.md");
        var external = _outside.File("ext.md");
        var vm = OpenAndScan(_tmp.Root);
        vm.TrackOpenedFile(external);
        vm.SyncSelection(external);
        var node = vm.SelectedNode!;

        Rescan(vm);

        Assert.False(node.IsSelected);
        Assert.Null(vm.SelectedNode);
    }

    #endregion

    #region Views and Flow

    [Fact]
    public void Plain_folder_has_only_the_files_view()
    {
        _tmp.File("a.md");
        var vm = OpenAndScan(_tmp.Root);

        Assert.False(vm.SwitchView(TreeViewMode.Flow, null));
        Assert.False(vm.SwitchView(TreeViewMode.Files, null));
        Assert.Equal(TreeViewMode.Files, vm.ViewMode);
    }

    [Fact]
    public void Switching_to_flow_shows_its_tree_and_keeps_the_document_selected()
    {
        ReversaFolder();
        var vm = OpenAndScan(_tmp.Root);
        var doc = _tmp.PathOf("_reversa_sdd/architecture.md");

        Assert.True(vm.SwitchView(TreeViewMode.Flow, doc));

        Assert.Equal(TreeViewMode.Flow, vm.ViewMode);
        Assert.Equal(vm.Reversa!.Flow.Nodes, vm.Items);
        Assert.Equal(doc, vm.SelectedNode?.FullPath);
    }

    [Fact]
    public void Flow_is_active_only_in_the_flow_view_on_a_flow_document()
    {
        ReversaFolder();
        _tmp.File("notes.md");
        var vm = OpenAndScan(_tmp.Root);
        var doc = _tmp.PathOf("_reversa_sdd/architecture.md");

        Assert.False(vm.IsFlowActive(doc));
        vm.SwitchView(TreeViewMode.Flow, doc);
        Assert.True(vm.IsFlowActive(doc));
        Assert.False(vm.IsFlowActive(_tmp.PathOf("notes.md")));
        Assert.False(vm.IsFlowActive(null));
    }

    [Fact]
    public void Flow_footer_labels_come_from_the_flow_tree()
    {
        ReversaFolder();
        var vm = OpenAndScan(_tmp.Root);
        var docs = vm.Reversa!.FlowDocuments;
        Assert.Equal(2, docs.Count);

        var (prevLabel, nextLabel) = vm.BuildFlowFooter(docs[0]);

        Assert.Null(prevLabel);
        Assert.Equal(vm.FlowLabel(docs[1]), nextLabel);
        Assert.Equal("loose.md", vm.FlowLabel(_outside.PathOf("loose.md")));
    }

    [Fact]
    public void Losing_the_reversa_install_falls_back_to_the_files_view()
    {
        ReversaFolder();
        var vm = OpenAndScan(_tmp.Root);
        vm.SwitchView(TreeViewMode.Flow, null);

        Directory.Delete(_tmp.PathOf(".reversa"), recursive: true);
        Rescan(vm);

        Assert.Null(vm.Reversa);
        Assert.Equal(TreeViewMode.Files, vm.ViewMode);
        Assert.Equal(vm.FolderTree!.Nodes, vm.Items);
    }

    [Fact]
    public void Refresh_keeps_the_reversa_view()
    {
        ReversaFolder();
        var vm = OpenAndScan(_tmp.Root);
        vm.SwitchView(TreeViewMode.Skills, null);

        Rescan(vm);

        Assert.Equal(TreeViewMode.Skills, vm.ViewMode);
        Assert.Equal(vm.Reversa!.Skills.Nodes, vm.Items);
    }

    #endregion
}
