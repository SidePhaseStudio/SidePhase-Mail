namespace SidePhase.Mail.Core.Models;

public enum ComposeMode
{
    New,
    Reply,
    ReplyAll,
    Forward,
    EditDraft,
}

public sealed record DraftAttachment(string FileName, long SizeBytes, string? SourcePath, string ContentType = "application/octet-stream");

/// <summary>
/// A message being composed. Also used to persist drafts.
/// </summary>
public sealed record DraftMessage
{
    /// <summary>Id of the stored draft, if it has been saved before.</summary>
    public Guid? DraftId { get; init; }
    public Guid AccountId { get; init; }
    public ComposeMode Mode { get; init; } = ComposeMode.New;

    /// <summary>The message being replied to or forwarded.</summary>
    public Guid? SourceMessageId { get; init; }

    public IReadOnlyList<EmailAddress> To { get; init; } = [];
    public IReadOnlyList<EmailAddress> Cc { get; init; } = [];
    public IReadOnlyList<EmailAddress> Bcc { get; init; } = [];
    public string Subject { get; init; } = string.Empty;

    public bool IsPlainText { get; init; }
    public string? HtmlBody { get; init; }
    public string TextBody { get; init; } = string.Empty;

    /// <summary>Quoted original content for replies and forwards (HTML).</summary>
    public string? QuotedHtml { get; init; }

    /// <summary>Quoted original content for replies and forwards (plain text).</summary>
    public string? QuotedText { get; init; }

    public IReadOnlyList<DraftAttachment> Attachments { get; init; } = [];

    public bool HasAnyRecipient => To.Count + Cc.Count + Bcc.Count > 0;
}
