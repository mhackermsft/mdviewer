using System.IO;
using System.Text.RegularExpressions;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace MDViewer.Services;

public record LinkedDocument(string FilePath, string MarkdownContent, string Id);

public class LinkedDocumentCollector
{
    private readonly MarkdownPipeline _pipeline;
    private int _slugCounter;

    public LinkedDocumentCollector()
    {
        _pipeline = new MarkdownPipelineBuilder()
            .UseAdvancedExtensions()
            .Build();
    }

    /// <summary>
    /// Collects the root document and all recursively linked markdown documents
    /// in depth-first order. The root document is always first.
    /// </summary>
    public IReadOnlyList<LinkedDocument> CollectLinkedDocuments(string rootFilePath)
    {
        _slugCounter = 0;
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<LinkedDocument>();
        CollectRecursive(Path.GetFullPath(rootFilePath), visited, result);
        return result;
    }

    private void CollectRecursive(string filePath, HashSet<string> visited, List<LinkedDocument> result)
    {
        var normalizedPath = Path.GetFullPath(filePath);
        if (!visited.Add(normalizedPath))
            return;

        if (!File.Exists(normalizedPath))
            return;

        var markdown = File.ReadAllText(normalizedPath);
        var slug = GenerateSlug(normalizedPath);
        result.Add(new LinkedDocument(normalizedPath, markdown, slug));

        var document = Markdown.Parse(markdown, _pipeline);
        var currentDir = Path.GetDirectoryName(normalizedPath) ?? "";

        foreach (var link in document.Descendants<LinkInline>())
        {
            var url = link.Url;
            if (string.IsNullOrEmpty(url))
                continue;

            if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                || url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
                continue;

            // Strip fragment
            var hashIndex = url.IndexOf('#');
            if (hashIndex >= 0)
                url = url[..hashIndex];

            if (string.IsNullOrEmpty(url))
                continue;

            try
            {
                var targetPath = Path.GetFullPath(Path.Combine(currentDir, url));
                var ext = Path.GetExtension(targetPath).ToLowerInvariant();

                if (ext is ".md" or ".markdown")
                {
                    CollectRecursive(targetPath, visited, result);
                }
            }
            catch
            {
                // Invalid path — skip
            }
        }
    }

    private string GenerateSlug(string filePath)
    {
        var fileName = Path.GetFileNameWithoutExtension(filePath);
        var slug = Regex.Replace(fileName.ToLowerInvariant(), @"[^a-z0-9\-]", "-");
        return $"doc-{slug}-{_slugCounter++}";
    }
}
