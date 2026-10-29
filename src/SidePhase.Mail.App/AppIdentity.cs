using System.Reflection;
using System.Runtime.InteropServices;
using SidePhase.Mail.Infrastructure.Storage;

namespace SidePhase.Mail.App;

/// <summary>How this copy of SidePhase Mail was installed.</summary>
public enum DistributionChannel
{
    /// <summary>The unpackaged folder build (CI artifact or local build), started from SidePhaseMail.exe.</summary>
    Portable,

    /// <summary>An MSIX package installed outside the Store (beta sideloading or App Installer).</summary>
    Sideloaded,

    /// <summary>Installed from the Microsoft Store, which manages updates.</summary>
    Store,
}

/// <summary>
/// Package identity, version and data location of the running app. The installed (MSIX) app and the portable build
/// keep separate data folders: see docs/PACKAGING.md.
/// </summary>
public static class AppIdentity
{
    private const int APPMODEL_ERROR_NO_PACKAGE = 15700;
    private const int ERROR_INSUFFICIENT_BUFFER = 122;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref uint packageFullNameLength, char[]? packageFullName);

    private static readonly Lazy<bool> Packaged = new(DetectPackage);

    /// <summary>True when running from an installed MSIX package (with package identity).</summary>
    public static bool IsPackaged => Packaged.Value;

    /// <summary>Identifies the data folder in use (used for the single-instance key).</summary>
    public static string DataScope => IsPackaged ? "package" : "portable";

    public static DistributionChannel Channel
    {
        get
        {
            if (!IsPackaged) return DistributionChannel.Portable;
            try
            {
                return Windows.ApplicationModel.Package.Current.SignatureKind == Windows.ApplicationModel.PackageSignatureKind.Store
                    ? DistributionChannel.Store
                    : DistributionChannel.Sideloaded;
            }
            catch (Exception)
            {
                return DistributionChannel.Sideloaded;
            }
        }
    }

    /// <summary>The data root for this install: the package's LocalState folder, or %LOCALAPPDATA%\SidePhase Mail.</summary>
    public static string DataRoot
    {
        get
        {
            if (IsPackaged)
            {
                try
                {
                    return Windows.Storage.ApplicationData.Current.LocalFolder.Path;
                }
                catch (Exception)
                {
                    // Fall through: better to use the portable location than not start.
                }
            }

            return AppDataPaths.PortableRoot;
        }
    }

    /// <summary>The product version, e.g. "0.7.0-beta.1" (without build metadata).</summary>
    public static string Version
    {
        get
        {
            var informational = typeof(AppIdentity).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? typeof(AppIdentity).Assembly.GetName().Version?.ToString() ?? "0.0.0";
            var plus = informational.IndexOf('+');
            return plus < 0 ? informational : informational[..plus];
        }
    }

    /// <summary>The source revision the build came from (first 12 characters), if it was recorded.</summary>
    public static string? SourceRevision
    {
        get
        {
            var informational = typeof(AppIdentity).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            var plus = informational?.IndexOf('+') ?? -1;
            if (informational is null || plus < 0) return null;
            var revision = informational[(plus + 1)..];
            return revision.Length > 12 ? revision[..12] : revision;
        }
    }

    /// <summary>The package version (Store/MSIX builds) or null for the portable build.</summary>
    public static string? PackageVersion
    {
        get
        {
            if (!IsPackaged) return null;
            try
            {
                var v = Windows.ApplicationModel.Package.Current.Id.Version;
                return $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}";
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    public static string ChannelDescription => Channel switch
    {
        DistributionChannel.Store => "Microsoft Store (updates are managed by the Store)",
        DistributionChannel.Sideloaded => "Installed package (beta)",
        _ => "Portable build (no automatic updates)",
    };

    private static bool DetectPackage()
    {
        try
        {
            uint length = 0;
            var result = GetCurrentPackageFullName(ref length, null);
            return result != APPMODEL_ERROR_NO_PACKAGE && (result == ERROR_INSUFFICIENT_BUFFER || result == 0);
        }
        catch (Exception)
        {
            return false;
        }
    }
}
