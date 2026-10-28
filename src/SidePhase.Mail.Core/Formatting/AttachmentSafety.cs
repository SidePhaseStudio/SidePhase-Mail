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
        // Office documents with macros, and Office formats that can run code or fetch remote content
        "docm", "dotm", "xlsm", "xltm", "xlam", "pptm", "potm", "ppam", "ppsm", "sldm",
        "doc", "dot", "xls", "xlt", "xla", "ppt", "pot", "pps", "xll", "one", "onepkg", "iqy", "slk", "mdb", "accde", "accdb",
        // Other launchers and remote-execution formats
        "rdp", "appref-ms", "diagcab", "search-ms", "searchconnector-ms", "jnlp", "ws", "msh", "mshxml", "cab", "xbap",
        // Local web pages and documents that open in a browser outside the safe reading pane (phishing forms)
        "htm", "html", "xht", "xhtml", "mht", "mhtml", "svg", "shtml",
    };

    // Characters that change how text is displayed (right-to-left overrides, isolates) and can disguise an extension.
    private static readonly HashSet<char> DirectionalControls =
    [
        '\u200E', '\u200F', '\u202A', '\u202B', '\u202C', '\u202D', '\u202E', '\u2066', '\u2067', '\u2068', '\u2069',
    ];

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM¹", "COM²", "COM³",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT¹", "LPT²", "LPT³",
    };

    /// <summary>
    /// True if opening the file could run a program, script or macro. Judged on the name the file would actually be
    /// saved under, so tricks the saved name drops (trailing dots, spaces or other whitespace, direction controls)
    /// can't hide the real extension.
    /// </summary>
    public static bool IsDangerous(string? fileName)
    {
        var ext = Path.GetExtension(SafeFileName(fileName)).TrimStart('.');
        return ext.Length > 0 && Dangerous.Contains(ext);
    }

    /// <summary>
    /// Names like "invoice.pdf.exe" that try to look like a harmless document, or that hide the real extension behind
    /// many spaces.
    /// </summary>
    public static bool HasMisleadingName(string? fileName)
    {
        if (!IsDangerous(fileName)) return false;
        var name = Path.GetFileNameWithoutExtension(SafeFileName(fileName));
        return Path.GetExtension(name).Length > 1 || name.Contains("   ", StringComparison.Ordinal)
            || (fileName ?? string.Empty).Any(DirectionalControls.Contains);
    }

    /// <summary>A name that is safe to save under: no folders, no characters Windows forbids, no reserved device names.</summary>
    public static string SafeFileName(string? name)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(['<', '>', ':', '"', '/', '\\', '|', '?', '*']).ToHashSet();
        var leaf = (name ?? string.Empty).Replace('\\', '/');
        leaf = leaf[(leaf.LastIndexOf('/') + 1)..];
        var cleaned = new string(leaf.Select(c => c < 32 || c == 127 || invalid.Contains(c) || DirectionalControls.Contains(c) ? '_' : c).ToArray());

        // Windows drops trailing dots and spaces; strip them and any other whitespace until nothing changes, so the
        // saved name's extension is the one that's checked.
        string before;
        do
        {
            before = cleaned;
            cleaned = cleaned.Trim().TrimEnd('.');
        }
        while (cleaned != before);
        if (cleaned.Length == 0 || cleaned.All(c => c == '.')) cleaned = "attachment";

        // Device names are reserved whatever follows the first dot ("nul.tar.gz", "CON .txt").
        var firstPart = cleaned.Split('.')[0].TrimEnd();
        if (ReservedNames.Contains(firstPart)) cleaned = "_" + cleaned;

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
