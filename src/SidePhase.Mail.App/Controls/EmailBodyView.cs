using System.Text;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using SidePhase.Mail.App.Models;
using SidePhase.Mail.App.Services;
using SidePhase.Mail.Core.Rendering;

namespace SidePhase.Mail.App.Controls;

/// <summary>
/// Renders an email body in a locked-down WebView2.
/// </summary>
/// <remarks>
/// Security model (each layer is independent):
/// <list type="bullet">
/// <item>Script, web messages, host objects, dev tools, autofill, downloads and permission prompts are disabled.</item>
/// <item>The document is served from a private virtual origin with a strict Content-Security-Policy header
/// (plus a matching meta tag) that forbids scripts, frames, forms and — unless allowed — all remote resources.</item>
/// <item>Every network request is intercepted; only data: URIs and, when the user allows it, remote images load.</item>
/// <item>No navigation is allowed inside the view. Links open in the default browser; mailto: opens a compose window.</item>
/// </list>
/// </remarks>
public sealed partial class EmailBodyView : UserControl
{
    private const string DocumentHost = "message.sidephase.invalid";
    private const string DocumentUrlPrefix = "https://" + DocumentHost + "/";

    public static readonly DependencyProperty ContentSourceProperty =
        DependencyProperty.Register(nameof(ContentSource), typeof(EmailContent), typeof(EmailBodyView),
            new PropertyMetadata(null, (d, _) => ((EmailBodyView)d).QueueRender()));

    private readonly WebView2 _webView = new();
    private readonly TextBlock _fallback = new()
    {
        TextWrapping = TextWrapping.Wrap,
        IsTextSelectionEnabled = true,
        Visibility = Visibility.Collapsed,
        FontSize = 14,
        LineHeight = 22,
    };

    private readonly ScrollViewer _fallbackScroller;
    private Task? _initialization;
    private bool _initializationFailed;
    private string _documentHtml = string.Empty;
    private string _documentUrl = DocumentUrlPrefix;
    private bool _allowRemote;

    public EmailBodyView()
    {
        _webView.DefaultBackgroundColor = Colors.Transparent;
        _webView.Visibility = Visibility.Collapsed;
        var grid = new Grid();
        grid.Children.Add(_webView);
        // The plain-text fallback's scroller is shown only with it: left visible over the web view, it took the mouse
        // wheel and the message couldn't be scrolled.
        _fallbackScroller = new ScrollViewer { Content = _fallback, Padding = new Thickness(2, 4, 12, 24), Visibility = Visibility.Collapsed };
        grid.Children.Add(_fallbackScroller);
        Content = grid;

        ActualThemeChanged += (_, _) => QueueRender();
    }

    public EmailContent? ContentSource
    {
        get => (EmailContent?)GetValue(ContentSourceProperty);
        set => SetValue(ContentSourceProperty, value);
    }

    /// <summary>Raised when the user activates a mailto: link.</summary>
    public event EventHandler<string>? MailtoRequested;

    /// <summary>Raised when the user activates a web link. Nothing is opened until the host confirms it.</summary>
    public event EventHandler<Uri>? LinkRequested;

    private async void QueueRender()
    {
        try
        {
            await RenderAsync();
        }
        catch (Exception ex)
        {
            ShowFallback("This message couldn't be displayed: " + ex.Message);
        }
    }

    private async Task RenderAsync()
    {
        var content = ContentSource;
        if (content is null)
        {
            _webView.Visibility = Visibility.Collapsed;
            _fallback.Visibility = _fallbackScroller.Visibility = Visibility.Collapsed;
            return;
        }

        var themeService = App.GetService<ThemeService>();
        var theme = themeService.GetEmailTheme(ActualTheme);
        _allowRemote = content.AllowRemoteContent;
        _documentHtml = HtmlEmailDocumentBuilder.Build(new EmailRenderRequest
        {
            Html = content.Html,
            Text = content.Text,
            AllowRemoteContent = content.AllowRemoteContent,
            Theme = theme,
        });

        _initialization ??= InitializeAsync();
        await _initialization;

        if (_initializationFailed)
        {
            ShowFallback(content.Text ?? string.Empty);
            return;
        }

        // A unique URL per render so the WebView never serves a stale document.
        _documentUrl = $"{DocumentUrlPrefix}{content.MessageId:N}/{Guid.NewGuid():N}";
        _fallback.Visibility = _fallbackScroller.Visibility = Visibility.Collapsed;
        _webView.Visibility = Visibility.Visible;
        _webView.CoreWebView2.Navigate(_documentUrl);
    }

    private async Task InitializeAsync()
    {
        try
        {
            await _webView.EnsureCoreWebView2Async();
        }
        catch (Exception)
        {
            // The WebView2 runtime is missing or failed to start. Plain text is still readable.
            _initializationFailed = true;
            return;
        }

        var core = _webView.CoreWebView2;
        var settings = core.Settings;
        settings.IsScriptEnabled = false;
        settings.IsWebMessageEnabled = false;
        settings.AreDefaultScriptDialogsEnabled = false;
        settings.AreHostObjectsAllowed = false;
        settings.AreDevToolsEnabled = false;
        settings.IsStatusBarEnabled = false;
        settings.IsPasswordAutosaveEnabled = false;
        settings.IsGeneralAutofillEnabled = false;
        settings.AreBrowserAcceleratorKeysEnabled = false;
        settings.IsBuiltInErrorPageEnabled = false;
        settings.IsSwipeNavigationEnabled = false;
        settings.IsZoomControlEnabled = true;
        settings.AreDefaultContextMenusEnabled = true;

        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += OnWebResourceRequested;
        core.NavigationStarting += OnNavigationStarting;
        core.NewWindowRequested += OnNewWindowRequested;
        core.ContextMenuRequested += OnContextMenuRequested;
        core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
        core.DownloadStarting += (_, e) => e.Cancel = true;
    }

    private void OnWebResourceRequested(CoreWebView2 sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        var uri = e.Request.Uri;

        if (uri.StartsWith(DocumentUrlPrefix, StringComparison.OrdinalIgnoreCase))
        {
            if (!string.Equals(uri, _documentUrl, StringComparison.Ordinal))
            {
                e.Response = sender.Environment.CreateWebResourceResponse(null, 404, "Not Found", string.Empty);
                return;
            }

            var stream = new MemoryStream(Encoding.UTF8.GetBytes(_documentHtml)).AsRandomAccessStream();
            var csp = _allowRemote
                ? "default-src 'none'; img-src data: https: http:; style-src 'unsafe-inline'; font-src data:; form-action 'none'; base-uri 'none'; frame-ancestors 'none'"
                : "default-src 'none'; img-src data:; style-src 'unsafe-inline'; font-src data:; form-action 'none'; base-uri 'none'; frame-ancestors 'none'";
            e.Response = sender.Environment.CreateWebResourceResponse(
                stream,
                200,
                "OK",
                "Content-Type: text/html; charset=utf-8\r\n" +
                $"Content-Security-Policy: {csp}\r\n" +
                "X-DNS-Prefetch-Control: off\r\n" +
                "Referrer-Policy: no-referrer\r\n" +
                "X-Content-Type-Options: nosniff\r\n" +
                "Cache-Control: no-store");
            return;
        }

        if (uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // Remote images only when the user allowed them for this message, and only from public web addresses.
        var isRemoteImage = _allowRemote
            && e.ResourceContext == CoreWebView2WebResourceContext.Image
            && Core.Rendering.RemoteContentPolicy.IsAllowedImageUrl(uri);

        if (!isRemoteImage)
        {
            e.Response = sender.Environment.CreateWebResourceResponse(null, 403, "Blocked by SidePhase Mail", string.Empty);
            return;
        }

        // No cookies: image hosts can't recognise the reader across messages.
        e.Request.Headers.RemoveHeader("Cookie");
    }

    private void OnNavigationStarting(CoreWebView2 sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (string.Equals(e.Uri, _documentUrl, StringComparison.Ordinal))
        {
            return;
        }

        e.Cancel = true;
        if (e.IsUserInitiated) OpenExternally(e.Uri);
    }

    private void OnNewWindowRequested(CoreWebView2 sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        if (e.IsUserInitiated) OpenExternally(e.Uri);
    }

    private void OnContextMenuRequested(CoreWebView2 sender, CoreWebView2ContextMenuRequestedEventArgs e)
    {
        // Keep only harmless clipboard commands.
        string[] allowed = ["copy", "selectAll", "copyLinkLocation", "copyImage"];
        var items = e.MenuItems;
        for (var i = items.Count - 1; i >= 0; i--)
        {
            if (!allowed.Contains(items[i].Name)) items.RemoveAt(i);
        }

        if (items.Count == 0) e.Handled = true;
    }

    private void OpenExternally(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed)) return;

        if (parsed.Scheme == Uri.UriSchemeMailto)
        {
            MailtoRequested?.Invoke(this, Uri.UnescapeDataString(parsed.AbsoluteUri["mailto:".Length..]).Split('?')[0]);
            return;
        }

        if (parsed.Scheme == Uri.UriSchemeHttps || parsed.Scheme == Uri.UriSchemeHttp)
        {
            LinkRequested?.Invoke(this, parsed);
        }
    }

    private void ShowFallback(string text)
    {
        _webView.Visibility = Visibility.Collapsed;
        _fallback.Text = text;
        _fallback.Visibility = _fallbackScroller.Visibility = Visibility.Visible;
    }
}
