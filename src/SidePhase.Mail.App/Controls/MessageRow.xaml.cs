using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using SidePhase.Mail.App.ViewModels;

namespace SidePhase.Mail.App.Controls;

/// <summary>A message in the list. Quick actions fade in on hover; the full action set is on right-click.</summary>
public sealed partial class MessageRow : UserControl
{
    public static readonly DependencyProperty ItemProperty =
        DependencyProperty.Register(nameof(Item), typeof(MessageItemViewModel), typeof(MessageRow),
            new PropertyMetadata(null, (d, _) => ((MessageRow)d).IsHovered = false));

    public static readonly DependencyProperty IsHoveredProperty =
        DependencyProperty.Register(nameof(IsHovered), typeof(bool), typeof(MessageRow), new PropertyMetadata(false));

    public MessageRow()
    {
        InitializeComponent();
    }

    public MessageItemViewModel Item
    {
        get => (MessageItemViewModel)GetValue(ItemProperty);
        set => SetValue(ItemProperty, value);
    }

    /// <summary>True while the pointer is over the row (recycled containers reset it).</summary>
    public bool IsHovered
    {
        get => (bool)GetValue(IsHoveredProperty);
        set => SetValue(IsHoveredProperty, value);
    }

    private void OnPointerEntered(object sender, PointerRoutedEventArgs e) => IsHovered = true;

    private void OnPointerExited(object sender, PointerRoutedEventArgs e) => IsHovered = false;
}
