namespace SidePhase.Mail.Core.Models;

public sealed record AttachmentInfo(string FileName, string ContentType, long SizeBytes, string? ContentId = null)
{
    public string Extension
    {
        get
        {
            var ext = System.IO.Path.GetExtension(FileName);
            return string.IsNullOrEmpty(ext) ? string.Empty : ext.TrimStart('.').ToUpperInvariant();
        }
    }

    public bool IsImage => ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
}
