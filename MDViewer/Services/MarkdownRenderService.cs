using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Markdig;

namespace MDViewer.Services;

public class MarkdownRenderService
{
    private readonly MarkdownPipeline _pipeline;
    private readonly string _htmlTemplate;
    private readonly string _mermaidJs;

    public MarkdownRenderService()
    {
        _pipeline = new MarkdownPipelineBuilder()
            .UseAdvancedExtensions()
            .Build();

        _htmlTemplate = LoadEmbeddedResource("MDViewer.Assets.template.html");
        _mermaidJs = LoadEmbeddedResource("MDViewer.Assets.mermaid.min.js");
    }

    public string RenderToHtml(string markdown, bool isDarkTheme)
    {
        var htmlContent = Markdown.ToHtml(markdown, _pipeline);
        return ApplyTemplate(htmlContent, isDarkTheme);
    }

    /// <summary>
    /// Renders multiple linked documents into a single HTML page.
    /// Inter-document links are rewritten to internal anchors.
    /// </summary>
    public string RenderMergedToHtml(
        IReadOnlyList<LinkedDocument> documents, bool isDarkTheme, bool addPageBreaks = true)
    {
        var pathToSlug = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var doc in documents)
            pathToSlug[doc.FilePath] = doc.Id;

        var combined = new StringBuilder();
        for (var i = 0; i < documents.Count; i++)
        {
            var doc = documents[i];
            var html = Markdown.ToHtml(doc.MarkdownContent, _pipeline);
            html = RewriteInterDocumentLinks(html, doc.FilePath, pathToSlug);

            var extraClass = i > 0
                ? (addPageBreaks ? " document-page-break" : " document-separator")
                : "";
            combined.Append($"<div class=\"document-section{extraClass}\" id=\"{doc.Id}\">");
            combined.Append(html);
            combined.Append("</div>");
        }

        return ApplyTemplate(combined.ToString(), isDarkTheme);
    }

    /// <summary>
    /// Converts WebView2-targeted HTML into a self-contained standalone HTML file
    /// that works when opened directly in any browser.
    /// </summary>
    public string ConvertToStandaloneHtml(string html)
    {
        // Inline the mermaid.js library so the file works offline
        html = html.Replace(
            "<script src=\"https://assets.local/mermaid.min.js\"></script>",
            $"<script>{_mermaidJs}</script>");

        // Remove WebView2 host signaling (not available in a regular browser)
        html = html.Replace(
            "window.chrome.webview.postMessage('render-complete');",
            "");

        // Let browsers handle link navigation natively instead of delegating to host
        html = html.Replace(
            @"// All other links: delegate to the host application
            e.preventDefault();
            window.chrome.webview.postMessage(JSON.stringify({ type: 'navigate', href: href }));",
            "// Links navigate normally in standalone HTML");

        return html;
    }

    private string ApplyTemplate(string htmlContent, bool isDarkTheme)
    {
        var mermaidTheme = isDarkTheme ? "dark" : "default";

        var html = _htmlTemplate
            .Replace("{{CONTENT}}", htmlContent)
            .Replace("{{MERMAID_THEME}}", mermaidTheme);

        if (isDarkTheme)
        {
            html = html.Replace("<html lang=\"en\">", "<html lang=\"en\" data-theme=\"dark\">");
        }

        return html;
    }

    private static string RewriteInterDocumentLinks(
        string html, string documentPath, Dictionary<string, string> pathToSlug)
    {
        var docDir = Path.GetDirectoryName(documentPath) ?? "";

        return Regex.Replace(html, @"href=""([^""]+)""", match =>
        {
            var fullHref = match.Groups[1].Value;

            if (fullHref.StartsWith('#')
                || fullHref.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || fullHref.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                || fullHref.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
                return match.Value;

            var href = fullHref;
            var hashIndex = fullHref.IndexOf('#');
            if (hashIndex >= 0)
                href = fullHref[..hashIndex];

            if (string.IsNullOrEmpty(href))
                return match.Value;

            try
            {
                var resolvedPath = Path.GetFullPath(Path.Combine(docDir, href));
                if (pathToSlug.TryGetValue(resolvedPath, out var slug))
                    return $"href=\"#{slug}\"";
            }
            catch { }

            return match.Value;
        });
    }

    private static string LoadEmbeddedResource(string resourceName)
    {
        var assembly = typeof(MarkdownRenderService).Assembly;
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{resourceName}' not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
