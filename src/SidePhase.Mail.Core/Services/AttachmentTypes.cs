using SidePhase.Mail.Core.Models;

namespace SidePhase.Mail.Core.Services;

/// <summary>Friendly attachment type groups used by <c>filetype:</c> and the attachment finder.</summary>
public static class AttachmentTypes
{
    public static readonly string[] Groups = ["pdf", "image", "document", "spreadsheet", "presentation", "archive", "audio", "video", "calendar"];

    public static string GroupOf(AttachmentInfo a)
    {
        var ext = a.Extension.ToLowerInvariant();
        var type = a.ContentType.ToLowerInvariant();
        if (ext == "pdf" || type == "application/pdf") return "pdf";
        if (type.StartsWith("image/", StringComparison.Ordinal) || ext is "jpg" or "jpeg" or "png" or "gif" or "bmp" or "webp" or "heic" or "tif" or "tiff") return "image";
        if (ext is "doc" or "docx" or "odt" or "rtf" or "txt" or "md" or "pages") return "document";
        if (ext is "xls" or "xlsx" or "ods" or "csv" or "numbers") return "spreadsheet";
        if (ext is "ppt" or "pptx" or "odp" or "key") return "presentation";
        if (ext is "zip" or "7z" or "rar" or "gz" or "tar" or "tgz" or "bz2") return "archive";
        if (type.StartsWith("audio/", StringComparison.Ordinal) || ext is "mp3" or "wav" or "m4a" or "flac" or "ogg") return "audio";
        if (type.StartsWith("video/", StringComparison.Ordinal) || ext is "mp4" or "mov" or "avi" or "mkv" or "webm") return "video";
        if (ext is "ics" or "vcs" || type == "text/calendar") return "calendar";
        return "other";
    }

    /// <summary>True if the attachment is in the named group, or has that extension (e.g. "docx").</summary>
    public static bool Matches(AttachmentInfo a, string typeOrExtension)
    {
        var wanted = typeOrExtension.Trim().TrimStart('.').ToLowerInvariant();
        if (wanted is "photo" or "picture" or "images") wanted = "image";
        if (wanted is "word") wanted = "document";
        if (wanted is "excel") wanted = "spreadsheet";
        if (wanted is "zip" && a.Extension.Equals("zip", StringComparison.OrdinalIgnoreCase)) return true;
        return GroupOf(a) == wanted || a.Extension.Equals(wanted, StringComparison.OrdinalIgnoreCase);
    }
}
