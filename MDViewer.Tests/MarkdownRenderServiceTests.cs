using System.Text.RegularExpressions;
using MDViewer.Services;

namespace MDViewer.Tests;

public class MarkdownRenderServiceTests
{
    private readonly MarkdownRenderService _service = new();

    private static string Nonce(string html) =>
        Regex.Match(html, "'nonce-([^']+)'").Groups[1].Value;

    [Fact]
    public void Renders_markdown_inside_the_template()
    {
        var html = _service.RenderToHtml("# Title\n\nText", isDarkTheme: false);

        Assert.Contains("<h1 id=\"title\">Title</h1>", html);
        Assert.DoesNotContain("{{", html.Replace("{{CONTENT}}", "")); // no placeholder left
    }

    [Fact]
    public void Every_render_gets_a_fresh_nonce_used_by_the_app_scripts()
    {
        var first = _service.RenderToHtml("x", false);
        var second = _service.RenderToHtml("x", false);

        var nonce = Nonce(first);
        Assert.False(string.IsNullOrEmpty(nonce));
        Assert.NotEqual(nonce, Nonce(second));
        Assert.Equal(3, Regex.Matches(first, $"<script nonce=\"{Regex.Escape(nonce)}\"").Count);
    }

    [Fact]
    public void Document_scripts_do_not_receive_the_nonce()
    {
        var html = _service.RenderToHtml("<script>alert(1)</script>", false);

        Assert.Contains("<script>alert(1)</script>", html); // present but blocked by the CSP
        Assert.Contains("Content-Security-Policy", html);
    }

    [Theory]
    [InlineData("{{NONCE}}")]
    [InlineData("{{BASE_HREF}}")]
    [InlineData("{{MERMAID_THEME}}")]
    [InlineData("{{FLOW_FOOTER}}")]
    public void Placeholder_text_in_a_document_stays_literal(string placeholder)
    {
        var html = _service.RenderToHtml($"before {placeholder} after", false, "https://mddoc.local/", ("p", "n"));

        Assert.Contains($"before {placeholder} after", html);
    }

    [Fact]
    public void Dark_theme_marks_the_html_element_only()
    {
        var html = _service.RenderToHtml("<html lang=\"en\">", isDarkTheme: true);

        Assert.StartsWith("<!DOCTYPE html>\n<html lang=\"en\" data-theme=\"dark\">", html.ReplaceLineEndings("\n"));
        Assert.Single(Regex.Matches(html, "data-theme=\"dark\">"));
        Assert.Contains("theme: 'dark'", html);
    }

    [Fact]
    public void Flow_footer_labels_are_html_encoded()
    {
        var html = _service.RenderToHtml("x", false, flowFooter: ("<b>prev</b>", "a&b"));

        Assert.Contains("← Previous: &lt;b&gt;prev&lt;/b&gt;", html);
        Assert.Contains("Next: a&amp;b →", html);
        Assert.Contains("data-flow=\"prev\"", html);
    }

    [Fact]
    public void Footer_is_omitted_without_neighbors()
    {
        Assert.DoesNotContain("<nav class=\"flow-footer\"", _service.RenderToHtml("x", false));
        Assert.DoesNotContain("<nav class=\"flow-footer\"", _service.RenderToHtml("x", false, flowFooter: (null, null)));

        var onlyNext = _service.RenderToHtml("x", false, flowFooter: (null, "n"));
        Assert.DoesNotContain("data-flow=\"prev\"", onlyNext);
        Assert.Contains("data-flow=\"next\"", onlyNext);
    }

    [Fact]
    public void Base_href_is_encoded()
    {
        var html = _service.RenderToHtml("x", false, "https://mddoc.local/\"x");

        Assert.Contains("<base href=\"https://mddoc.local/&quot;x\">", html);
    }
}
