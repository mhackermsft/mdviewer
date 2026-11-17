using MDViewer.Services;

namespace MDViewer.Tests;

public sealed class FolderTreeServiceTests : IDisposable
{
    private readonly TempFolder _tmp = new();

    public void Dispose() => _tmp.Dispose();

    private static List<string> Names(IEnumerable<MDViewer.Models.FileTreeNode> nodes) =>
        nodes.Select(n => n.Name).ToList();

    [Fact]
    public void Lists_folders_before_files_in_natural_order()
    {
        _tmp.File("file10.md");
        _tmp.File("file2.md");
        _tmp.File("zdocs/a.md");
        _tmp.File("adocs/a.md");

        var tree = FolderTreeService.Build(_tmp.Root);

        Assert.Equal(new[] { "adocs", "zdocs", "file2.md", "file10.md" }, Names(tree.Nodes));
    }

    [Fact]
    public void Skips_non_markdown_files_and_prunes_folders_without_markdown()
    {
        _tmp.File("readme.markdown");
        _tmp.File("notes.txt", "x");
        _tmp.File("empty/image.png", "x");
        _tmp.File("deep/a/b/c.md");

        var tree = FolderTreeService.Build(_tmp.Root);

        Assert.Equal(new[] { "deep", "readme.markdown" }, Names(tree.Nodes));
        Assert.NotNull(tree.Find(_tmp.PathOf("deep/a/b/c.md")));
    }

    [Theory]
    [InlineData("node_modules")]
    [InlineData("bin")]
    [InlineData("obj")]
    [InlineData(".git")]
    [InlineData("__pycache__")]
    public void Skips_dependency_and_build_folders(string excluded)
    {
        _tmp.File($"{excluded}/pkg/readme.md");
        _tmp.File("doc.md");

        var tree = FolderTreeService.Build(_tmp.Root);

        Assert.Equal(new[] { "doc.md" }, Names(tree.Nodes));
    }

    [Fact]
    public void Keeps_generic_folder_names_that_may_hold_documentation()
    {
        _tmp.File("build/guide.md");

        Assert.Equal(new[] { "build" }, Names(FolderTreeService.Build(_tmp.Root).Nodes));
    }

    [Fact]
    public void Find_is_case_insensitive_and_first_file_is_depth_first()
    {
        _tmp.File("b.md");
        var nested = _tmp.File("a/first.md");

        var tree = FolderTreeService.Build(_tmp.Root);

        Assert.Same(tree.Find(nested), tree.Find(nested.ToUpperInvariant()));
        Assert.Equal(nested, tree.FirstFile()!.FullPath);
        Assert.Null(tree.Find(_tmp.PathOf("missing.md")));
        Assert.Null(tree.Find("reversa:flow/x")); // synthetic keys are never paths
    }

    [Fact]
    public void Empty_folder_gives_an_empty_tree()
    {
        var tree = FolderTreeService.Build(_tmp.Root);

        Assert.Empty(tree.Nodes);
        Assert.Null(tree.FirstFile());
    }

    [Theory]
    [InlineData("a.md", true)]
    [InlineData("A.MARKDOWN", true)]
    [InlineData("a.mdx", false)]
    [InlineData("md", false)]
    public void Recognizes_markdown_extensions(string name, bool expected)
    {
        Assert.Equal(expected, FolderTreeService.IsMarkdownFile(name));
    }
}
