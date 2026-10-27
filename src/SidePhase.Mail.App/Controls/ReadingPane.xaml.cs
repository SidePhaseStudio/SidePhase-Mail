using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using SidePhase.Mail.App.ViewModels;
using SidePhase.Mail.Core.Composition;

namespace SidePhase.Mail.App.Controls;

public sealed partial class ReadingPane : UserControl
{
    private Guid? _shownMessageId;

    public ReadingPane()
    {
        ViewModel = App.GetService<ReadingPaneViewModel>();
        InitializeComponent();

        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        BodyView.MailtoRequested += async (_, address) =>
        {
            var parsed = RecipientParser.Parse(address);
            if (parsed.Addresses.Count > 0) await ViewModel.ComposeToAsync(parsed.Addresses[0]);
        };
    }

    public ReadingPaneViewModel ViewModel { get; }

    public InfoBarSeverity SpamSeverity(bool severe) => severe ? InfoBarSeverity.Error : InfoBarSeverity.Warning;

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ReadingPaneViewModel.Message)) return;

        var id = ViewModel.Message?.Id;
        if (id is null || id == _shownMessageId)
        {
            _shownMessageId = id;
            return;
        }

        _shownMessageId = id;
        PlayEntrance();
    }

    /// <summary>A short, subtle fade-and-rise when a different message is opened.</summary>
    private void PlayEntrance()
    {
        var storyboard = new Storyboard();
        var fade = new DoubleAnimation
        {
            From = 0.0,
            To = 1.0,
            Duration = new Duration(TimeSpan.FromMilliseconds(180)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(fade, MessageView);
        Storyboard.SetTargetProperty(fade, "Opacity");
        storyboard.Children.Add(fade);
        storyboard.Begin();
    }
}
