using MDViewer.Services;

namespace MDViewer.Tests;

public class FlowNavigatorTests
{
    private static readonly string[] Docs = { @"C:\r\a.md", @"C:\r\b.md", @"C:\r\c.md" };

    [Fact]
    public void Middle_document_has_both_neighbors()
    {
        Assert.Equal((@"C:\r\a.md", @"C:\r\c.md"), FlowNavigator.Neighbors(Docs, @"C:\r\b.md"));
    }

    [Fact]
    public void Ends_have_a_single_neighbor()
    {
        Assert.Equal((null, @"C:\r\b.md"), FlowNavigator.Neighbors(Docs, @"C:\r\a.md"));
        Assert.Equal((@"C:\r\b.md", null), FlowNavigator.Neighbors(Docs, @"C:\r\c.md"));
    }

    [Fact]
    public void Paths_compare_case_insensitively()
    {
        Assert.Equal(1, FlowNavigator.IndexOf(Docs, @"c:\R\B.MD"));
    }

    [Theory]
    [InlineData(@"C:\r\other.md")]
    [InlineData(null)]
    public void Document_outside_the_flow_has_no_neighbors(string? path)
    {
        Assert.Equal(-1, FlowNavigator.IndexOf(Docs, path));
        Assert.Equal((null, null), FlowNavigator.Neighbors(Docs, path));
    }

    [Fact]
    public void Missing_flow_is_handled()
    {
        Assert.Equal((null, null), FlowNavigator.Neighbors(null, @"C:\r\a.md"));
    }
}
