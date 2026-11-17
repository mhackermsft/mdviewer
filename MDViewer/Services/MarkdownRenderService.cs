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

    /// <param name="flowFooter">
    /// Labels of the previous/next documents of the Reversa Flow (null: no
    /// footer; a null label: no link on that side).
    /// </param>
    public string RenderToHtml(string markdown, bool isDarkTheme, string? baseHref = null,
        (string? PrevLabel, string? NextLabel)? flowFooter = null)
    {
        var htmlContent = Markdown.ToHtml(markdown, _pipeline);
        return ApplyTemplate(htmlContent, isDarkTheme, baseHref, flowFooter);
    }

    private string ApplyTemplate(string htmlContent, bool isDarkTheme, string? baseHref,
        (string? PrevLabel, string? NextLabel)? flowFooter)
    {
        var mermaidTheme = isDarkTheme ? "dark" : "default";

        var baseTag = string.IsNullOrEmpty(baseHref)
            ? ""
            : $"<base href=\"{System.Net.WebUtility.HtmlEncode(baseHref)}\">";

        // Fresh per render, so a document can never guess it and run a script.
        var nonce = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(18));

        // Every template placeholder is replaced before the content goes in, so
        // a document that contains placeholder text (e.g. "{{NONCE}}") never
        // receives the value.
        var template = isDarkTheme
            ? _htmlTemplate.Replace("<html lang=\"en\">", "<html lang=\"en\" data-theme=\"dark\">")
            : _htmlTemplate;

        return template
            .Replace("{{FLOW_FOOTER}}", BuildFlowFooter(flowFooter))
            .Replace("{{BASE_HREF}}", baseTag)
            .Replace("{{MERMAID_THEME}}", mermaidTheme)
            .Replace("{{NONCE}}", nonce)
            .Replace("{{CONTENT}}", htmlContent);
    }

    /// <summary>
    /// Previous/next links of the Reversa Flow. The links carry no path: the
    /// page's click handler sends {type:'flow', dir} to the host.
    /// </summary>
    private static string BuildFlowFooter((string? PrevLabel, string? NextLabel)? footer)
    {
        if (footer is not { } f || (f.PrevLabel == null && f.NextLabel == null))
            return "";

        var prev = f.PrevLabel == null
            ? ""
            : $"<a href=\"#\" class=\"flow-prev\" data-flow=\"prev\">← Previous: {System.Net.WebUtility.HtmlEncode(f.PrevLabel)}</a>";
        var next = f.NextLabel == null
            ? ""
            : $"<a href=\"#\" class=\"flow-next\" data-flow=\"next\">Next: {System.Net.WebUtility.HtmlEncode(f.NextLabel)} →</a>";
        return $"<nav class=\"flow-footer\" aria-label=\"Reversa flow\">{prev}{next}</nav>";
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
