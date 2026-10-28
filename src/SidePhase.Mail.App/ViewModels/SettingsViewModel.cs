using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SidePhase.Mail.App.Services;
using SidePhase.Mail.Core.Abstractions;
using SidePhase.Mail.Core.Composition;
using SidePhase.Mail.Core.Models;
using SidePhase.Mail.Core.Settings;
using SidePhase.Mail.Infrastructure.Storage;

namespace SidePhase.Mail.App.ViewModels;

public sealed partial class AccentOptionViewModel(string name, string hex, Action<AccentOptionViewModel> select) : ObservableObject
{
    public string Name { get; } = name;
    public string Hex { get; } = hex;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    [RelayCommand]
    private void Select() => select(this);
}

public sealed partial class SenderRuleItemViewModel(SenderRule rule, Action<SenderRuleItemViewModel> remove)
{
    public SenderRule Rule { get; } = rule;
    public string Value => Rule.Value;
    public string AddedText => "Added " + Rule.CreatedAt.ToLocalTime().ToString("d", CultureInfo.CurrentCulture);

    [RelayCommand]
    private void Remove() => remove(this);
}

public sealed partial class AccountSettingsViewModel : ObservableObject
{
    private readonly ISettingsService _settings;
    private readonly Func<AccountSettingsViewModel, Task> _reauthenticate;
    private readonly Func<AccountSettingsViewModel, Task> _remove;

    public AccountSettingsViewModel(MailAccount account, ISettingsService settings, Func<AccountSettingsViewModel, Task> reauthenticate, Func<AccountSettingsViewModel, Task> remove)
    {
        Account = account;
        _settings = settings;
        _reauthenticate = reauthenticate;
        _remove = remove;
    }

    /// <summary>"None" followed by the signature library, for the default-signature picker.</summary>
    public IReadOnlyList<string> SignatureOptions =>
        ["None", .. _settings.Current.SignatureLibrary.Select(s => s.Name)];

    public int DefaultSignatureIndex
    {
        get
        {
            var current = _settings.Current.DefaultSignatureFor(Account.Id);
            return current is null ? 0 : _settings.Current.SignatureLibrary.FindIndex(s => s.Id == current.Id) + 1;
        }
        set
        {
            if (value < 0 || value == DefaultSignatureIndex) return;
            _settings.Update(s =>
            {
                if (value == 0 || value > s.SignatureLibrary.Count) s.DefaultSignatureIds.Remove(Account.Id);
                else s.DefaultSignatureIds[Account.Id] = s.SignatureLibrary[value - 1].Id;
            });
            OnPropertyChanged();
        }
    }

    /// <summary>Call after the signature library changes so the picker shows the new names.</summary>
    public void RefreshSignatures()
    {
        OnPropertyChanged(nameof(SignatureOptions));
        OnPropertyChanged(nameof(DefaultSignatureIndex));
    }

    [RelayCommand]
    private Task ReauthenticateAsync() => _reauthenticate(this);

    [RelayCommand]
    private Task RemoveAsync() => _remove(this);

    public MailAccount Account { get; }
    public string Name => Account.DisplayName;
    public string Address => Account.EmailAddress;
    public string ColorHex => Account.ColorHex ?? "#8B7CFF";
    public string ProviderText => Account.Provider switch
    {
        MailProviderKind.Demo => "Demonstration account · sample data only",
        MailProviderKind.Gmail => "Google",
        MailProviderKind.Microsoft => "Microsoft",
        _ => "IMAP / SMTP",
    };

    public bool CanRemove => !Account.IsDemo;

    public bool NotificationsEnabled
    {
        get => !_settings.Current.MutedNotificationAccounts.Contains(Account.Id);
        set
        {
            if (value == NotificationsEnabled) return;
            _settings.Update(s =>
            {
                if (value) s.MutedNotificationAccounts.Remove(Account.Id);
                else s.MutedNotificationAccounts.Add(Account.Id);
            });
            OnPropertyChanged();
        }
    }

}

/// <summary>One signature in the library, edited in place.</summary>
public sealed partial class SignatureItemViewModel(Guid id, ISettingsService settings, Action<SignatureItemViewModel> deleted, Action renamed) : ObservableObject
{
    public Guid Id { get; } = id;

    private SignatureDefinition? Definition => settings.Current.SignatureLibrary.FirstOrDefault(s => s.Id == Id);

    public string Name
    {
        get => Definition?.Name ?? string.Empty;
        set
        {
            if (value == Name) return;
            settings.Update(s =>
            {
                if (s.SignatureLibrary.FirstOrDefault(x => x.Id == Id) is { } d) d.Name = value;
            });
            renamed();
        }
    }

    public string Text
    {
        get => Definition?.Text ?? string.Empty;
        set
        {
            if (value == Text) return;
            settings.Update(s =>
            {
                if (s.SignatureLibrary.FirstOrDefault(x => x.Id == Id) is { } d) d.Text = value;
            });
        }
    }

    [RelayCommand]
    private void Delete()
    {
        settings.Update(s =>
        {
            s.SignatureLibrary.RemoveAll(x => x.Id == Id);
            foreach (var account in s.DefaultSignatureIds.Where(p => p.Value == Id).Select(p => p.Key).ToList()) s.DefaultSignatureIds.Remove(account);
        });
        deleted(this);
    }
}

/// <summary>Settings page. Every property writes straight through to <see cref="ISettingsService"/>.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private static readonly int[] SyncIntervals = [1, 5, 15, 30, 60];

    private readonly ISettingsService _settings;
    private readonly ISenderRulesService _rules;
    private readonly IMailDataSource _mail;
    private readonly DialogService _dialogs;
    private readonly ToastService _toasts;
    private readonly ThemeService _theme;
    private readonly IUiDispatcher _ui;
    private readonly AppDataPaths _paths;
    private readonly IAccountService _accounts;
    private readonly ISecretStore _secrets;

    public SettingsViewModel(
        ISettingsService settings,
        ISenderRulesService rules,
        IMailDataSource mail,
        DialogService dialogs,
        ToastService toasts,
        ThemeService theme,
        IUiDispatcher ui,
        AppDataPaths paths,
        IAccountService accounts,
        ISecretStore secrets)
    {
        _accounts = accounts;
        _secrets = secrets;
        _settings = settings;
        _rules = rules;
        _mail = mail;
        _dialogs = dialogs;
        _toasts = toasts;
        _theme = theme;
        _ui = ui;
        _paths = paths;

        NewTrustedSender = NewBlockedSender = NewBlockedDomain = RuleError = GoogleClientSecret = string.Empty;

        AccentOptions =
        [
            new("Violet", "#8B7CFF", SelectAccent),
            new("Indigo", "#6C8CFF", SelectAccent),
            new("Azure", "#3FA7F5", SelectAccent),
            new("Teal", "#3FB6A8", SelectAccent),
            new("Mint", "#4CC38A", SelectAccent),
            new("Amber", "#E9A93B", SelectAccent),
            new("Coral", "#FF7A6B", SelectAccent),
            new("Rose", "#F06BA8", SelectAccent),
            new("Graphite", "#9DA5B4", SelectAccent),
        ];

        _rules.RulesChanged += (_, _) => _ui.Post(RebuildRules);
        _settings.SettingsChanged += (_, _) => _ui.Post(UpdateAccentSelection);
        RebuildRules();
        UpdateAccentSelection();
    }

    public ObservableCollection<AccentOptionViewModel> AccentOptions { get; }
    public ObservableCollection<AccountSettingsViewModel> Accounts { get; } = [];
    public ObservableCollection<SignatureItemViewModel> Signatures { get; } = [];

    [RelayCommand]
    private void AddSignature()
    {
        var signature = new SignatureDefinition { Name = $"Signature {_settings.Current.SignatureLibrary.Count + 1}" };
        _settings.Update(s => s.SignatureLibrary.Add(signature));
        Signatures.Add(CreateSignatureItem(signature.Id));
        foreach (var account in Accounts) account.RefreshSignatures();
    }

    private SignatureItemViewModel CreateSignatureItem(Guid id) => new(id, _settings, item =>
    {
        Signatures.Remove(item);
        foreach (var account in Accounts) account.RefreshSignatures();
    }, () =>
    {
        foreach (var account in Accounts) account.RefreshSignatures();
    });
    public ObservableCollection<SenderRuleItemViewModel> TrustedSenders { get; } = [];
    public ObservableCollection<SenderRuleItemViewModel> BlockedSenders { get; } = [];
    public ObservableCollection<SenderRuleItemViewModel> BlockedDomains { get; } = [];

    public event EventHandler? ShowWelcomeRequested;

    private AppSettings S => _settings.Current;

    // ---------------------------------------------------------------- Appearance

    public int ThemeIndex
    {
        get => (int)S.Theme;
        set => Set(s => s.Theme = (AppTheme)value, (int)S.Theme != value && value >= 0);
    }

    public string ThemeNote => _theme.IsHighContrast
        ? "A Windows contrast theme is active, so SidePhase Mail uses your contrast colours."
        : _theme.TransparencyDisabledBySystem
            ? "Windows transparency effects are turned off, so glass themes are shown as solid surfaces."
            : "Glass themes use the native Windows 11 Mica material, tinted by your desktop wallpaper.";

    public int DensityIndex
    {
        get => (int)S.Density;
        set => Set(s => s.Density = (ListDensity)value, (int)S.Density != value && value >= 0);
    }

    public int ReadingPaneIndex
    {
        get => (int)S.ReadingPane;
        set => Set(s => s.ReadingPane = (ReadingPanePosition)value, (int)S.ReadingPane != value && value >= 0);
    }

    public bool SidebarCollapsed
    {
        get => S.SidebarStartsCollapsed;
        set => Set(s => s.SidebarStartsCollapsed = value, S.SidebarStartsCollapsed != value);
    }

    public bool ShowUnreadBadges
    {
        get => S.ShowUnreadBadges;
        set => Set(s => s.ShowUnreadBadges = value, S.ShowUnreadBadges != value);
    }

    // ---------------------------------------------------------------- Mail

    public bool MarkAsReadOnOpen
    {
        get => S.MarkAsReadOnOpen;
        set => Set(s => s.MarkAsReadOnOpen = value, S.MarkAsReadOnOpen != value);
    }

    public bool NotificationsEnabled
    {
        get => S.NotificationsEnabled;
        set => Set(s => s.NotificationsEnabled = value, S.NotificationsEnabled != value);
    }

    public bool ShowPinnedSection
    {
        get => S.ShowPinnedSection;
        set => Set(s => s.ShowPinnedSection = value, S.ShowPinnedSection != value);
    }

    public bool PinnedSectionCollapsed
    {
        get => S.PinnedSectionCollapsed;
        set => Set(s => s.PinnedSectionCollapsed = value, S.PinnedSectionCollapsed != value);
    }

    public int ComposeFormatIndex
    {
        get => (int)S.DefaultComposeFormat;
        set => Set(s => s.DefaultComposeFormat = (ComposeFormat)value, (int)S.DefaultComposeFormat != value && value >= 0);
    }

    private static readonly int[] UndoSendDelays = [0, 5, 10, 20, 30];

    public int UndoSendIndex
    {
        get => Math.Max(0, Array.IndexOf(UndoSendDelays, S.UndoSendSeconds));
        set
        {
            if (value < 0 || value >= UndoSendDelays.Length) return;
            Set(s => s.UndoSendSeconds = UndoSendDelays[value], S.UndoSendSeconds != UndoSendDelays[value]);
        }
    }

    public bool ConfirmCtrlEnterSend
    {
        get => S.ConfirmCtrlEnterSend;
        set => Set(s => s.ConfirmCtrlEnterSend = value, S.ConfirmCtrlEnterSend != value);
    }

    public bool ConversationView
    {
        get => S.ConversationView;
        set => Set(s => s.ConversationView = value, S.ConversationView != value);
    }

    public int SyncIntervalIndex
    {
        get => Math.Max(0, Array.IndexOf(SyncIntervals, S.SyncIntervalMinutes));
        set
        {
            if (value < 0 || value >= SyncIntervals.Length) return;
            Set(s => s.SyncIntervalMinutes = SyncIntervals[value], S.SyncIntervalMinutes != SyncIntervals[value]);
        }
    }

    // ---------------------------------------------------------------- Spam

    public double SpamThreshold
    {
        get => S.SpamThreshold;
        set
        {
            var rounded = Math.Round(value * 2) / 2; // half-point steps
            Set(s => s.SpamThreshold = rounded, Math.Abs(S.SpamThreshold - rounded) > 0.01);
            OnPropertyChanged(nameof(SpamThresholdText));
        }
    }

    public string SpamThresholdText => S.SpamThreshold.ToString("0.0", CultureInfo.CurrentCulture);

    public bool TreatHighScoresAsJunk
    {
        get => S.TreatHighScoresAsJunk;
        set => Set(s => s.TreatHighScoresAsJunk = value, S.TreatHighScoresAsJunk != value);
    }

    public bool MoveBlockedMailToServerJunk
    {
        get => S.MoveBlockedMailToServerJunk;
        set => Set(s => s.MoveBlockedMailToServerJunk = value, S.MoveBlockedMailToServerJunk != value);
    }

    public bool NotifyWhenFiltered
    {
        get => S.NotifyWhenFiltered;
        set => Set(s => s.NotifyWhenFiltered = value, S.NotifyWhenFiltered != value);
    }

    [ObservableProperty] public partial string NewTrustedSender { get; set; }
    [ObservableProperty] public partial string NewBlockedSender { get; set; }
    [ObservableProperty] public partial string NewBlockedDomain { get; set; }
    [ObservableProperty] public partial string RuleError { get; set; }

    // ---------------------------------------------------------------- Privacy

    public bool BlockRemoteImages
    {
        get => S.BlockRemoteImages;
        set => Set(s => s.BlockRemoteImages = value, S.BlockRemoteImages != value);
    }

    public string DataFolder => _paths.Root;

    public bool IsDemo => _mail.IsDemo;

    public string CredentialSummary => _mail.IsDemo
        ? "The demonstration mailbox stores no credentials. Real accounts' passwords and sign-in tokens are encrypted with Windows Data Protection for your user account, never written to logs, and never sent to SidePhase Studio."
        : "Passwords and sign-in tokens are encrypted with Windows Data Protection for your user account, never written to logs, and never sent to SidePhase Studio.";

    // ---------------------------------------------------------------- Accounts

    /// <summary>Shows the demonstration mailbox instead of real accounts. The two are never mixed.</summary>
    public bool UseDemoMailbox
    {
        get => S.DataMode == DataMode.Demo;
        set
        {
            if (UseDemoMailbox == value) return;
            _settings.Update(s => s.DataMode = value ? DataMode.Demo : DataMode.Live);
            _ = LoadAsync();
        }
    }

    public string GoogleClientId
    {
        get => S.GoogleClientId ?? string.Empty;
        set => Set(s => s.GoogleClientId = string.IsNullOrWhiteSpace(value) ? null : value.Trim(), (S.GoogleClientId ?? string.Empty) != value);
    }

    public string MicrosoftClientId
    {
        get => S.MicrosoftClientId ?? string.Empty;
        set => Set(s => s.MicrosoftClientId = string.IsNullOrWhiteSpace(value) ? null : value.Trim(), (S.MicrosoftClientId ?? string.Empty) != value);
    }

    [ObservableProperty] public partial string GoogleClientSecret { get; set; }

    [RelayCommand]
    private async Task SaveGoogleClientSecretAsync()
    {
        if (string.IsNullOrWhiteSpace(GoogleClientSecret))
        {
            await _secrets.DeleteAsync(Infrastructure.Security.SecretKeys.GoogleClientSecret);
            _toasts.Show("Google client secret removed.");
        }
        else
        {
            await _secrets.SetAsync(Infrastructure.Security.SecretKeys.GoogleClientSecret, GoogleClientSecret.Trim());
            _toasts.Show("Google client secret saved (encrypted).", ToastKind.Success);
        }

        GoogleClientSecret = string.Empty;
    }

    private async Task ReauthenticateAccountAsync(AccountSettingsViewModel item)
    {
        try
        {
            var connection = await _accounts.GetConnectionSettingsAsync(item.Account.Id);
            if (connection is null) return;

            if (connection.AuthMethod == AuthMethod.Password)
            {
                var password = await AskForPasswordAsync(item.Account);
                if (password is null) return;
                _toasts.Show($"Checking the new password for {item.Address}…");
                await _accounts.ReauthenticateAsync(item.Account.Id, password, null);
            }
            else
            {
                _toasts.Show("Finish signing in in your browser…");
                var tokens = await _accounts.SignInAsync(connection.AuthMethod, item.Address);
                await _accounts.ReauthenticateAsync(item.Account.Id, null, tokens);
            }

            _toasts.Show($"{item.Address} is signed in again.", ToastKind.Success);
        }
        catch (Exception ex)
        {
            _toasts.Show($"Couldn't sign in: {ex.Message}", ToastKind.Error);
        }
    }

    private async Task<string?> AskForPasswordAsync(MailAccount account)
    {
        var box = new Microsoft.UI.Xaml.Controls.PasswordBox { Header = $"Password for {account.EmailAddress}", MinWidth = 320 };
        var dialog = new Microsoft.UI.Xaml.Controls.ContentDialog
        {
            Title = "Sign in again",
            Content = box,
            PrimaryButtonText = "Test and save",
            CloseButtonText = "Cancel",
            DefaultButton = Microsoft.UI.Xaml.Controls.ContentDialogButton.Primary,
        };
        var result = await _dialogs.ShowAsync(dialog);
        return result == Microsoft.UI.Xaml.Controls.ContentDialogResult.Primary && box.Password.Length > 0 ? box.Password : null;
    }

    private async Task RemoveAccountAsync(AccountSettingsViewModel item)
    {
        var confirmed = await _dialogs.ConfirmAsync(
            $"Remove {item.Address}?",
            "Its cached messages, pins and saved sign-in are deleted from this PC. Nothing is deleted from your mail server, and you can add the account again at any time.",
            "Remove account");
        if (!confirmed) return;

        try
        {
            await _accounts.RemoveAsync(item.Account.Id);
            Accounts.Remove(item);
            _toasts.Show($"{item.Address} removed from this PC.", ToastKind.Success);
        }
        catch (Exception ex)
        {
            _toasts.Show($"Couldn't remove the account: {ex.Message}", ToastKind.Error);
        }
    }

    // ---------------------------------------------------------------- About

    public string VersionText
    {
        get
        {
            var version = typeof(SettingsViewModel).Assembly.GetName().Version;
            return $"Version {version?.ToString(3) ?? "0.1.0"} · Milestone 2 preview";
        }
    }

    public async Task LoadAsync()
    {
        Accounts.Clear();
        foreach (var account in await _mail.GetAccountsAsync())
        {
            Accounts.Add(new AccountSettingsViewModel(account, _settings, ReauthenticateAccountAsync, RemoveAccountAsync));
        }

        Signatures.Clear();
        foreach (var signature in _settings.Current.SignatureLibrary) Signatures.Add(CreateSignatureItem(signature.Id));

        OnPropertyChanged(string.Empty);
    }

    private void Set(Action<AppSettings> change, bool changed)
    {
        if (!changed) return;
        _settings.Update(change);
        OnPropertyChanged(string.Empty);
    }

    private void SelectAccent(AccentOptionViewModel option)
    {
        _settings.Update(s => s.AccentColor = option.Hex);
        UpdateAccentSelection();
    }

    private void UpdateAccentSelection()
    {
        foreach (var option in AccentOptions)
        {
            option.IsSelected = string.Equals(option.Hex, S.AccentColor, StringComparison.OrdinalIgnoreCase);
        }
    }

    private void RebuildRules()
    {
        void Fill(ObservableCollection<SenderRuleItemViewModel> target, SenderRuleKind kind)
        {
            target.Clear();
            foreach (var rule in _rules.Rules.Where(r => r.Kind == kind).OrderBy(r => r.Value, StringComparer.OrdinalIgnoreCase))
            {
                target.Add(new SenderRuleItemViewModel(rule, item => _rules.RemoveRule(item.Rule.Kind, item.Rule.Value)));
            }
        }

        Fill(TrustedSenders, SenderRuleKind.TrustedSender);
        Fill(BlockedSenders, SenderRuleKind.BlockedSender);
        Fill(BlockedDomains, SenderRuleKind.BlockedDomain);
        OnPropertyChanged(nameof(TrustedCountText));
        OnPropertyChanged(nameof(BlockedSendersCountText));
        OnPropertyChanged(nameof(BlockedDomainsCountText));
    }

    public string TrustedCountText => Count(TrustedSenders.Count, "trusted sender");
    public string BlockedSendersCountText => Count(BlockedSenders.Count, "blocked sender");
    public string BlockedDomainsCountText => Count(BlockedDomains.Count, "blocked domain");

    private static string Count(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";

    [RelayCommand]
    private void AddTrustedSender()
    {
        if (AddAddressRule(SenderRuleKind.TrustedSender, NewTrustedSender)) NewTrustedSender = string.Empty;
    }

    [RelayCommand]
    private void AddBlockedSender()
    {
        if (AddAddressRule(SenderRuleKind.BlockedSender, NewBlockedSender)) NewBlockedSender = string.Empty;
    }

    [RelayCommand]
    private void AddBlockedDomain()
    {
        var domain = NewBlockedDomain.Trim().TrimStart('@').ToLowerInvariant();
        if (domain.Length < 3 || !domain.Contains('.') || domain.Any(char.IsWhiteSpace))
        {
            RuleError = "Enter a domain such as example.com.";
            return;
        }

        RuleError = string.Empty;
        _rules.AddRule(SenderRuleKind.BlockedDomain, domain);
        NewBlockedDomain = string.Empty;
    }

    private bool AddAddressRule(SenderRuleKind kind, string text)
    {
        var parsed = RecipientParser.Parse(text);
        if (parsed.Addresses.Count != 1 || !parsed.IsValid)
        {
            RuleError = "Enter a single email address, such as name@example.com.";
            return false;
        }

        RuleError = string.Empty;
        _rules.AddRule(kind, parsed.Addresses[0].Address);
        return true;
    }

    [RelayCommand]
    private async Task ClearLocalDataAsync()
    {
        var message = _mail.IsDemo
            ? "This resets the demonstration mailbox to its original sample messages. Your settings are kept."
            : "This removes cached messages from this PC. Nothing is deleted from your mail server; messages download again on the next sync.";
        if (!await _dialogs.ConfirmAsync("Clear local data?", message, "Clear local data")) return;

        await _mail.ClearLocalDataAsync();
        _toasts.Show(_mail.IsDemo ? "Demonstration mailbox reset." : "Local cache cleared.", ToastKind.Success);
    }

    [RelayCommand]
    private async Task OpenDataFolderAsync()
    {
        _paths.EnsureCreated();
        await Windows.System.Launcher.LaunchFolderPathAsync(_paths.Root);
    }

    [RelayCommand]
    private void ShowWelcome() => ShowWelcomeRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void ResetAppearance() =>
        Set(s =>
        {
            s.Theme = AppTheme.DarkGlass;
            s.AccentColor = AppSettings.DefaultAccent;
            s.Density = ListDensity.Comfortable;
            s.ReadingPane = ReadingPanePosition.Right;
        }, changed: true);
}
