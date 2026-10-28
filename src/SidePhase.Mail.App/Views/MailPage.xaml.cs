using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using SidePhase.Mail.App.ViewModels;
using SidePhase.Mail.Core.Abstractions;
using SidePhase.Mail.Core.Services;
using SidePhase.Mail.Core.Settings;

namespace SidePhase.Mail.App.Views;

/// <summary>The message list and reading pane.</summary>
public sealed partial class MailPage : Page
{
    private readonly ISettingsService _settings;
    private bool _syncingSelection;
    private int _lastSelectedIndex = -1;
    private ReadingPanePosition? _appliedPosition;

    public MailPage()
    {
        ViewModel = App.GetService<MailListViewModel>();
        _settings = App.GetService<ISettingsService>();
        InitializeComponent();

        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        _settings.SettingsChanged += (_, e) => DispatcherQueue.TryEnqueue(() => ApplyLayout(e.Settings.ReadingPane));
        ApplyLayout(_settings.Current.ReadingPane);
    }

    public MailListViewModel ViewModel { get; }

    /// <summary>Fills the advanced search form from whatever is in the search box.</summary>
    private void OnAdvancedSearchOpening(object? sender, object e)
    {
        var form = AdvancedSearch.FromQuery(ViewModel.SearchText);
        AdvWords.Text = form.Words;
        AdvFrom.Text = form.From;
        AdvTo.Text = form.To;
        AdvSubject.Text = form.Subject;
        AdvBody.Text = form.Body;

        AdvAccount.Items.Clear();
        AdvAccount.Items.Add("Any account");
        foreach (var name in ViewModel.AccountNames) AdvAccount.Items.Add(name);
        var accountIndex = ViewModel.AccountNames.ToList().FindIndex(n => n.Equals(form.Account, StringComparison.CurrentCultureIgnoreCase));
        AdvAccount.SelectedIndex = accountIndex + 1;

        AdvIn.SelectedIndex = Math.Max(0, AdvIn.Items.OfType<ComboBoxItem>().ToList().FindIndex(i => string.Equals(i.Tag as string, form.In, StringComparison.OrdinalIgnoreCase)));
        AdvAfter.Date = form.After;
        AdvBefore.Date = form.Before;
        AdvAttachment.IsChecked = form.HasAttachment;
        AdvUnread.IsChecked = form.Unread;
        AdvFavourite.IsChecked = form.Favourite;
        AdvPinned.IsChecked = form.Pinned;
        AdvWhere.SelectedIndex = ViewModel.SearchAllMail ? 1 : 0;
    }

    private void OnAdvancedSearchGo(object sender, RoutedEventArgs e)
    {
        var previous = AdvancedSearch.FromQuery(ViewModel.SearchText);
        var form = previous with
        {
            Words = AdvWords.Text,
            From = AdvFrom.Text,
            To = AdvTo.Text,
            Subject = AdvSubject.Text,
            Body = AdvBody.Text,
            Account = AdvAccount.SelectedIndex > 0 ? ViewModel.AccountNames[AdvAccount.SelectedIndex - 1] : string.Empty,
            In = (AdvIn.SelectedItem as ComboBoxItem)?.Tag as string ?? string.Empty,
            After = AdvAfter.Date?.Date,
            Before = AdvBefore.Date?.Date,
            HasAttachment = AdvAttachment.IsChecked == true,
            Unread = AdvUnread.IsChecked == true,
            Favourite = AdvFavourite.IsChecked == true,
            Pinned = AdvPinned.IsChecked == true,
        };

        ViewModel.SearchAllMail = AdvWhere.SelectedIndex == 1;
        ViewModel.SearchText = form.ToQuery();
        AdvancedSearchFlyout.Hide();
    }

    private void OnAdvancedSearchClear(object sender, RoutedEventArgs e)
    {
        ViewModel.SearchText = string.Empty;
        ViewModel.SearchAllMail = false;
        AdvancedSearchFlyout.Hide();
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingSelection) return;

        var selected = MessageList.SelectedItem;
        if (selected is ListHeaderViewModel)
        {
            // Section headers aren't selectable: move on to the nearest message in the direction of travel.
            var index = MessageList.SelectedIndex;
            var forward = index >= _lastSelectedIndex;
            var target = ViewModel.NearestMessage(index, forward);
            DispatcherQueue.TryEnqueue(() =>
            {
                _syncingSelection = true;
                MessageList.SelectedItem = target;
                _syncingSelection = false;
                ViewModel.SelectedItem = target;
            });
            return;
        }

        _lastSelectedIndex = MessageList.SelectedIndex;
        ViewModel.SelectedItem = selected as MessageItemViewModel;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MailListViewModel.SelectedItem):
                if (!ReferenceEquals(MessageList.SelectedItem, ViewModel.SelectedItem))
                {
                    _syncingSelection = true;
                    MessageList.SelectedItem = ViewModel.SelectedItem;
                    _syncingSelection = false;
                    if (ViewModel.SelectedItem is not null) MessageList.ScrollIntoView(ViewModel.SelectedItem);
                }

                _lastSelectedIndex = MessageList.SelectedIndex;
                break;

            case nameof(MailListViewModel.Filter):
                var radio = ViewModel.Filter switch
                {
                    MessageFilter.Unread => FilterUnread,
                    MessageFilter.Starred => FilterStarred,
                    MessageFilter.WithAttachments => FilterAttachments,
                    _ => FilterAll,
                };
                radio.IsChecked = true;
                break;
        }
    }

    private void ApplyLayout(ReadingPanePosition position)
    {
        if (_appliedPosition == position) return;
        _appliedPosition = position;

        if (position == ReadingPanePosition.Bottom)
        {
            ListColumn.Width = new GridLength(1, GridUnitType.Star);
            ReaderColumn.Width = new GridLength(0);
            ListRow.Height = new GridLength(1, GridUnitType.Star);
            ReaderRow.Height = new GridLength(1.3, GridUnitType.Star);

            Grid.SetColumnSpan(ListPane, 2);
            Grid.SetRow(ReaderHost, 1);
            Grid.SetColumn(ReaderHost, 0);
            Grid.SetColumnSpan(ReaderHost, 2);
            ReaderHost.Margin = new Thickness(0, 6, 0, 0);
        }
        else
        {
            ListColumn.Width = new GridLength(400);
            ReaderColumn.Width = new GridLength(1, GridUnitType.Star);
            ListRow.Height = new GridLength(1, GridUnitType.Star);
            ReaderRow.Height = new GridLength(0);

            Grid.SetColumnSpan(ListPane, 1);
            Grid.SetRow(ReaderHost, 0);
            Grid.SetColumn(ReaderHost, 1);
            Grid.SetColumnSpan(ReaderHost, 1);
            ReaderHost.Margin = new Thickness(6, 0, 0, 0);
        }
    }

    private void OnComposeAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        ViewModel.ComposeCommand.Execute(null);
    }

    private void OnReplyAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        if (ViewModel.Reading.CanReply) ViewModel.Reading.ReplyCommand.Execute(null);
    }

    private void OnReplyAllAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        if (ViewModel.Reading.CanReply) ViewModel.Reading.ReplyAllCommand.Execute(null);
    }

    private void OnForwardAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        if (ViewModel.Reading.CanReply) ViewModel.Reading.ForwardCommand.Execute(null);
    }

    private void OnSearchAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        SearchBox.Focus(FocusState.Keyboard);
    }

    private void OnSyncAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        ViewModel.SyncCommand.Execute(null);
    }

    private void OnDeleteAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        if (ViewModel.Reading.HasMessage) ViewModel.Reading.DeleteCommand.Execute(null);
    }

    private void OnArchiveAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        if (ViewModel.Reading.HasMessage) ViewModel.Reading.ArchiveCommand.Execute(null);
    }

    private void OnMarkReadAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        if (ViewModel.Reading.Message is { IsRead: false }) ViewModel.Reading.ToggleReadCommand.Execute(null);
    }

    private void OnMarkUnreadAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        if (ViewModel.Reading.Message is { IsRead: true }) ViewModel.Reading.ToggleReadCommand.Execute(null);
    }

    private void OnPinAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        if (ViewModel.Reading.HasMessage) ViewModel.Reading.TogglePinCommand.Execute(null);
    }

    private void OnStarAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        if (ViewModel.Reading.HasMessage) ViewModel.Reading.ToggleStarCommand.Execute(null);
    }
}
