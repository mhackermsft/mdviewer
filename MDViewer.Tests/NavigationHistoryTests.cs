using MDViewer.ViewModels;

namespace MDViewer.Tests;

public class NavigationHistoryTests
{
    [Fact]
    public void Goes_back_in_reverse_order()
    {
        var history = new NavigationHistory();
        history.Push("a.md");
        history.Push("b.md");

        Assert.Equal("b.md", history.GoBack());
        Assert.Equal("a.md", history.GoBack());
        Assert.Null(history.GoBack());
        Assert.False(history.CanGoBack);
    }

    [Fact]
    public void Ignores_no_document_and_consecutive_duplicates()
    {
        var history = new NavigationHistory();
        history.Push(null);
        history.Push(@"C:\a.md");
        history.Push(@"c:\A.MD");

        Assert.Equal(1, history.Count);
    }

    [Fact]
    public void Keeps_non_consecutive_repeats()
    {
        var history = new NavigationHistory();
        history.Push("a.md");
        history.Push("b.md");
        history.Push("a.md");

        Assert.Equal(3, history.Count);
    }

    [Fact]
    public void Clear_empties_the_history()
    {
        var history = new NavigationHistory();
        history.Push("a.md");
        history.Clear();

        Assert.False(history.CanGoBack);
    }
}
