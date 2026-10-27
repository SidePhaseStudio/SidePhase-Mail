namespace SidePhase.Mail.Core.Models;

public sealed record MessageBody
{
    public required Guid MessageId { get; init; }
    public string? HtmlBody { get; init; }
    public string? TextBody { get; init; }
    public IReadOnlyList<AttachmentInfo> Attachments { get; init; } = [];
    public IReadOnlyList<EmailAddress> Bcc { get; init; } = [];
}
