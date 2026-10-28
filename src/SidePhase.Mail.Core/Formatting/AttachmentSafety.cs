namespace SidePhase.Mail.Core.Formatting;

/// <summary>
/// File-name safety for attachments: which files can run code when opened, clean names for saving, and unique names
/// so saving never overwrites an existing file. SidePhase Mail never opens or runs attachments itself.
/// </summary>
public static class AttachmentSafety
{
    private static readonly HashSet<string> Dangerous = new(StringComparer.OrdinalIgnoreCase)
    {
        // Programs and installers
        "exe", "com", "scr", "pif", "msi", "msp", "mst", "msix", "msixbundle", "appx", "appxbundle", "appinstaller",
        "application", "gadget", "cpl", "dll", "sys", "drv", "ocx",
        // Scripts
        "bat", "cmd", "ps1", "psm1", "psd1", "ps1xml", "vbs", "vbe", "js", "jse", "wsf", "wsh", "wsc", "hta", "jar",
        "py", "pyw", "sh", "msc", "scf", "sct",
        // Shortcuts and settings that can launch things
        "lnk", "url", "inf", "reg", "chm", "settingcontent-ms", "library-ms", "website",
        // Disk images that can hide the above
        "iso", "img", "vhd", "vhdx",
        // Office documents with macros
        "docm", "dotm", "xlsm", "xltm", "xlam", "pptm", "potm", "ppam", "ppsm", "sldm",
    };

    /// <summary>True if opening the file could run a program, script or macro.</summary>
    public static bool IsDangerous(string? fileName)
    {
        // Windows ignores trailing dots and spaces, so "setup.exe. " is still a program.
        var ext = Path.GetExtension((fileName ?? string.Empty).TrimEnd('.', ' ')).TrimStart('.');
        return ext.Length > 0 && Dangerous.Contains(ext);
    }

    /// <summary>
    /// Names like "invoice.pdf.exe" that try to look like a harmless document, or that hide the real extension behind
    /// many spaces.
    /// </summary>
    public static bool HasMisleadingName(string? fileName)
    {
        if (!IsDangerous(fileName)) return false;
        var name = Path.GetFileNameWithoutExtension(fileName!.TrimEnd('.', ' '));
        return Path.GetExtension(name).Length > 1 || name.Contains("   ", StringComparison.Ordinal);
    }

    /// <summary>A name that is safe to save under: no folders, no characters Windows forbids, no reserved device names.</summary>
    public static string SafeFileName(string? name)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(['<', '>', ':', '"', '/', '\\', '|', '?', '*']).ToHashSet();
        var leaf = (name ?? string.Empty).Replace('\\', '/');
        leaf = leaf[(leaf.LastIndexOf('/') + 1)..];
        var cleaned = new string(leaf.Select(c => c < 32 || invalid.Contains(c) || c is '‮' or '‭' or '‎' or '‏' ? '_' : c).ToArray())
            .Trim().TrimEnd('.', ' ');
        if (cleaned.Length == 0 || cleaned.All(c => c == '.')) cleaned = "attachment";

        var stem = Path.GetFileNameWithoutExtension(cleaned);
        if (stem.ToUpperInvariant() is "CON" or "PRN" or "AUX" or "NUL" or "COM1" or "COM2" or "COM3" or "COM4" or "COM5"
            or "COM6" or "COM7" or "COM8" or "COM9" or "LPT1" or "LPT2" or "LPT3" or "LPT4" or "LPT5" or "LPT6" or "LPT7" or "LPT8" or "LPT9")
        {
            cleaned = "_" + cleaned;
        }

        if (cleaned.Length > 180)
        {
            var ext = Path.GetExtension(cleaned);
            cleaned = cleaned[..(180 - Math.Min(ext.Length, 20))] + ext[..Math.Min(ext.Length, 20)];
        }

        return cleaned;
    }

    /// <summary>"report.pdf" → "report (2).pdf" … until <paramref name="exists"/> says the name is free.</summary>
    public static string UniqueName(string fileName, Func<string, bool> exists)
    {
        if (!exists(fileName)) return fileName;
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        for (var i = 2; i < 10_000; i++)
        {
            var candidate = $"{stem} ({i}){ext}";
            if (!exists(candidate)) return candidate;
        }

        return $"{stem} ({Guid.NewGuid():N}){ext}";
    }
}
