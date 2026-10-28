using System.Text.Json;
using System.Text.Json.Serialization;

namespace SidePhase.Mail.Advertising;

/// <summary>Which promotions may be shown in the footer.</summary>
public enum AdvertisingMode
{
    /// <summary>No banner; the footer stays collapsed.</summary>
    Off,

    /// <summary>The built-in test banner only (for previewing the placement). Nothing leaves the PC.</summary>
    Demo,

    /// <summary>
    /// Approved SidePhase Studio promotions bundled with the app (the default). Nothing is downloaded; with no approved
    /// promotion in the bundle, the footer stays collapsed.
    /// </summary>
    House,

    /// <summary>
    /// The separate SidePhase Ads API. Used only when explicitly configured: this mode and an https:// endpoint must
    /// both be set in advertising.json (there's no setting for it in the app). Otherwise it behaves like Off.
    /// </summary>
    Production,
}

/// <summary>Fixed rules for the placement.</summary>
public static class AdvertisingPolicy
{
    /// <summary>How long a banner stays before another may replace it (no rotating or flashing banners).</summary>
    public static readonly TimeSpan MinimumDisplayTime = TimeSpan.FromMinutes(10);

    /// <summary>The banner's design size in effective pixels.</summary>
    public const int BannerWidth = 728;
    public const int BannerHeight = 90;
}

/// <summary>Advertising preferences, stored on their own (advertising.json), apart from the mail settings.</summary>
public sealed class AdvertisingSettings
{
    public AdvertisingMode Mode { get; set; } = AdvertisingMode.House;

    /// <summary>The SidePhase Ads API (HTTPS). Only used with <see cref="AdvertisingMode.Production"/>.</summary>
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
            // Advertising preferences are never worth failing over: fall back to the defaults (bundled promotions only).
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
