using System.Text.Json;
using System.Text.Json.Serialization;

namespace SidePhase.Mail.Advertising;

/// <summary>Which ads may be shown.</summary>
public enum AdvertisingMode
{
    /// <summary>No sponsored cards.</summary>
    Off,

    /// <summary>The built-in test cards only (for previewing the design). Nothing leaves the PC.</summary>
    Demo,

    /// <summary>
    /// Approved campaigns from SidePhase Studio's advertising service. Not available until
    /// <see cref="AdvertisingPolicy.ProductionAdvertisingEnabled"/> is turned on in a reviewed release.
    /// </summary>
    Production,
}

/// <summary>Release-level switches, changed only in reviewed code (never by a setting or a server).</summary>
public static class AdvertisingPolicy
{
    /// <summary>
    /// Production advertising stays off until campaigns are approved and the privacy policy describes it. While false,
    /// <see cref="AdvertisingMode.Production"/> behaves like <see cref="AdvertisingMode.Off"/>.
    /// </summary>
    public const bool ProductionAdvertisingEnabled = false;

    /// <summary>How long a card stays before another may replace it (no rotating or flashing cards).</summary>
    public static readonly TimeSpan MinimumDisplayTime = TimeSpan.FromMinutes(10);
}

/// <summary>Advertising preferences, stored on their own (advertising.json), apart from the mail settings.</summary>
public sealed class AdvertisingSettings
{
    public AdvertisingMode Mode { get; set; } = AdvertisingMode.Off;

    /// <summary>The future advertising API (HTTPS). Ignored while production advertising is disabled.</summary>
    public string? Endpoint { get; set; }

    /// <summary>
    /// For testing the Premium experience before the Store add-on exists: behaves as if the ad-free upgrade were
    /// owned. Real entitlement will come from the Microsoft Store (see <see cref="IAdEntitlement"/>).
    /// </summary>
    public bool SimulateAdFree { get; set; }

    internal static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };
}

/// <summary>Reads and writes advertising.json in the app's data folder (atomic writes; unreadable means defaults).</summary>
public sealed class AdvertisingSettingsStore(string folder)
{
    public string File => Path.Combine(folder, "advertising.json");

    public AdvertisingSettings Load()
    {
        try
        {
            if (!System.IO.File.Exists(File)) return new AdvertisingSettings();
            return JsonSerializer.Deserialize<AdvertisingSettings>(System.IO.File.ReadAllText(File), AdvertisingSettings.Json) ?? new AdvertisingSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Advertising preferences are never worth failing over; the safe default is Off.
            return new AdvertisingSettings();
        }
    }

    public void Save(AdvertisingSettings settings)
    {
        Directory.CreateDirectory(folder);
        var temp = File + ".tmp";
        System.IO.File.WriteAllText(temp, JsonSerializer.Serialize(settings, AdvertisingSettings.Json));
        System.IO.File.Move(temp, File, overwrite: true);
    }
}
