namespace MDViewer.Services;

/// <summary>
/// Previous/next over the Reversa Flow documents (display order). Pure logic,
/// no UI: paths are compared case-insensitively, like Windows paths.
/// </summary>
public static class FlowNavigator
{
    /// <summary>Position of <paramref name="path"/> in <paramref name="documents"/>; -1 when absent.</summary>
    public static int IndexOf(IReadOnlyList<string>? documents, string? path)
    {
        if (documents == null || path == null) return -1;
        for (var i = 0; i < documents.Count; i++)
        {
            if (string.Equals(documents[i], path, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return -1;
    }

    /// <summary>Neighbors of <paramref name="path"/>; nulls at the ends or when it is not in the Flow.</summary>
    public static (string? Prev, string? Next) Neighbors(IReadOnlyList<string>? documents, string? path)
    {
        var i = IndexOf(documents, path);
        if (i < 0) return (null, null);
        return (i > 0 ? documents![i - 1] : null, i < documents!.Count - 1 ? documents[i + 1] : null);
    }
}
