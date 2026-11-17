namespace MDViewer.ViewModels;

/// <summary>
/// Documents visited before the current one, for "Back" (Alt+Left). Session
/// only; cleared when another root folder is opened.
/// </summary>
public sealed class NavigationHistory
{
    private readonly Stack<string> _entries = new();

    public bool CanGoBack => _entries.Count > 0;

    public int Count => _entries.Count;

    /// <summary>
    /// Records <paramref name="current"/> before navigating away from it.
    /// Nothing to record (no document) or the same document twice in a row is ignored.
    /// </summary>
    public void Push(string? current)
    {
        if (current == null) return;
        if (_entries.TryPeek(out var top) && string.Equals(top, current, StringComparison.OrdinalIgnoreCase))
            return;
        _entries.Push(current);
    }

    /// <summary>The previous document, or null when there is none.</summary>
    public string? GoBack() => _entries.TryPop(out var path) ? path : null;

    public void Clear() => _entries.Clear();
}
