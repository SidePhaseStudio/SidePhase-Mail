using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SidePhase.Mail.App.Services;
using SidePhase.Mail.Core.Abstractions;
using SidePhase.Mail.Core.Composition;
using SidePhase.Mail.Core.Models;
using SidePhase.Mail.Core.Settings;
using SidePhase.Mail.Infrastructure.Live;
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

public sealed partial class AccountColorOption(string name, string hex, Action<string> select) : ObservableObject
{
    public string Name { get; } = name;

    public string Hex { get; } = hex;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    [RelayCommand]
    private void Select() => select(Hex);
}

public sealed partial class AccountSettingsViewModel : ObservableObject
{
    private static readonly int?[] SyncChoices = [null, 1, 5, 15, 30, 60];

    private readonly ISettingsService _settings;
    private readonly Func<AccountSettingsViewModel, Task> _reauthenticate;
    private readonly Func<AccountSettingsViewModel, Task> _remove;
    private readonly Func<AccountSettingsViewModel, string, string?, Task<bool>> _saveAppearance;
    private readonly Func<Guid, AccountStatus> _status;

    public AccountSettingsViewModel(
        MailAccount account,
        ISettingsService settings,
        Func<AccountSettingsViewModel, Task> reauthenticate,
        Func<AccountSettingsViewModel, Task> remove,
        Func<AccountSettingsViewModel, string, string?, Task<bool>> saveAppearance,
        Func<Guid, AccountStatus> status)
    {
        Account = account;
        _settings = settings;
        _reauthenticate = reauthenticate;
        _remove = remove;
        _saveAppearance = saveAppearance;
        _status = status;
        EditName = account.DisplayName;
        ColorOptions = AccountPalette.Colours.Select(c => new AccountColorOption(c.Name, c.Hex, SelectColor)).ToList();
        UpdateColorSelection();
    }

    private void UpdateColorSelection()
    {
        foreach (var option in ColorOptions) option.IsSelected = string.Equals(option.Hex, Account.ColorHex, StringComparison.OrdinalIgnoreCase);
    }

    public IReadOnlyList<AccountColorOption> ColorOptions { get; }

    /// <summary>The name being edited (saved with <see cref="SaveNameCommand"/>).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveNameCommand))]
    public partial string EditName { get; set; }

    public bool CanEditAppearance => !Account.IsDemo;

    private bool CanSaveName() => CanEditAppearance && !string.IsNullOrWhiteSpace(EditName) && EditName.Trim() != Account.DisplayName;

    [RelayCommand(CanExecute = nameof(CanSaveName))]
    private async Task SaveNameAsync()
    {
        if (await _saveAppearance(this, EditName.Trim(), Account.ColorHex)) SetAccount(Account with { DisplayName = EditName.Trim() });
    }

    private async void SelectColor(string hex)
    {
        if (!CanEditAppearance || string.Equals(hex, Account.ColorHex, StringComparison.OrdinalIgnoreCase)) return;
        if (await _saveAppearance(this, Account.DisplayName, hex))
        {
            SetAccount(Account with { ColorHex = hex });
            UpdateColorSelection();
        }
    }

    private void SetAccount(MailAccount account)
    {
        Account = account;
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(ColorHex));
        SaveNameCommand.NotifyCanExecuteChanged();
    }

    /// <summary>"Same as all accounts", then 1, 5, 15, 30 and 60 minutes.</summary>
    public int SyncIntervalIndex
    {
        get => _settings.Current.AccountSyncIntervals.TryGetValue(Account.Id, out var m) ? Math.Max(0, Array.IndexOf(SyncChoices, m)) : 0;
        set
        {
            if (value < 0 || value >= SyncChoices.Length || value == SyncIntervalIndex) return;
            _settings.Update(s =>
            {
                if (SyncChoices[value] is { } minutes) s.AccountSyncIntervals[Account.Id] = minutes;
                else s.AccountSyncIntervals.Remove(Account.Id);
            });
            OnPropertyChanged();
        }
    }

    /// <summary>New messages written from the Unified Inbox (or anywhere without an account) are sent from here.</summary>
    public bool IsDefaultSender
    {
        get => _settings.Current.DefaultSendingAccountId == Account.Id;
        set
        {
            if (value == IsDefaultSender) return;
            _settings.Update(s => s.DefaultSendingAccountId = value ? Account.Id : null);
            DefaultSenderChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Lets the page refresh the other accounts' "default" switches (only one can be on).</summary>
    public event EventHandler? DefaultSenderChanged;

    public void RefreshDefaultSender() => OnPropertyChanged(nameof(IsDefaultSender));

    public bool HasConnectionProblem => !Account.IsDemo && _status(Account.Id).HasProblem;

    public string StatusText
    {
        get
        {
            if (Account.IsDemo) return "Demonstration account · nothing is sent or received";
            var status = _status(Account.Id);
            if (status.HasProblem)
            {
                return (status.RequiresSignIn ? "Sign-in needed: " : "Couldn't connect: ") + status.LastError;
            }

            return status.LastSyncedAt is { } at
                ? "Connected · last checked " + at.ToLocalTime().ToString("t", CultureInfo.CurrentCulture)
                : "Not checked yet since SidePhase Mail started";
        }
    }

    /// <summary>Mailbox storage from the server (only where the server reports it).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStorage), nameof(StorageText), nameof(StoragePercent))]
    public partial AccountStorage? Storage { get; set; }

    public bool HasStorage => Storage is not null;

    public string StorageText => Storage is { } s ? "Mailbox storage: " + s.Describe() : string.Empty;

    public double StoragePercent => (Storage?.Fraction ?? 0) * 100;

    public async Task LoadStorageAsync(Func<Guid, CancellationToken, Task<AccountStorage?>> load)
    {
        if (Account.IsDemo) return;
        try
        {
            Storage = await load(Account.Id, CancellationToken.None);
        }
        catch (Exception)
        {
            Storage = null;
        }
    }

    public void RefreshStatus()
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(HasConnectionProblem));
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

    public MailAccount Account { get; private set; }
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

/// <summary>One mail rule in Settings › Rules.</summary>
public sealed partial class RuleItemViewModel(Core.Organisation.MailRule rule, string description, SettingsViewModel owner) : ObservableObject
{
    public Core.Organisation.MailRule Rule { get; private set; } = rule;
    public string Name => Rule.Name;
    public string Description { get; } = description;

    public bool Enabled
    {
        get => Rule.Enabled;
        set
        {
            if (value == Rule.Enabled) return;
            Rule = Rule with { Enabled = value };
            _ = owner.SaveRuleAsync(Rule);
            OnPropertyChanged();
        }
    }

    [RelayCommand] private Task EditAsync() => owner.EditRuleAsync(this);
    [RelayCommand] private Task DeleteAsync() => owner.DeleteRuleAsync(this);
    [RelayCommand] private Task RunNowAsync() => owner.RunRuleNowAsync(this);
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
    private readonly LiveMailDataSource _live;
    private readonly Core.Spam.LocalSpamFilter _localFilter;
    private readonly Core.Organisation.MailRuleService _mailRules;
    private readonly Services.RuleEditor _ruleEditor;
    private readonly global::SidePhase.Mail.Advertising.AdService _ads;

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
        ISecretStore secrets,
        LiveMailDataSource live,
        Core.Spam.LocalSpamFilter localFilter,
        Core.Organisation.MailRuleService mailRules,
        Services.RuleEditor ruleEditor,
        global::SidePhase.Mail.Advertising.AdService ads)
    {
        _ads = ads;
        _ads.Changed += (_, _) => ui.Post(() =>
        {
            OnPropertyChanged(nameof(PreviewPromotionBanner));
            OnPropertyChanged(nameof(SimulateAdFree));
        });
        _mailRules = mailRules;
        _ruleEditor = ruleEditor;
        _mailRules.Changed += (_, _) => ui.Post(() => _ = LoadRulesAsync());
        _localFilter = localFilter;
        _localFilter.Changed += (_, _) => ui.Post(() => OnPropertyChanged(nameof(LocalFilterStatus)));
        _accounts = accounts;
        _live = live;
        _live.AccountStatusChanged += (_, id) => ui.Post(() =>
        {
            foreach (var account in Accounts.Where(a => a.Account.Id == id)) account.RefreshStatus();
        });
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

    // ---------------------------------------------------------------- Rules

    public ObservableCollection<RuleItemViewModel> Rules { get; } = [];

    [ObservableProperty] public partial bool HasNoRules { get; set; }

    public async Task LoadRulesAsync()
    {
        var folderNames = new Dictionary<Guid, string>();
        foreach (var account in await _mail.GetAccountsAsync())
        {
            foreach (var folder in await _mail.GetFoldersAsync(account.Id)) folderNames[folder.Id] = folder.Name;
        }

        Rules.Clear();
        foreach (var rule in await _mailRules.ListAsync()) Rules.Add(new RuleItemViewModel(rule, Services.RuleEditor.Describe(rule, folderNames), this));
        HasNoRules = Rules.Count == 0;
    }

    [RelayCommand]
    private Task AddRuleAsync() => _ruleEditor.EditAsync(null);

    internal Task EditRuleAsync(RuleItemViewModel item) => _ruleEditor.EditAsync(item.Rule);

    internal async Task SaveRuleAsync(Core.Organisation.MailRule rule)
    {
        try
        {
            await _mailRules.SaveAsync(rule);
        }
        catch (ArgumentException ex)
        {
            _toasts.Show(ex.Message, ToastKind.Warning);
        }
    }

    internal async Task DeleteRuleAsync(RuleItemViewModel item)
    {
        if (!await _dialogs.ConfirmAsync("Delete rule?", $"\"{item.Name}\" will stop acting on new mail. Messages it already handled aren't changed.", "Delete")) return;
        await _mailRules.DeleteAsync(item.Rule.Id);
    }

    /// <summary>Runs a rule on a folder's messages stored on this PC (each message at most once per rule).</summary>
    internal async Task RunRuleNowAsync(RuleItemViewModel item)
    {
        var folders = new List<(MailFolder Folder, MailAccount Account)>();
        foreach (var account in await _mail.GetAccountsAsync())
        {
            if (item.Rule.AccountId is { } only && only != account.Id) continue;
            foreach (var folder in await _mail.GetFoldersAsync(account.Id)) folders.Add((folder, account));
        }

        var combo = new Microsoft.UI.Xaml.Controls.ComboBox { Header = "Folder", HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Stretch, MinWidth = 300 };
        foreach (var (f, a) in folders) combo.Items.Add($"{a.DisplayName} · {f.Name}");
        combo.SelectedIndex = Math.Max(0, folders.FindIndex(f => f.Folder.Kind == FolderKind.Inbox));
        var panel = new Microsoft.UI.Xaml.Controls.StackPanel { Spacing = 10 };
        panel.Children.Add(combo);
        panel.Children.Add(new Microsoft.UI.Xaml.Controls.TextBlock
        {
            Text = "The rule runs on the messages in this folder that are stored on this PC. Messages it has already handled are skipped.",
            TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap,
            MaxWidth = 340,
            Opacity = 0.75,
        });
        var dialog = new Microsoft.UI.Xaml.Controls.ContentDialog
        {
            Title = $"Run \"{item.Name}\" now",
            Content = panel,
            PrimaryButtonText = "Run",
            CloseButtonText = "Cancel",
            DefaultButton = Microsoft.UI.Xaml.Controls.ContentDialogButton.Primary,
        };
        if (await _dialogs.ShowAsync(dialog) != Microsoft.UI.Xaml.Controls.ContentDialogResult.Primary || combo.SelectedIndex < 0) return;

        var target = folders[combo.SelectedIndex].Folder;
        var messages = (await _mail.GetMessagesAsync()).Where(m => m.FolderId == target.Id).ToList();
        _toasts.Show($"Running \"{item.Name}\" on {messages.Count} messages…");
        var result = await _mailRules.RunNowAsync(item.Rule with { Enabled = true }, messages);
        _toasts.Show(result.Problems.Count > 0
            ? result.Problems[0]
            : result.Matched == 0 ? "No messages matched (or they were already handled)." : $"\"{item.Name}\" handled {result.Matched} message{(result.Matched == 1 ? string.Empty : "s")}.",
            result.Problems.Count > 0 ? ToastKind.Warning : ToastKind.Success);
    }

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

    public bool LocalSpamLearning
    {
        get => S.LocalSpamLearning;
        set => Set(s => s.LocalSpamLearning = value, S.LocalSpamLearning != value);
    }

    public bool LocalFilterShowsJunk
    {
        get => S.LocalFilterShowsJunk;
        set => Set(s => s.LocalFilterShowsJunk = value, S.LocalFilterShowsJunk != value);
    }

    public string LocalFilterStatus => _localFilter.IsReady
        ? $"Learned from {_localFilter.SpamExamples} junk and {_localFilter.HamExamples} good messages you marked."
        : $"Learning: it gives an opinion after you've marked at least {Core.Spam.LocalSpamFilter.MinimumExamples} messages as Junk and {Core.Spam.LocalSpamFilter.MinimumExamples} as Not junk (so far {_localFilter.SpamExamples} and {_localFilter.HamExamples}).";

    [RelayCommand]
    private async Task ResetLocalFilterAsync()
    {
        var confirmed = await _dialogs.ConfirmAsync(
            "Reset the local spam filter?",
            "Everything it learned from the messages you marked is forgotten. Your messages, Junk folder and sender rules aren't changed.",
            "Reset");
        if (!confirmed) return;
        await _localFilter.ResetAsync();
        OnPropertyChanged(nameof(LocalFilterStatus));
        _toasts.Show("The local spam filter has been reset.", ToastKind.Success);
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

    private async Task<bool> SaveAppearanceAsync(AccountSettingsViewModel item, string name, string? color)
    {
        try
        {
            await _accounts.UpdateAppearanceAsync(item.Account.Id, name, color);
            return true;
        }
        catch (ArgumentException ex)
        {
            _toasts.Show(ex.Message, ToastKind.Warning);
            return false;
        }
        catch (Exception ex)
        {
            _toasts.Show("Couldn't update the account: " + ex.Message, ToastKind.Error);
            return false;
        }
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

    public string VersionText =>
        $"Version {AppIdentity.Version}" + (AppIdentity.SourceRevision is { } revision ? $" · build {revision}" : string.Empty) + " · beta";

    public string DistributionText =>
        AppIdentity.ChannelDescription + (AppIdentity.PackageVersion is { } package ? $" · package {package}" : string.Empty);

    /// <summary>Where this install keeps its data (shown so beta testers can find logs; nothing is uploaded).</summary>
    public string DataFolderText => _paths.Root;

    /// <summary>Opens the open-source licence notices shipped with the app (a plain text file).</summary>
    [RelayCommand]
    private async Task OpenLicencesAsync()
    {
        var file = Path.Combine(AppContext.BaseDirectory, "THIRD-PARTY-NOTICES.txt");
        try
        {
            if (!File.Exists(file))
            {
                _toasts.Show("The licence notices file is missing from this build.", ToastKind.Warning);
                return;
            }

            await Windows.System.Launcher.LaunchFileAsync(await Windows.Storage.StorageFile.GetFileFromPathAsync(file));
        }
        catch (Exception ex)
        {
            _toasts.Show("Couldn't open the licence notices: " + ex.Message, ToastKind.Error);
        }
    }

    // ---------------------------------------------------------------- Promotions

    /// <summary>Shows the built-in test banner in the footer (nothing is downloaded or reported). Off: approved bundled promotions only.</summary>
    public bool PreviewPromotionBanner
    {
        get => _ads.Settings.Mode == global::SidePhase.Mail.Advertising.AdvertisingMode.Demo;
        set
        {
            if (value == PreviewPromotionBanner) return;
            _ads.Update(a => a.Mode = value ? global::SidePhase.Mail.Advertising.AdvertisingMode.Demo : global::SidePhase.Mail.Advertising.AdvertisingMode.House);
        }
    }

    /// <summary>Acts as if the planned ad-free upgrade were owned (for testing; no purchase exists yet).</summary>
    public bool SimulateAdFree
    {
        get => _ads.Settings.SimulateAdFree;
        set
        {
            if (value == SimulateAdFree) return;
            _ads.Update(a => a.SimulateAdFree = value);
        }
    }

    public async Task LoadAsync()
    {
        Accounts.Clear();
        foreach (var account in await _mail.GetAccountsAsync())
        {
            var item = new AccountSettingsViewModel(account, _settings, ReauthenticateAccountAsync, RemoveAccountAsync, SaveAppearanceAsync, _live.GetAccountStatus);
            _ = item.LoadStorageAsync(_live.GetStorageAsync);
            item.DefaultSenderChanged += (_, _) =>
            {
                foreach (var other in Accounts) other.RefreshDefaultSender();
            };
            Accounts.Add(item);
        }

        Signatures.Clear();
        foreach (var signature in _settings.Current.SignatureLibrary) Signatures.Add(CreateSignatureItem(signature.Id));
        await LoadRulesAsync();

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
