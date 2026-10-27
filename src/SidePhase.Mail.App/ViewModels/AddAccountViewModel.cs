using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SidePhase.Mail.App.Services;
using SidePhase.Mail.Core.Abstractions;
using SidePhase.Mail.Core.Models;
using SidePhase.Mail.Infrastructure.Accounts;

namespace SidePhase.Mail.App.ViewModels;

public enum AddAccountPreset
{
    Any,
    Google,
    Microsoft,
    Microsoft365,
}

/// <summary>
/// Account setup: find settings for the address, sign in (OAuth in the browser, or a password), test incoming and
/// outgoing mail, and only then save. Every step can be corrected through the manual server settings.
/// </summary>
public sealed partial class AddAccountViewModel : ObservableObject
{
    private readonly IAccountService _accounts;
    private readonly ToastService _toasts;
    private AuthMethod _authMethod = AuthMethod.Password;
    private MailProviderKind _provider = MailProviderKind.Imap;
    private AddAccountPreset _preset;
    private CancellationTokenSource? _cts;

    public AddAccountViewModel(IAccountService accounts, ToastService toasts)
    {
        _accounts = accounts;
        _toasts = toasts;
        Email = DisplayName = Password = StatusText = ProviderText = PasswordHint = OAuthButtonText = OAuthUnavailableText = string.Empty;
        ImapHost = SmtpHost = Username = ImapResult = SmtpResult = string.Empty;
        Title = "Add an account";
        ImapPort = 993;
        SmtpPort = 465;
    }

    /// <summary>Raised after the account has been tested and saved.</summary>
    public event EventHandler<MailAccount>? Completed;

    [ObservableProperty] public partial string Title { get; set; }
    [ObservableProperty] public partial string Email { get; set; }
    [ObservableProperty] public partial string DisplayName { get; set; }
    [ObservableProperty] public partial string Password { get; set; }

    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial string StatusText { get; set; }
    [ObservableProperty] public partial bool StatusIsError { get; set; }

    [ObservableProperty] public partial bool HasDiscovered { get; set; }
    [ObservableProperty] public partial string ProviderText { get; set; }
    [ObservableProperty] public partial string PasswordHint { get; set; }
    [ObservableProperty] public partial bool UsesOAuth { get; set; }
    [ObservableProperty] public partial bool OAuthAvailable { get; set; }
    [ObservableProperty] public partial bool CanUseAppPassword { get; set; }
    [ObservableProperty] public partial string OAuthButtonText { get; set; }
    [ObservableProperty] public partial string OAuthUnavailableText { get; set; }

    [ObservableProperty] public partial bool ShowServerSettings { get; set; }
    [ObservableProperty] public partial string ImapHost { get; set; }
    [ObservableProperty] public partial double ImapPort { get; set; }
    [ObservableProperty] public partial int ImapSecurityIndex { get; set; }
    [ObservableProperty] public partial string SmtpHost { get; set; }
    [ObservableProperty] public partial double SmtpPort { get; set; }
    [ObservableProperty] public partial int SmtpSecurityIndex { get; set; }
    [ObservableProperty] public partial string Username { get; set; }

    [ObservableProperty] public partial string ImapResult { get; set; }
    [ObservableProperty] public partial string SmtpResult { get; set; }

    public bool ShowPasswordForm => HasDiscovered && !UsesOAuth;
    public bool ShowOAuthForm => HasDiscovered && UsesOAuth;

    partial void OnHasDiscoveredChanged(bool value) => NotifyForms();

    partial void OnUsesOAuthChanged(bool value) => NotifyForms();

    partial void OnEmailChanged(string value)
    {
        // A different address needs fresh settings.
        if (HasDiscovered) HasDiscovered = false;
        ImapResult = SmtpResult = string.Empty;
    }

    private void NotifyForms()
    {
        OnPropertyChanged(nameof(ShowPasswordForm));
        OnPropertyChanged(nameof(ShowOAuthForm));
    }

    public void Apply(AddAccountPreset preset)
    {
        _preset = preset;
        HasDiscovered = false;
        Title = preset switch
        {
            AddAccountPreset.Google => "Add your Google account",
            AddAccountPreset.Microsoft => "Add your Microsoft account",
            AddAccountPreset.Microsoft365 => "Add a work or school account",
            _ => "Add an account",
        };
    }

    [RelayCommand]
    private async Task ContinueAsync()
    {
        var email = Email.Trim();
        if (!AccountDiscovery.IsValidAddress(email))
        {
            SetStatus("Enter your full email address, such as name@example.com.", error: true);
            return;
        }

        await RunAsync("Looking up the settings for your provider…", async ct =>
        {
            AccountDiscoveryResult result;
            if (_preset == AddAccountPreset.Microsoft365)
            {
                result = new AccountDiscoveryResult
                {
                    EmailAddress = email,
                    ProviderName = "Microsoft 365",
                    Provider = MailProviderKind.Microsoft,
                    Source = DiscoverySource.KnownProvider,
                    Settings = AccountDiscovery.Microsoft365(email),
                };
            }
            else
            {
                result = await _accounts.DiscoverAsync(email, ct);
            }

            ApplyDiscovery(result);
            HasDiscovered = true;
            SetStatus(result.Source switch
            {
                DiscoverySource.Guess => $"We couldn't find published settings for {email[(email.IndexOf('@') + 1)..]}, so we've filled in common ones. They'll be tested before anything is saved — check them under Server settings if the test fails.",
                _ => string.Empty,
            }, error: false);
        });
    }

    private void ApplyDiscovery(AccountDiscoveryResult result)
    {
        var settings = result.Settings!;
        _provider = result.Provider;
        _authMethod = settings.AuthMethod;
        ProviderText = result.Source switch
        {
            DiscoverySource.KnownProvider => $"{result.ProviderName} · recognised provider",
            DiscoverySource.Guess => "Unknown provider · settings not yet confirmed",
            _ => $"{result.ProviderName ?? "Your provider"} · settings published by the provider",
        };
        PasswordHint = result.PasswordHint ?? string.Empty;
        ImapHost = settings.Imap.Host;
        ImapPort = settings.Imap.Port;
        ImapSecurityIndex = settings.Imap.Security == ConnectionSecurity.StartTls ? 1 : 0;
        SmtpHost = settings.Smtp.Host;
        SmtpPort = settings.Smtp.Port;
        SmtpSecurityIndex = settings.Smtp.Security == ConnectionSecurity.StartTls ? 1 : 0;
        Username = settings.Username;
        ShowServerSettings = result.IsGuess;
        if (string.IsNullOrWhiteSpace(DisplayName)) DisplayName = Email.Trim()[..Email.Trim().IndexOf('@')];
        UpdateAuthPresentation();
    }

    private void UpdateAuthPresentation()
    {
        UsesOAuth = _authMethod is AuthMethod.OAuthGoogle or AuthMethod.OAuthMicrosoft;
        var provider = _authMethod == AuthMethod.OAuthGoogle ? "Google" : "Microsoft";
        OAuthButtonText = $"Sign in with {provider}";
        OAuthAvailable = UsesOAuth && _accounts.IsOAuthConfigured(_authMethod);
        CanUseAppPassword = _authMethod == AuthMethod.OAuthGoogle;
        OAuthUnavailableText = OAuthAvailable
            ? $"Your browser will open {provider}'s own sign-in page. SidePhase Mail never sees your {provider} password."
            : $"{provider} sign-in needs an OAuth app registration that hasn't been configured in this build. Add a client ID under Settings → Accounts (see docs/ACCOUNT_PROVIDERS.md)."
              + (CanUseAppPassword ? " You can also use a Google app password instead." : string.Empty);
    }

    /// <summary>Gmail accounts with 2-Step Verification can use an app password over IMAP instead of OAuth.</summary>
    [RelayCommand]
    private void UseAppPassword()
    {
        _authMethod = AuthMethod.Password;
        PasswordHint = "Create an app password at myaccount.google.com/apppasswords (requires 2-Step Verification) and paste it here.";
        UpdateAuthPresentation();
    }

    [RelayCommand]
    private async Task SignInAsync()
    {
        await RunAsync("Waiting for you to finish signing in in your browser…", async ct =>
        {
            var tokens = await _accounts.SignInAsync(_authMethod, Email.Trim(), ct);
            if (!string.IsNullOrEmpty(tokens.Email) && !tokens.Email.Equals(Email.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                SetStatus($"You signed in as {tokens.Email}, which is different from {Email.Trim()}. The account will be added as {tokens.Email}.", error: false);
                Email = tokens.Email;
                HasDiscovered = true;
            }

            await TestAndAddAsync(BuildRequest(password: null, tokens), ct);
        });
    }

    [RelayCommand]
    private async Task AddAsync()
    {
        if (string.IsNullOrEmpty(Password))
        {
            SetStatus("Enter the password for this account.", error: true);
            return;
        }

        await RunAsync("Testing incoming and outgoing mail…", ct => TestAndAddAsync(BuildRequest(Password, null), ct));
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    private async Task TestAndAddAsync(AccountSetupRequest request, CancellationToken ct)
    {
        SetStatus("Testing incoming and outgoing mail…", error: false);
        var test = await _accounts.TestAsync(request, ct);
        ImapResult = test.ImapOk ? "✓ Incoming mail (IMAP) connected" : "✗ Incoming mail (IMAP): " + test.ImapError;
        SmtpResult = test.SmtpOk ? "✓ Outgoing mail (SMTP) connected" : "✗ Outgoing mail (SMTP): " + test.SmtpError;
        if (!test.Succeeded)
        {
            ShowServerSettings = true;
            SetStatus("Nothing has been saved. Check the details and try again.", error: true);
            return;
        }

        SetStatus("Saving the account…", error: false);
        var account = await _accounts.AddAsync(request, ct);
        Password = string.Empty;
        _toasts.Show($"{account.EmailAddress} added. Downloading your mail…", ToastKind.Success);
        Completed?.Invoke(this, account);
    }

    private AccountSetupRequest BuildRequest(string? password, OAuthTokens? tokens) => new()
    {
        EmailAddress = Email.Trim(),
        DisplayName = string.IsNullOrWhiteSpace(DisplayName) ? Email.Trim() : DisplayName.Trim(),
        Provider = _provider,
        Password = password,
        Tokens = tokens,
        Settings = new AccountConnectionSettings
        {
            Imap = new ServerEndpoint(ImapHost.Trim(), (int)ImapPort, ImapSecurityIndex == 1 ? ConnectionSecurity.StartTls : ConnectionSecurity.SslOnConnect),
            Smtp = new ServerEndpoint(SmtpHost.Trim(), (int)SmtpPort, SmtpSecurityIndex == 1 ? ConnectionSecurity.StartTls : ConnectionSecurity.SslOnConnect),
            Username = string.IsNullOrWhiteSpace(Username) ? Email.Trim() : Username.Trim(),
            AuthMethod = _authMethod,
        },
    };

    private async Task RunAsync(string status, Func<CancellationToken, Task> work)
    {
        if (IsBusy) return;
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        IsBusy = true;
        SetStatus(status, error: false);
        try
        {
            await work(_cts.Token);
        }
        catch (OperationCanceledException)
        {
            SetStatus("Cancelled. Nothing has been saved.", error: false);
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, error: true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void SetStatus(string text, bool error)
    {
        StatusText = text;
        StatusIsError = error;
    }
}
