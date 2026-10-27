using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SidePhase.Mail.App.ViewModels;

namespace SidePhase.Mail.App.Views;

public sealed partial class MessageListTemplateSelector : DataTemplateSelector
{
    public DataTemplate? MessageTemplate { get; set; }
    public DataTemplate? HeaderTemplate { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item) =>
        item is ListHeaderViewModel ? HeaderTemplate : MessageTemplate;

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container) => SelectTemplateCore(item);
}

public sealed partial class MessageListContainerStyleSelector : StyleSelector
{
    public Style? MessageStyle { get; set; }
    public Style? HeaderStyle { get; set; }

    protected override Style? SelectStyleCore(object item, DependencyObject container) =>
        item is ListHeaderViewModel ? HeaderStyle : MessageStyle;
}
