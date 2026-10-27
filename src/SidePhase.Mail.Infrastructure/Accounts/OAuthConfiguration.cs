using SidePhase.Mail.Core.Abstractions;
using SidePhase.Mail.Core.Models;
using SidePhase.Mail.Infrastructure.OAuth;
using SidePhase.Mail.Infrastructure.Security;

namespace SidePhase.Mail.Infrastructure.Accounts;

/// <summary>
/// Resolves the OAuth client registrations. Nothing is compiled in: client IDs come from environment variables or
/// Settings, and the Google client secret from an environment variable or the encrypted secret store.
/// </summary>
public sealed class OAuthConfiguration(ISettingsService settings, ISecretStore secrets)
{
    public const string GoogleClientIdVariable = "SIDEPHASE_GOOGLE_CLIENT_ID";
    public const string GoogleClientSecretVariable = "SIDEPHASE_GOOGLE_CLIENT_SECRET";
    public const string MicrosoftClientIdVariable = "SIDEPHASE_MICROSOFT_CLIENT_ID";
    public const string MicrosoftTenantVariable = "SIDEPHASE_MICROSOFT_TENANT";

    public bool IsConfigured(AuthMethod method) => !string.IsNullOrWhiteSpace(ClientId(method));

    public string? ClientId(AuthMethod method) => method switch
    {
        AuthMethod.OAuthGoogle => Environment.GetEnvironmentVariable(GoogleClientIdVariable) ?? settings.Current.GoogleClientId,
        AuthMethod.OAuthMicrosoft => Environment.GetEnvironmentVariable(MicrosoftClientIdVariable) ?? settings.Current.MicrosoftClientId,
        _ => null,
    };

    public async Task<OAuthProviderConfig> GetAsync(AuthMethod method, CancellationToken ct = default)
    {
        var clientId = ClientId(method);
        if (string.IsNullOrWhiteSpace(clientId))
        {
            throw new OAuthException(method == AuthMethod.OAuthGoogle
                ? "Google sign-in isn't set up yet. Add a Google OAuth client ID in Settings → Accounts (see docs/ACCOUNT_PROVIDERS.md)."
                : "Microsoft sign-in isn't set up yet. Add a Microsoft application (client) ID in Settings → Accounts (see docs/ACCOUNT_PROVIDERS.md).");
        }

        return method switch
        {
            AuthMethod.OAuthGoogle => OAuthProviders.Google(
                clientId.Trim(),
                Environment.GetEnvironmentVariable(GoogleClientSecretVariable) ?? await secrets.GetAsync(SecretKeys.GoogleClientSecret, ct).ConfigureAwait(false)),
            AuthMethod.OAuthMicrosoft => OAuthProviders.Microsoft(clientId.Trim(), Environment.GetEnvironmentVariable(MicrosoftTenantVariable) ?? "common"),
            _ => throw new ArgumentOutOfRangeException(nameof(method)),
        };
    }
}
