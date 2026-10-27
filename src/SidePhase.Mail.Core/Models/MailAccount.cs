namespace SidePhase.Mail.Core.Models;

public enum MailProviderKind
{
    /// <summary>Built-in demonstration data. Never connects to a server.</summary>
    Demo,
    Gmail,
    Microsoft,
    Imap,
}

public sealed record MailAccount
{
    public required Guid Id { get; init; }
    public required string DisplayName { get; init; }
    public required string EmailAddress { get; init; }
    public required MailProviderKind Provider { get; init; }

    /// <summary>Optional colour used to tell accounts apart in unified views (#RRGGBB).</summary>
    public string? ColorHex { get; init; }

    public bool IsDemo => Provider == MailProviderKind.Demo;

    public EmailAddress Address => new(DisplayName, EmailAddress);
}
