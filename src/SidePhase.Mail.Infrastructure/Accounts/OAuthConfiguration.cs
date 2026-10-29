using System.Reflection;
using SidePhase.Mail.Core.Abstractions;
using SidePhase.Mail.Core.Models;
using SidePhase.Mail.Infrastructure.OAuth;

namespace SidePhase.Mail.Infrastructure.Accounts;

/// <summary>Publisher-owned public desktop registrations; no confidential client credentials.</summary>
public sealed class OAuthConfiguration(ISettingsService settings)
{
    public const string GoogleClientIdVariable = "SIDEPHASE_GOOGLE_CLIENT_ID";
    public const string GoogleDesktopSecretVariable = "SIDEPHASE_GOOGLE_DESKTOP_CLIENT_SECRET";
    public const string MicrosoftClientIdVariable = "SIDEPHASE_MICROSOFT_CLIENT_ID";
    public const string MicrosoftTenantVariable = "SIDEPHASE_MICROSOFT_TENANT";

    public static string? BundledGoogleClientId => typeof(OAuthConfiguration).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "GoogleClientId").Value;

    public static string? BundledMicrosoftClientId => typeof(OAuthConfiguration).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "MicrosoftClientId").Value;

    public static string? BundledGoogleDesktopClientSecret => typeof(OAuthConfiguration).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "GoogleDesktopClientSecret").Value;

    private static string? GoogleDesktopSecret => ResolveClientId(
        Environment.GetEnvironmentVariable(GoogleDesktopSecretVariable), BundledGoogleDesktopClientSecret);

    public bool IsConfigured(AuthMethod method) => !string.IsNullOrWhiteSpace(ClientId(method))
        && (method != AuthMethod.OAuthGoogle || !string.IsNullOrWhiteSpace(GoogleDesktopSecret));

    public string? ClientId(AuthMethod method) => method switch
    {
        // The environment override is for developer testing. Distributed builds carry the Studio ID.
        AuthMethod.OAuthGoogle => ResolveClientId(Environment.GetEnvironmentVariable(GoogleClientIdVariable), BundledGoogleClientId),
        AuthMethod.OAuthMicrosoft => ResolveMicrosoftClientId(Environment.GetEnvironmentVariable(MicrosoftClientIdVariable), BundledMicrosoftClientId, settings.Current.MicrosoftClientId),
        _ => null,
    };

    internal static string? ResolveClientId(string? developmentOverride, string? bundled) =>
        string.IsNullOrWhiteSpace(developmentOverride) ? bundled?.Trim() : developmentOverride.Trim();

    internal static string? ResolveMicrosoftClientId(string? developmentOverride, string? bundled, string? legacy) =>
        ResolveClientId(developmentOverride, ResolveClientId(bundled, legacy));

    public Task<OAuthProviderConfig> GetAsync(AuthMethod method, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var clientId = ClientId(method);
        if (!IsConfigured(method))
        {
            throw new OAuthException(method == AuthMethod.OAuthGoogle
                ? "Google sign-in isn't available in this build. Please install a build configured by SidePhase Studio."
                : "Microsoft sign-in isn't available in this build. Please install a build configured by SidePhase Studio.");
        }

        return Task.FromResult(method switch
        {
            AuthMethod.OAuthGoogle => OAuthProviders.Google(clientId!.Trim(), GoogleDesktopSecret),
            AuthMethod.OAuthMicrosoft => OAuthProviders.Microsoft(clientId!.Trim(), Environment.GetEnvironmentVariable(MicrosoftTenantVariable) ?? "common"),
            _ => throw new ArgumentOutOfRangeException(nameof(method)),
        });
    }
}
