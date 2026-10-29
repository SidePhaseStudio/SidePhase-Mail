using System.Reflection;
using SidePhase.Mail.Core.Abstractions;
using SidePhase.Mail.Core.Models;
using SidePhase.Mail.Infrastructure.OAuth;

namespace SidePhase.Mail.Infrastructure.Accounts;

/// <summary>One public Google Desktop registration for every installation; no confidential client credentials.</summary>
public sealed class OAuthConfiguration(ISettingsService settings)
{
    public const string GoogleClientIdVariable = "SIDEPHASE_GOOGLE_CLIENT_ID";
    public const string GoogleDesktopSecretVariable = "SIDEPHASE_GOOGLE_DESKTOP_CLIENT_SECRET";
    public const string MicrosoftClientIdVariable = "SIDEPHASE_MICROSOFT_CLIENT_ID";
    public const string MicrosoftTenantVariable = "SIDEPHASE_MICROSOFT_TENANT";

    public static string? BundledGoogleClientId => typeof(OAuthConfiguration).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "GoogleClientId").Value;

    public static string? BundledGoogleDesktopClientSecret => typeof(OAuthConfiguration).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "GoogleDesktopClientSecret").Value;

    private static string? GoogleDesktopSecret => ResolveGoogleClientId(
        Environment.GetEnvironmentVariable(GoogleDesktopSecretVariable), BundledGoogleDesktopClientSecret);

    public bool IsConfigured(AuthMethod method) => !string.IsNullOrWhiteSpace(ClientId(method))
        && (method != AuthMethod.OAuthGoogle || !string.IsNullOrWhiteSpace(GoogleDesktopSecret));

    public string? ClientId(AuthMethod method) => method switch
    {
        // The environment override is for developer testing. Distributed builds carry the Studio ID.
        AuthMethod.OAuthGoogle => ResolveGoogleClientId(Environment.GetEnvironmentVariable(GoogleClientIdVariable), BundledGoogleClientId),
        AuthMethod.OAuthMicrosoft => Environment.GetEnvironmentVariable(MicrosoftClientIdVariable) ?? settings.Current.MicrosoftClientId,
        _ => null,
    };

    internal static string? ResolveGoogleClientId(string? developmentOverride, string? bundled) =>
        string.IsNullOrWhiteSpace(developmentOverride) ? bundled?.Trim() : developmentOverride.Trim();

    public Task<OAuthProviderConfig> GetAsync(AuthMethod method, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var clientId = ClientId(method);
        if (!IsConfigured(method))
        {
            throw new OAuthException(method == AuthMethod.OAuthGoogle
                ? "Google sign-in isn't available in this build. Please install a build configured by SidePhase Studio."
                : "Microsoft sign-in isn't set up yet. Add a Microsoft application (client) ID in Settings → Accounts (see docs/ACCOUNT_PROVIDERS.md).");
        }

        return Task.FromResult(method switch
        {
            AuthMethod.OAuthGoogle => OAuthProviders.Google(clientId!.Trim(), GoogleDesktopSecret),
            AuthMethod.OAuthMicrosoft => OAuthProviders.Microsoft(clientId!.Trim(), Environment.GetEnvironmentVariable(MicrosoftTenantVariable) ?? "common"),
            _ => throw new ArgumentOutOfRangeException(nameof(method)),
        });
    }
}
