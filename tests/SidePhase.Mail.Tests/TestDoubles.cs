using SidePhase.Mail.Core.Abstractions;
using SidePhase.Mail.Core.Models;
using SidePhase.Mail.Core.Settings;

namespace SidePhase.Mail.Tests;

internal sealed class FixedClock(DateTimeOffset now) : IClock
{
    public DateTimeOffset Now { get; set; } = now;
}

internal sealed class InMemorySettingsStore : ISettingsStore
{
    public AppSettings? Stored { get; private set; }
    public int SaveCount { get; private set; }

    public Task<AppSettings?> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(Stored?.Clone());

    public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        Stored = settings.Clone();
        SaveCount++;
        return Task.CompletedTask;
    }
}

internal static class Samples
{
    public static readonly DateTimeOffset Now = new(2026, 10, 27, 14, 30, 0, TimeSpan.Zero); // a Tuesday

    public static MessageSummary Message(
        string from = "Jane Doe <jane@example.com>",
        string subject = "Hello",
        DateTimeOffset? date = null,
        FolderKind folder = FolderKind.Inbox,
        bool read = false,
        bool starred = false,
        bool pinned = false,
        bool attachments = false,
        Guid? accountId = null,
        Guid? folderId = null)
    {
        var parsed = Core.Composition.RecipientParser.Parse(from).Addresses.Single();
        return new MessageSummary
        {
            Id = Guid.NewGuid(),
            AccountId = accountId ?? Guid.Empty,
            FolderId = folderId ?? Guid.Empty,
            FolderKind = folder,
            From = parsed,
            Subject = subject,
            Preview = "Preview of " + subject,
            Date = date ?? Now,
            IsRead = read,
            IsStarred = starred,
            IsPinned = pinned,
            PinnedAt = pinned ? date ?? Now : null,
            HasAttachments = attachments,
        };
    }
}
