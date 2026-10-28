using CommunityToolkit.Mvvm.Input;
using SidePhase.Mail.App.Helpers;
using SidePhase.Mail.App.Services;
using SidePhase.Mail.Core.Formatting;
using SidePhase.Mail.Core.Models;

namespace SidePhase.Mail.App.ViewModels;

/// <summary>An attachment in the reading pane. Clicking previews images and text; anything else is saved.</summary>
public sealed partial class AttachmentItemViewModel(AttachmentInfo info, Func<AttachmentInfo, Task> preview, Func<AttachmentInfo, Task> save, Func<Task> saveAll)
{
    public AttachmentInfo Info { get; } = info;
    public string FileName => Info.FileName;
    public string SizeText => FileSizeFormatter.Format(Info.SizeBytes);
    public string Extension => Info.Extension.Length > 0 ? Info.Extension : "FILE";
    public string Glyph => IsDangerous ? Glyphs.Shield : Glyphs.ForAttachment(Info);
    public bool IsDangerous => AttachmentSafety.IsDangerous(Info.FileName);
    public bool CanPreview => MessageCommandService.CanPreview(Info);
    public string Description => IsDangerous ? $"{Extension} · {SizeText} · can run code" : $"{Extension} · {SizeText}";
    public string Tooltip => IsDangerous
        ? $"{FileName} — this type of file can run programs on your PC. SidePhase Mail never opens it."
        : CanPreview ? $"{FileName} — click to preview" : $"{FileName} — click to save";

    [RelayCommand]
    private Task OpenAsync() => CanPreview ? preview(Info) : save(Info);

    [RelayCommand]
    private Task PreviewAsync() => preview(Info);

    [RelayCommand]
    private Task SaveAsync() => save(Info);

    [RelayCommand]
    private Task SaveAllAsync() => saveAll();
}
