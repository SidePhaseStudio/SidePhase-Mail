using SidePhase.Mail.Core.Rendering;

namespace SidePhase.Mail.Tests;

public class RenderingTests
{
    private static string Build(string? html = null, string? text = null, bool allowRemote = false) =>
        HtmlEmailDocumentBuilder.Build(new EmailRenderRequest
        {
            Html = html,
            Text = text,
            AllowRemoteContent = allowRemote,
            Theme = EmailRenderTheme.Dark,
        });

    [Fact]
    public void Strips_scripts_handlers_and_active_content()
    {
        var doc = Build("""
            <p onclick="steal()">Hi</p><script>alert('x')</script>
            <iframe src="https://evil.example"></iframe>
            <a href="javascript:alert(1)">x</a><form action="https://evil.example"><input name=p></form>
            <meta http-equiv="refresh" content="0;url=https://evil.example">
            <svg><script>alert(2)</script></svg>
            """);

        Assert.DoesNotContain("<script", doc, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onclick", doc, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<iframe", doc, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("javascript:", doc, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<form", doc, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("refresh", doc, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<p>Hi</p>", doc);
    }

    [Fact]
    public void Csp_blocks_remote_images_by_default()
    {
        var doc = Build("<img src=\"https://tracker.example/p.gif\">");
        Assert.Contains("img-src data:;", doc);
        Assert.Contains("script-src 'none'", doc);
        Assert.Contains("default-src 'none'", doc);
    }

    [Fact]
    public void Csp_allows_remote_images_when_requested()
    {
        var doc = Build("<img src=\"https://cdn.example/a.png\">", allowRemote: true);
        Assert.Contains("img-src data: https: http:;", doc);
        Assert.Contains("script-src 'none'", doc);
    }

    [Theory]
    [InlineData("<img src=\"https://x.example/a.png\">", true)]
    [InlineData("<img src='//x.example/a.png'>", true)]
    [InlineData("<td background=\"http://x.example/bg.jpg\">", true)]
    [InlineData("<div style=\"background:url('https://x.example/a.png')\">", true)]
    [InlineData("<img src=\"data:image/png;base64,AAAA\">", false)]
    [InlineData("<a href=\"https://x.example\">link</a>", false)]
    public void Detects_remote_content(string html, bool expected) =>
        Assert.Equal(expected, HtmlEmailDocumentBuilder.ContainsRemoteContent(html));

    [Fact]
    public void Extracts_body_and_keeps_head_styles()
    {
        var html = "<html><head><title>T</title><style>.x{color:red}</style></head><body class=a><p>Body</p></body></html>";
        var content = HtmlEmailDocumentBuilder.ExtractBodyContent(html);
        Assert.Equal("<style>.x{color:red}</style><p>Body</p>", content);
    }

    [Fact]
    public void Plain_text_is_escaped_and_linkified()
    {
        var html = PlainTextHtml.Convert("<b>not bold</b>\nSee https://example.com/page.");
        Assert.Contains("&lt;b&gt;not bold&lt;/b&gt;<br>", html);
        Assert.Contains("<a href=\"https://example.com/page\">https://example.com/page</a>.", html);
    }

    [Fact]
    public void Html_to_text_and_preview()
    {
        var text = HtmlText.ToPlainText("<style>p{}</style><p>Hello&nbsp;there</p><p>Second &amp; last</p>");
        Assert.Equal("Hello\u00a0there\nSecond & last", text);
        Assert.Equal("a b c", HtmlText.Preview("a\n\n b   c"));
        Assert.Equal(10, HtmlText.Preview(new string('x', 50), 10).Length);
    }
}
