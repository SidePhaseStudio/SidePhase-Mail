using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SidePhase.Mail.App.Helpers;
using SidePhase.Mail.App.Services;
using SidePhase.Mail.Core.Abstractions;
using SidePhase.Mail.Core.Formatting;
using SidePhase.Mail.Core.Services;

namespace SidePhase.Mail.App.ViewModels;

public sealed partial class AttachmentHitViewModel(AttachmentHit hit, DateTimeOffset now, Func<Guid, Task> open)
{
    public AttachmentHit Hit { get; } = hit;
    public string FileName => Hit.Attachment.FileName;
    public string SizeText => FileSizeFormatter.Format(Hit.Attachment.SizeBytes);
    public string TypeText => AttachmentTypes.GroupOf(Hit.Attachment) switch
    {
        "other" => Hit.Attachment.Extension.Length > 0 ? Hit.Attachment.Extension : "File",
        var group => char.ToUpperInvariant(group[0]) + group[1..],
    };

    public string Glyph => AttachmentSafety.IsDangerous(FileName) ? Glyphs.Shield : Glyphs.ForAttachment(Hit.Attachment);
    public string Sender => Hit.Message.From.DisplayName;
    public string Subject => Hit.Message.DisplaySubject;
    public string DateText { get; } = DateDisplayFormatter.ForList(hit.Message.Date, now);
    public string Details => $"{TypeText} · {SizeText} · {Sender}";

    [RelayCommand]
    private Task OpenAsync() => open(Hit.Message.Id);
}

/// <summary>
/// Finds attachments in mail stored on this PC by name, type, size, sender and date, using only their recorded
/// details. Nothing is downloaded or opened; "Open email" shows the message, where the file can be saved.
/// </summary>
public sealed partial class AttachmentsViewModel : ObservableObject
{
    private static readonly long[] Sizes = [0, 1L << 20, 5L << 20, 10L << 20, 25L << 20];
    private static readonly int[] Periods = [0, 7, 30, 90, 365];
    private readonly IMailDataSource _mail;
    private readonly IClock _clock;
    private readonly IUiDispatcher _ui;
    private int _version;

    public AttachmentsViewModel(IMailDataSource mail, IClock clock, IUiDispatcher ui)
    {
        _mail = mail;
        _clock = clock;
        _ui = ui;
        FileName = Sender = StatusText = string.Empty;
        _mail.DataChanged += (_, _) => _ui.Post(() => _ = RefreshAsync());
    }

    public ObservableCollection<AttachmentHitViewModel> Results { get; } = [];

    public IReadOnlyList<string> TypeOptions { get; } = ["Any type", .. AttachmentTypes.Groups.Select(g => char.ToUpperInvariant(g[0]) + g[1..])];

    [ObservableProperty] public partial string FileName { get; set; }
    [ObservableProperty] public partial string Sender { get; set; }
    [ObservableProperty] public partial int TypeIndex { get; set; }
    [ObservableProperty] public partial int SizeIndex { get; set; }
    [ObservableProperty] public partial int PeriodIndex { get; set; }
    [ObservableProperty] public partial string StatusText { get; set; }
    [ObservableProperty] public partial bool IsEmpty { get; set; }

    /// <summary>Asks the window to show a message.</summary>
    public Func<Guid, Task>? OpenMessage { get; set; }

    partial void OnFileNameChanged(string value) => _ = RefreshAsync();
    partial void OnSenderChanged(string value) => _ = RefreshAsync();
    partial void OnTypeIndexChanged(int value) => _ = RefreshAsync();
    partial void OnSizeIndexChanged(int value) => _ = RefreshAsync();
    partial void OnPeriodIndexChanged(int value) => _ = RefreshAsync();

    public async Task RefreshAsync()
    {
        var version = ++_version;
        var now = _clock.Now;
        var query = new AttachmentQuery
        {
            FileName = FileName,
            Sender = Sender,
            Type = TypeIndex > 0 && TypeIndex <= AttachmentTypes.Groups.Length ? AttachmentTypes.Groups[TypeIndex - 1] : string.Empty,
            MinimumSize = SizeIndex >= 0 && SizeIndex < Sizes.Length ? Sizes[SizeIndex] : 0,
            After = PeriodIndex > 0 && PeriodIndex < Periods.Length ? now.AddDays(-Periods[PeriodIndex]) : null,
        };

        var messages = await _mail.GetMessagesAsync();
        var hits = await Task.Run(() => AttachmentFinder.Find(messages, query));
        if (version != _version) return;

        Results.Clear();
        foreach (var hit in hits) Results.Add(new AttachmentHitViewModel(hit, now, id => OpenMessage?.Invoke(id) ?? Task.CompletedTask));
        IsEmpty = Results.Count == 0;
        var withoutDetails = messages.Count(m => m.HasAttachments && m.Attachments.Count == 0);
        StatusText = (hits.Count == 1 ? "1 attachment" : $"{hits.Count} attachments") + " in mail stored on this PC"
            + (withoutDetails > 0 ? $" · details for {withoutDetails} older messages are still being fetched" : string.Empty);
    }
}
