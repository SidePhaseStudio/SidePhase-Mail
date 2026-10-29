using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using SidePhase.Mail.Advertising;

namespace SidePhase.Mail.App.Advertising;

/// <summary>
/// The footer banner. Asks <see cref="AdService"/> for a promotion and shows it, or collapses entirely when there is
/// none (advertising off, ad-free, hidden, nothing approved) or when the window is too short to spare the space. Knows
/// nothing about mail: it is given no messages, accounts or addresses.
/// </summary>
public sealed partial class PromotionFooter : UserControl
{
    /// <summary>Below this window height (effective pixels) the footer stays collapsed so mail keeps the space.</summary>
    public const double MinimumWindowHeight = 680;

    private readonly AdService _ads;
    private SponsoredContent? _card;
    private int _loadVersion;
    private bool _roomForBanner = true;

    public PromotionFooter()
    {
        _ads = App.GetService<AdService>();
        InitializeComponent();
        Loaded += (_, _) =>
        {
            _ads.Changed += OnAdsChanged;
            _ = LoadAsync();
        };
        Unloaded += (_, _) => _ads.Changed -= OnAdsChanged;
    }

    /// <summary>Called by the window when it's resized: no banner in short windows.</summary>
    public void UpdateAvailableSpace(double windowHeight)
    {
        var room = windowHeight >= MinimumWindowHeight;
        if (room == _roomForBanner) return;
        _roomForBanner = room;
        ApplyVisibility();
    }

    private void OnAdsChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(() => _ = LoadAsync());

    private async Task LoadAsync()
    {
        var version = ++_loadVersion;
        SponsoredContent? card = null;
        try
        {
            card = await _ads.GetCardAsync(AdPlacement.Footer);
        }
        catch (Exception ex)
        {
            StartupDiagnostics.Log("promotion banner", ex);
        }

        if (version != _loadVersion) return; // a newer load started meanwhile
        _card = card;
        if (card is not null) Show(card);
        ApplyVisibility();
    }

    private void ApplyVisibility() => Visibility = _card is not null && _roomForBanner ? Visibility.Visible : Visibility.Collapsed;

    private void Show(SponsoredContent card)
    {
        LabelText.Text = card.IsTest ? "Promotion · Test content" : "Promotion";
        TestExplanation.Visibility = card.IsTest ? Visibility.Visible : Visibility.Collapsed;

        var image = card.Image is { } name ? Path.Combine(AppContext.BaseDirectory, "Assets", "Promotions", name) : null;
        if (image is not null && File.Exists(image))
        {
            BannerImage.Source = new BitmapImage(new Uri(image));
            AutomationProperties.SetName(ImageButton, $"{card.Headline}. {card.CallToAction}");
            ImageButton.IsEnabled = card.Destination is not null;
            ImageButton.Visibility = Visibility.Visible;
            TextLayout.Visibility = Visibility.Collapsed;
        }
        else
        {
            AdvertiserText.Text = card.Advertiser;
            HeadlineText.Text = card.Headline;
            BodyText.Text = card.Body;
            BodyText.Visibility = string.IsNullOrWhiteSpace(card.Body) ? Visibility.Collapsed : Visibility.Visible;
            ActionButton.Content = card.CallToAction;
            ActionButton.Visibility = card.Destination is null ? Visibility.Collapsed : Visibility.Visible;
            ImageButton.Visibility = Visibility.Collapsed;
            TextLayout.Visibility = Visibility.Visible;
        }

        AutomationProperties.SetName(Banner, $"Promotion{(card.IsTest ? ", test content" : string.Empty)}: {card.Headline}, from {card.Advertiser}");
    }

    private void OnHideClick(object sender, RoutedEventArgs e) => _ads.HideForSession();

    private async void OnActionClick(object sender, RoutedEventArgs e)
    {
        // Same confirmation as links in email: the destination is shown before anything opens.
        if (_card?.Destination is { } destination)
            await App.GetService<Services.MessageCommandService>().OpenLinkAsync(destination);
    }
}
