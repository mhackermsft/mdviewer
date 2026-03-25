using System.IO;
using Markdig;

namespace MDViewer.Services;

public class MarkdownRenderService
{
    private readonly MarkdownPipeline _pipeline;
    private readonly string _htmlTemplate;

    public MarkdownRenderService()
    {
        _pipeline = new MarkdownPipelineBuilder()
            .UseAdvancedExtensions()
            .Build();

        _htmlTemplate = LoadEmbeddedResource("MDViewer.Assets.template.html");
    }

    public string RenderToHtml(string markdown, bool isDarkTheme)
    {
        var htmlContent = Markdown.ToHtml(markdown, _pipeline);
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

    private static string LoadEmbeddedResource(string resourceName)
    {
        var assembly = typeof(MarkdownRenderService).Assembly;
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{resourceName}' not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
