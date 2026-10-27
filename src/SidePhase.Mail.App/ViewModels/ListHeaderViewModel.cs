using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SidePhase.Mail.Core.Services;

namespace SidePhase.Mail.App.ViewModels;

/// <summary>A section header in the message list ("Pinned", "Today", ...).</summary>
public sealed partial class ListHeaderViewModel : ObservableObject
{
    private readonly Action<ListHeaderViewModel>? _toggle;

    public ListHeaderViewModel(MessageSectionKind kind, Action<ListHeaderViewModel>? toggle)
    {
        Kind = kind;
        _toggle = toggle;
        Title = MessageListBuilder.TitleFor(kind);
    }

    public MessageSectionKind Kind { get; }

    public bool IsCollapsible => Kind == MessageSectionKind.Pinned;

    public bool IsPinnedSection => Kind == MessageSectionKind.Pinned;

    [ObservableProperty]
    public partial string Title { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CountText))]
    public partial int Count { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ChevronAngle), nameof(ToggleLabel))]
    public partial bool IsCollapsed { get; set; }

    public string CountText => Count.ToString(System.Globalization.CultureInfo.CurrentCulture);

    /// <summary>Rotation of the chevron: pointing down when expanded, right when collapsed.</summary>
    public double ChevronAngle => IsCollapsed ? -90 : 0;

    public string ToggleLabel => IsCollapsed ? $"Expand {Title}" : $"Collapse {Title}";

    [RelayCommand]
    private void Toggle() => _toggle?.Invoke(this);
}
