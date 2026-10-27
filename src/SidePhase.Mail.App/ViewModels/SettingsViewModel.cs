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

    public AccountSettingsViewModel(MailAccount account, ISettingsService settings)
    {
        Account = account;
        _settings = settings;
        Signature = settings.Current.Signatures.TryGetValue(account.Id, out var s) ? s : string.Empty;
    }

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

    [ObservableProperty]
    public partial string Signature { get; set; }

    partial void OnSignatureChanged(string value) =>
        _settings.Update(s =>
        {
            if (string.IsNullOrWhiteSpace(value)) s.Signatures.Remove(Account.Id);
            else s.Signatures[Account.Id] = value;
        });
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

    public SettingsViewModel(
        ISettingsService settings,
        ISenderRulesService rules,
        IMailDataSource mail,
        DialogService dialogs,
        ToastService toasts,
        ThemeService theme,
        IUiDispatcher ui,
        AppDataPaths paths)
    {
        _settings = settings;
        _rules = rules;
        _mail = mail;
        _dialogs = dialogs;
        _toasts = toasts;
        _theme = theme;
        _ui = ui;
        _paths = paths;

        NewTrustedSender = NewBlockedSender = NewBlockedDomain = RuleError = string.Empty;

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

    public bool BuggerBackBlocksDomain
    {
        get => S.BuggerBackBlocksDomainByDefault;
        set => Set(s => s.BuggerBackBlocksDomainByDefault = value, S.BuggerBackBlocksDomainByDefault != value);
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
        ? "No credentials are stored. Only demonstration accounts are configured."
        : "Sign-in tokens are encrypted with Windows Data Protection and never written to logs.";

    // ---------------------------------------------------------------- About

    public string VersionText
    {
        get
        {
            var version = typeof(SettingsViewModel).Assembly.GetName().Version;
            return $"Version {version?.ToString(3) ?? "0.1.0"} · Milestone 1 preview";
        }
    }

    public async Task LoadAsync()
    {
        Accounts.Clear();
        foreach (var account in await _mail.GetAccountsAsync())
        {
            Accounts.Add(new AccountSettingsViewModel(account, _settings));
        }

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
