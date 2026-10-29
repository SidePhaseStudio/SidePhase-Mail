namespace SidePhase.Mail.Advertising;

/// <summary>
/// Decides whether a sponsored card is shown, and which. Rules:
/// <list type="bullet">
/// <item>Nothing with the ad-free entitlement or with advertising Off; Production only when explicitly configured
/// (mode and an https:// endpoint in advertising.json).</item>
/// <item>No promotion available: nothing (the footer collapses).</item>
/// <item>A card stays for at least <see cref="AdvertisingPolicy.MinimumDisplayTime"/>; no rotation or animation.</item>
/// <item>The user can hide cards for the rest of the session.</item>
/// <item>Failures never surface: no card is shown instead.</item>
/// </list>
/// </summary>
public sealed class AdService
{
    private readonly AdvertisingSettingsStore _store;
    private readonly IAdEntitlement _entitlement;
    private readonly Func<AdvertisingSettings, IAdProvider?> _providerFor;
    private readonly Func<DateTimeOffset> _now;
    private readonly string _appVersion;
    private readonly string _language;
    private readonly Dictionary<AdPlacement, (SponsoredContent Card, DateTimeOffset Shown)> _current = [];
    private readonly Lock _gate = new();
    private AdvertisingSettings _settings;
    private readonly HousePromotionProvider? _house;
    private bool _hiddenForSession;

    public AdService(
        AdvertisingSettingsStore store,
        IAdEntitlement entitlement,
        string appVersion,
        string language,
        Func<AdvertisingSettings, IAdProvider?>? providerFor = null,
        Func<DateTimeOffset>? now = null,
        string? promotionsFile = null)
    {
        _house = promotionsFile is null ? null : new HousePromotionProvider(promotionsFile, now);
        _store = store;
        _entitlement = entitlement;
        _appVersion = appVersion;
        _language = language;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _providerFor = providerFor ?? DefaultProvider;
        _settings = store.Load();
        entitlement.Changed += (_, _) => Reset();
    }

    /// <summary>Raised when cards should be refreshed (settings, entitlement or "hide" changed).</summary>
    public event EventHandler? Changed;

    public AdvertisingSettings Settings
    {
        get { lock (_gate) return _settings; }
    }

    /// <summary>True if sponsored cards may appear at all right now.</summary>
    public bool IsActive
    {
        get
        {
            lock (_gate)
            {
                return !_hiddenForSession && !_entitlement.IsAdFree && EffectiveMode(_settings) != AdvertisingMode.Off;
            }
        }
    }

    /// <summary>Production without a usable https:// endpoint behaves like Off.</summary>
    public static AdvertisingMode EffectiveMode(AdvertisingSettings settings) =>
        settings.Mode == AdvertisingMode.Production && RemoteAdProvider.Create(Http, settings.Endpoint) is null
            ? AdvertisingMode.Off
            : settings.Mode;

    public async Task<SponsoredContent?> GetCardAsync(AdPlacement placement, CancellationToken cancellationToken = default)
    {
        IAdProvider? provider;
        lock (_gate)
        {
            if (!IsActive) return null;
            if (_current.TryGetValue(placement, out var shown) && _now() - shown.Shown < AdvertisingPolicy.MinimumDisplayTime) return shown.Card;
            provider = _providerFor(_settings);
        }

        if (provider is null) return null;
        SponsoredContent? card;
        try
        {
            card = await provider.GetAsync(new AdRequest(placement, _appVersion, _language), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            card = null;
        }

        if (card is null || !card.IsValid(out _)) return null;
        lock (_gate)
        {
            if (!IsActive) return null;
            _current[placement] = (card, _now());
        }

        return card;
    }

    /// <summary>Hides sponsored cards until the app is restarted.</summary>
    public void HideForSession()
    {
        lock (_gate) _hiddenForSession = true;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Update(Action<AdvertisingSettings> change)
    {
        lock (_gate)
        {
            var next = new AdvertisingSettings
            {
                Mode = _settings.Mode,
                Endpoint = _settings.Endpoint,
                SimulateAdFree = _settings.SimulateAdFree,
            };
            change(next);
            _settings = next;
            _current.Clear();
            try
            {
                _store.Save(next);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Applies for this session.
            }
        }

        if (_entitlement is LocalAdEntitlement local) local.NotifyChanged();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Reset()
    {
        lock (_gate) _current.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static readonly DemoAdProvider Demo = new();
    private static readonly HttpClient Http = new(new SocketsHttpHandler { UseCookies = false, AllowAutoRedirect = false });

    private IAdProvider? DefaultProvider(AdvertisingSettings settings) => EffectiveMode(settings) switch
    {
        AdvertisingMode.Demo => Demo,
        AdvertisingMode.House => _house,
        AdvertisingMode.Production => RemoteAdProvider.Create(Http, settings.Endpoint),
        _ => null,
    };
}
