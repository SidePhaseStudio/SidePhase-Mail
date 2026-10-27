namespace SidePhase.Mail.App.Models;

/// <summary>What the reading pane's WebView2 host should render.</summary>
public sealed record EmailContent(Guid MessageId, string? Html, string? Text, bool AllowRemoteContent);
