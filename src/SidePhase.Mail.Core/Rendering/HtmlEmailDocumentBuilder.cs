using System.Text;
using System.Text.RegularExpressions;

namespace SidePhase.Mail.Core.Rendering;

/// <summary>Colours and typography used to render a message so it matches the app theme.</summary>
public sealed record EmailRenderTheme(
    string Foreground,
    string SecondaryForeground,
    string Accent,
    string QuoteBorder,
    string ColorScheme,
    string FontFamily = "'Segoe UI Variable Text', 'Segoe UI', system-ui, sans-serif",
    double FontSizePx = 14.5)
{
    public static EmailRenderTheme Dark { get; } = new("#E9EBF2", "#9DA5B4", "#A99DFF", "rgba(255,255,255,0.14)", "dark");
    public static EmailRenderTheme Light { get; } = new("#1B1E26", "#5B6272", "#5A49E0", "rgba(0,0,0,0.14)", "light");
}

public sealed record EmailRenderRequest
{
    public string? Html { get; init; }
    public string? Text { get; init; }
    public bool AllowRemoteContent { get; init; }
    public required EmailRenderTheme Theme { get; init; }
}

/// <summary>
/// Builds the complete HTML document handed to WebView2.
/// </summary>
/// <remarks>
/// Defence in depth. The WebView2 host disables script, blocks network requests and cancels navigation; this builder
/// additionally removes active content and adds a Content-Security-Policy that forbids scripts, frames, forms and
/// (unless the user allows it) all remote resources such as tracking pixels.
/// </remarks>
public static partial class HtmlEmailDocumentBuilder
{
    public static string Build(EmailRenderRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var content = request.Html is { Length: > 0 } html
            ? Sanitize(ExtractBodyContent(html))
            : PlainTextHtml.Convert(request.Text);

        var t = request.Theme;
        var imgSrc = request.AllowRemoteContent ? "data: https: http:" : "data:";
        var csp = $"default-src 'none'; img-src {imgSrc}; style-src 'unsafe-inline'; font-src data:; " +
                  "script-src 'none'; object-src 'none'; frame-src 'none'; form-action 'none'; base-uri 'none'";

        var sb = new StringBuilder(content.Length + 2048);
        sb.Append("<!DOCTYPE html><html><head><meta charset=\"utf-8\">");
        sb.Append("<meta http-equiv=\"Content-Security-Policy\" content=\"").Append(csp).Append("\">");
        sb.Append("<meta name=\"referrer\" content=\"no-referrer\">");
        sb.Append("<meta name=\"color-scheme\" content=\"").Append(t.ColorScheme).Append("\">");
        sb.Append("<style>");
        sb.Append(":root{color-scheme:").Append(t.ColorScheme).Append(";}");
        sb.Append("html,body{background:transparent;margin:0;padding:0;}");
        sb.Append("body{color:").Append(t.Foreground).Append(";font-family:").Append(t.FontFamily)
          .Append(";font-size:").Append(t.FontSizePx.ToString(System.Globalization.CultureInfo.InvariantCulture))
          .Append("px;line-height:1.6;word-wrap:break-word;overflow-wrap:anywhere;padding:4px 2px 32px 2px;")
          .Append("-webkit-font-smoothing:antialiased;}");
        sb.Append("a{color:").Append(t.Accent).Append(";text-decoration:none;}a:hover{text-decoration:underline;}");
        sb.Append("img{max-width:100%;height:auto;}table{max-width:100%;}");
        sb.Append("blockquote{margin:12px 0;padding:2px 0 2px 14px;border-left:2px solid ").Append(t.QuoteBorder)
          .Append(";color:").Append(t.SecondaryForeground).Append(";}");
        sb.Append("hr{border:none;border-top:1px solid ").Append(t.QuoteBorder).Append(";margin:20px 0;}");
        sb.Append("pre,code{font-family:'Cascadia Mono',Consolas,monospace;font-size:0.92em;}");
        sb.Append(".sp-muted{color:").Append(t.SecondaryForeground).Append(";}");
        sb.Append("h1,h2,h3{line-height:1.3;font-weight:600;}");
        sb.Append("::selection{background:").Append(t.Accent).Append("55;}");
        sb.Append("</style></head><body>");
        sb.Append(content);
        sb.Append("</body></html>");
        return sb.ToString();
    }

    /// <summary>True if the HTML references images, stylesheets or other resources on the network.</summary>
    public static bool ContainsRemoteContent(string? html)
    {
        if (string.IsNullOrEmpty(html)) return false;
        return RemoteAttributeRegex().IsMatch(html) || RemoteCssRegex().IsMatch(html);
    }

    /// <summary>Returns the inner content of &lt;body&gt; if present, otherwise the input.</summary>
    public static string ExtractBodyContent(string html)
    {
        var match = BodyRegex().Match(html);
        if (!match.Success) return html;

        // Keep <style> blocks from the head so message formatting survives.
        var styles = string.Concat(HeadStyleRegex().Matches(html[..match.Index]).Select(m => m.Value));
        return styles + match.Groups["content"].Value;
    }

    /// <summary>Removes active and dangerous content. Not a substitute for the WebView2 restrictions.</summary>
    public static string Sanitize(string html)
    {
        var s = DangerousBlockRegex().Replace(html, string.Empty);
        s = DangerousTagRegex().Replace(s, string.Empty);
        s = EventHandlerRegex().Replace(s, string.Empty);
        s = ScriptUrlRegex().Replace(s, "${attr}=\"#\"");
        s = MetaRefreshRegex().Replace(s, string.Empty);
        return s;
    }

    [GeneratedRegex(@"<body\b[^>]*>(?<content>.*?)(</body\s*>|$)", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex BodyRegex();

    [GeneratedRegex(@"<style\b[^>]*>.*?</style\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex HeadStyleRegex();

    [GeneratedRegex(@"<(script|iframe|frame|frameset|object|embed|applet|form|noscript|template|svg|math)\b[^>]*>.*?</\1\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex DangerousBlockRegex();

    [GeneratedRegex(@"</?(script|iframe|frame|frameset|object|embed|applet|form|input|button|textarea|select|base|link|meta|noscript|template|svg|math)\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex DangerousTagRegex();

    [GeneratedRegex(@"\s+on[a-z]+\s*=\s*(""[^""]*""|'[^']*'|[^\s>]+)", RegexOptions.IgnoreCase)]
    private static partial Regex EventHandlerRegex();

    [GeneratedRegex(@"(?<attr>href|src|action|formaction|xlink:href|background)\s*=\s*(""\s*(javascript|vbscript|data:text/html)[^""]*""|'\s*(javascript|vbscript|data:text/html)[^']*'|(javascript|vbscript):[^\s>]*)", RegexOptions.IgnoreCase)]
    private static partial Regex ScriptUrlRegex();

    [GeneratedRegex(@"<meta\b[^>]*http-equiv\s*=\s*[""']?refresh[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex MetaRefreshRegex();

    [GeneratedRegex(@"\b(src|background|poster|srcset)\s*=\s*[""']?\s*(https?:)?//", RegexOptions.IgnoreCase)]
    private static partial Regex RemoteAttributeRegex();

    [GeneratedRegex(@"url\(\s*[""']?\s*(https?:)?//|@import", RegexOptions.IgnoreCase)]
    private static partial Regex RemoteCssRegex();
}
