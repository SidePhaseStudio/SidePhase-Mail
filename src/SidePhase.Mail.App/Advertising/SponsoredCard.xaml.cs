using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using SidePhase.Mail.Advertising;

namespace SidePhase.Mail.App.Advertising;

/// <summary>
/// Shows one sponsored card for <see cref="Placement"/> if the <see cref="AdService"/> has one (advertising on, not ad-free,
/// not hidden), otherwise nothing at all. Takes no input from the mail views: it knows only its placement.
/// </summary>
public sealed partial class SponsoredCard : UserControl
{
    private readonly AdService _ads;
    private SponsoredContent? _card;
    private int _loadVersion;

    public SponsoredCard()
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

    public AdPlacement Placement { get; set; } = AdPlacement.SidebarFooter;

    private void OnAdsChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(() => _ = LoadAsync());

    private async Task LoadAsync()
    {
        var version = ++_loadVersion;
        SponsoredContent? card = null;
        try
        {
            card = await _ads.GetCardAsync(Placement);
        }
        catch (Exception ex)
        {
            StartupDiagnostics.Log("sponsored card", ex);
        }

        if (version != _loadVersion) return; // a newer load started meanwhile
        _card = card;
        if (card is null)
        {
            Card.Visibility = Visibility.Collapsed;
            return;
        }

        HeadlineText.Text = card.Headline;
        BodyText.Text = card.Body;
        BodyText.Visibility = string.IsNullOrWhiteSpace(card.Body) ? Visibility.Collapsed : Visibility.Visible;
        AdvertiserText.Text = card.Advertiser;
        ActionButton.Content = card.CallToAction;
        ActionButton.Visibility = card.Destination is null ? Visibility.Collapsed : Visibility.Visible;
        TestPill.Visibility = card.IsTest ? Visibility.Visible : Visibility.Collapsed;
        TestExplanation.Visibility = TestPill.Visibility;
        AutomationProperties.SetName(Card, $"Sponsored{(card.IsTest ? " test content" : string.Empty)}: {card.Headline}, from {card.Advertiser}");
        Card.Visibility = Visibility.Visible;
    }

    private void OnHideClick(object sender, RoutedEventArgs e) => _ads.HideForSession();

    private async void OnActionClick(object sender, RoutedEventArgs e)
    {
        // Same confirmation as links in email: the user sees the destination before anything opens.
        if (_card?.Destination is { } destination)
            await App.GetService<Services.MessageCommandService>().OpenLinkAsync(destination);
    }
}
