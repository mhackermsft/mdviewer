using System.IO;

namespace MDViewer.Tests;

/// <summary>A scratch folder under %TEMP%, deleted when the test ends.</summary>
public sealed class TempFolder : IDisposable
{
    public TempFolder()
    {
        Root = Path.Combine(Path.GetTempPath(), "MDViewer.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string PathOf(string relative) =>
        Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>Creates a file (and its folders) with <paramref name="content"/>; returns its full path.</summary>
    public string File(string relative, string content = "# doc\n")
    {
        var path = PathOf(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, content);
        return path;
    }

    public string Dir(string relative)
    {
        var path = PathOf(relative);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Every file and folder below the root with its size and write time, for "nothing changed" checks.</summary>
    public IReadOnlyList<string> Snapshot() =>
        Directory.EnumerateFileSystemEntries(Root, "*", SearchOption.AllDirectories)
            .Select(p => new FileInfo(p))
            .Select(f => $"{f.FullName}|{(f.Exists ? f.Length : -1)}|{f.LastWriteTimeUtc.Ticks}")
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); }
        catch (IOException) { /* best effort */ }
        catch (UnauthorizedAccessException) { /* best effort */ }
    }
}
