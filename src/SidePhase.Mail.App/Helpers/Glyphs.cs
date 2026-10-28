using SidePhase.Mail.Core.Models;

namespace SidePhase.Mail.App.Helpers;

/// <summary>Segoe Fluent Icons code points used across the app, in one place.</summary>
public static class Glyphs
{
    public const string Menu = "";
    public const string Mail = "";
    public const string MailRead = "";
    public const string People = "";
    public const string Contact = "";
    public const string Pin = "";
    public const string Unpin = "";
    public const string Pinned = "";
    public const string Star = "";
    public const string StarFilled = "";
    public const string Edit = "";
    public const string Send = "";
    public const string Archive = "";
    public const string Junk = "";
    public const string Delete = "";
    public const string Folder = "";
    public const string Reply = "";
    public const string ReplyAll = "";
    public const string Forward = "";
    public const string Attach = "";
    public const string Settings = "";
    public const string AddAccount = "";
    public const string Block = "";
    public const string Shield = "";
    public const string Picture = "";
    public const string Document = "";
    public const string Page = "";
    public const string Accept = "";
    public const string Info = "";
    public const string Sync = "";
    public const string Snooze = "";
    public const string Flag = "";
    public const string Scheduled = "";
    public const string AddSmartFolder = "";

    public static string ForFolder(FolderKind kind) => kind switch
    {
        FolderKind.Inbox => Mail,
        FolderKind.Drafts => Edit,
        FolderKind.Sent => Send,
        FolderKind.Archive => Archive,
        FolderKind.Junk => Junk,
        FolderKind.Trash => Delete,
        _ => Folder,
    };

    public static string ForAttachment(AttachmentInfo attachment) =>
        attachment.IsImage ? Picture
        : attachment.Extension is "PDF" or "DOC" or "DOCX" or "TXT" or "RTF" ? Document
        : Page;
}
