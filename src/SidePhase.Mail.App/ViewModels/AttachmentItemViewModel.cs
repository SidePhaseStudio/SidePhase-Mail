using CommunityToolkit.Mvvm.Input;
using SidePhase.Mail.App.Helpers;
using SidePhase.Mail.Core.Formatting;
using SidePhase.Mail.Core.Models;

namespace SidePhase.Mail.App.ViewModels;

public sealed partial class AttachmentItemViewModel(AttachmentInfo info, Action<AttachmentInfo> open)
{
    public AttachmentInfo Info { get; } = info;
    public string FileName => Info.FileName;
    public string SizeText => FileSizeFormatter.Format(Info.SizeBytes);
    public string Extension => Info.Extension.Length > 0 ? Info.Extension : "FILE";
    public string Glyph => Glyphs.ForAttachment(Info);
    public string Description => $"{Extension} · {SizeText}";

    [RelayCommand]
    private void Open() => open(Info);
}
