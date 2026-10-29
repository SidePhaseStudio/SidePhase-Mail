using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using SidePhase.Mail.App.Controls;
using SidePhase.Mail.App.Services;
using SidePhase.Mail.App.ViewModels;
using SidePhase.Mail.Core.Abstractions;
using SidePhase.Mail.Core.Models;

namespace SidePhase.Mail.App.Views;

/// <summary>
/// One message (or conversation) in its own window, with its own reading-pane view model. It follows changes to the
/// message (read, star, pin) and closes itself when the message is no longer there (deleted or moved away).
/// </summary>
public sealed partial class MessageWindow : Window
{
    private readonly ReadingPaneViewModel _viewModel;
    private readonly IMailDataSource _mail;
    private readonly Guid _messageId;
    private bool _checking;

    public MessageWindow(ReadingPaneViewModel viewModel, MessageSummary message, IReadOnlyList<MessageSummary>? thread)
    {
        _viewModel = viewModel;
        _mail = App.GetService<IMailDataSource>();
        _messageId = message.Id;

        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(MessageTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        WindowHelper.SetIcon(this);
        WindowHelper.SizeAndCenter(this, 880, 780);
        WindowHelper.SetMinimumSize(this, 480, 420);
        App.GetService<ThemeService>().Register(this, RootGrid, SolidLayer);

        Title = MessageTitleBar.Title = string.IsNullOrWhiteSpace(message.Subject) ? "(no subject)" : message.Subject;
        PaneHost.Children.Add(new ReadingPane(viewModel));

        _mail.DataChanged += OnDataChanged;
        Closed += (_, _) =>
        {
            _mail.DataChanged -= OnDataChanged;
            _viewModel.Detach();
        };

        _ = _viewModel.ShowAsync(message, thread);
    }

    private void OnDataChanged(object? sender, MailDataChangedEventArgs e) => DispatcherQueue.TryEnqueue(async () =>
    {
        if (_checking) return;
        _checking = true;
        try
        {
            var current = await _mail.GetMessageAsync(_messageId);
            if (current is null)
            {
                Close();
                return;
            }

            _viewModel.UpdateSummary(current);
        }
        catch (Exception ex)
        {
            StartupDiagnostics.Log("message window", ex);
        }
        finally
        {
            _checking = false;
        }
    });
}
