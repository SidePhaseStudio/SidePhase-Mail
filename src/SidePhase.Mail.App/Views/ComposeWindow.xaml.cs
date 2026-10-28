using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using SidePhase.Mail.App.Helpers;
using SidePhase.Mail.App.Services;
using SidePhase.Mail.App.ViewModels;
using SidePhase.Mail.Core.Composition;
using SidePhase.Mail.Core.Contacts;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace SidePhase.Mail.App.Views;

/// <summary>The compose window: To/Cc/Bcc, subject, rich or plain editor, attachments and send.</summary>
public sealed partial class ComposeWindow : Window
{
    private bool _closeConfirmed;
    private bool _editorReady;
    private bool _closePromptOpen;

    public ComposeWindow(ComposeViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();

        Title = $"{viewModel.WindowTitle} — SidePhase Mail";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(ComposeTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        WindowHelper.SetIcon(this);
        WindowHelper.SizeAndCenter(this, 900, 760);
        WindowHelper.SetMinimumSize(this, 620, 520);
        App.GetService<ThemeService>().Register(this, RootGrid, SolidLayer);

        ViewModel.BodyProvider = GetBody;
        ViewModel.PickScheduleTime = async choice =>
            await App.GetService<MessageCommandService>().ResolveTimeAsync(choice, "Schedule send", "Schedule", root: RootGrid.XamlRoot);
        ViewModel.CloseRequested += (_, _) =>
        {
            _closeConfirmed = true;
            Close();
        };
        AppWindow.Closing += OnClosing;
        RootGrid.Loaded += OnLoaded;
    }

    public ComposeViewModel ViewModel { get; }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await ViewModel.InitializeAsync();

        var body = ViewModel.InitialBodyText.Replace("\r\n", "\r").Replace('\n', '\r');
        if (ViewModel.IsNewMessage && !ViewModel.BodyIncludesSignature && ViewModel.Signature.Length > 0)
        {
            body += "\r\r" + ViewModel.Signature.Replace("\r\n", "\r").Replace('\n', '\r');
        }

        RichEditor.Document.SetText(TextSetOptions.None, body);
        RichEditor.Document.Selection.SetRange(0, 0);
        PlainEditor.Text = body.Replace("\r", Environment.NewLine);
        _editorReady = true;
        ViewModel.IsDirty = false;

        if (string.IsNullOrWhiteSpace(ViewModel.To)) ToBox.Focus(FocusState.Programmatic);
        else RichEditor.Focus(FocusState.Programmatic);
    }

    private ComposeBody GetBody()
    {
        if (ViewModel.IsPlainText)
        {
            return new ComposeBody(PlainEditor.Text.Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd(), null);
        }

        RichEditor.Document.GetText(TextGetOptions.None, out var text);
        return new ComposeBody(text.Replace('\r', '\n').TrimEnd(), RichTextHtmlExporter.Export(RichEditor.Document));
    }

    /// <summary>Suggests contacts and recent recipients for the address being typed.</summary>
    private async void OnRecipientTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInputChanged) return;
        var text = sender.Text;
        var token = RecipientParser.LastToken(text);
        if (token.Length == 0)
        {
            sender.ItemsSource = null;
            return;
        }

        try
        {
            var already = RecipientParser.Parse(text).Addresses.Select(a => a.Address);
            var suggestions = await App.GetService<ContactService>().SuggestAsync(token, 8, already);
            if (sender.Text == text) sender.ItemsSource = suggestions.Select(s => new RecipientSuggestionItem(s)).ToList();
        }
        catch (Exception ex)
        {
            StartupDiagnostics.Log("recipient suggestions", ex);
        }
    }

    private void OnRecipientQuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        if (args.ChosenSuggestion is not RecipientSuggestionItem chosen) return;
        sender.Text = RecipientParser.ReplaceLastToken(sender.Text, chosen.Suggestion.Address);
        sender.ItemsSource = null;
    }

    /// <summary>Ctrl+Enter sends, after asking first if the user turned that on in Settings.</summary>
    private async void OnSendAcceleratorInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        if (!ViewModel.SendCommand.CanExecute(null)) return;
        if (App.GetService<Core.Abstractions.ISettingsService>().Current.ConfirmCtrlEnterSend)
        {
            var confirmed = await App.GetService<DialogService>().ConfirmAsync(
                "Send this message?", "You pressed Ctrl+Enter.", "Send", root: RootGrid.XamlRoot);
            if (!confirmed) return;
        }

        await ViewModel.SendCommand.ExecuteAsync(null);
    }

    private async void OnSaveAcceleratorInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        if (ViewModel.SaveDraftCommand.CanExecute(null)) await ViewModel.SaveDraftCommand.ExecuteAsync(null);
    }

    /// <summary>Esc closes the window (asking to save the draft if there are changes).</summary>
    private void OnEscapeInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        Close();
    }

    private void OnEditorTextChanged(object sender, RoutedEventArgs e)
    {
        if (_editorReady) ViewModel.IsDirty = true;
    }

    private void OnPlainTextToggled(object sender, RoutedEventArgs e)
    {
        if (!_editorReady) return;

        // Carry the text across when switching editors. Formatting is dropped when moving to plain text.
        if (ViewModel.IsPlainText)
        {
            RichEditor.Document.GetText(TextGetOptions.None, out var text);
            PlainEditor.Text = text.TrimEnd('\r').Replace("\r", Environment.NewLine);
        }
        else
        {
            RichEditor.Document.SetText(TextSetOptions.None, PlainEditor.Text.Replace("\r\n", "\r").Replace('\n', '\r'));
        }
    }

    private void OnBoldClick(object sender, RoutedEventArgs e) =>
        ApplyFormat(s => s.CharacterFormat.Bold = FormatEffect.Toggle);

    private void OnItalicClick(object sender, RoutedEventArgs e) =>
        ApplyFormat(s => s.CharacterFormat.Italic = FormatEffect.Toggle);

    private void OnUnderlineClick(object sender, RoutedEventArgs e) =>
        ApplyFormat(s => s.CharacterFormat.Underline =
            s.CharacterFormat.Underline == UnderlineType.None ? UnderlineType.Single : UnderlineType.None);

    private void OnBulletsClick(object sender, RoutedEventArgs e) =>
        ApplyFormat(s => s.ParagraphFormat.ListType =
            s.ParagraphFormat.ListType == MarkerType.Bullet ? MarkerType.None : MarkerType.Bullet);

    private void ApplyFormat(Action<ITextSelection> change)
    {
        var selection = RichEditor.Document.Selection;
        if (selection is null) return;
        change(selection);
        RichEditor.Focus(FocusState.Programmatic);
    }

    private async void OnAttachClick(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker { ViewMode = PickerViewMode.List };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var files = await picker.PickMultipleFilesAsync();
        if (files is { Count: > 0 }) await AddFilesAsync(files);
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        e.AcceptedOperation = DataPackageOperation.Copy;
        if (e.DragUIOverride is not null)
        {
            e.DragUIOverride.Caption = "Attach";
            e.DragUIOverride.IsGlyphVisible = true;
        }

        DropOverlay.Visibility = Visibility.Visible;
    }

    private void OnDragLeave(object sender, DragEventArgs e) => DropOverlay.Visibility = Visibility.Collapsed;

    private async void OnDrop(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var deferral = e.GetDeferral();
        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            await AddFilesAsync(items.OfType<StorageFile>());
        }
        finally
        {
            deferral.Complete();
        }
    }

    private async Task AddFilesAsync(IEnumerable<StorageFile> files)
    {
        var list = new List<(string, string, long, string)>();
        foreach (var file in files)
        {
            var properties = await file.GetBasicPropertiesAsync();
            var contentType = string.IsNullOrEmpty(file.ContentType) ? "application/octet-stream" : file.ContentType;
            list.Add((file.Path, file.Name, (long)properties.Size, contentType));
        }

        ViewModel.AddFiles(list);
    }

    private async void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_closeConfirmed || !ViewModel.IsDirty || ViewModel.IsSending) return;

        args.Cancel = true;
        if (_closePromptOpen) return;
        _closePromptOpen = true;
        try
        {
            var dialog = new ContentDialog
            {
                Title = "Save this message as a draft?",
                Content = "You can finish it later from Drafts.",
                PrimaryButtonText = "Save draft",
                SecondaryButtonText = "Discard",
                CloseButtonText = "Keep editing",
                DefaultButton = ContentDialogButton.Primary,
            };
            var result = await App.GetService<DialogService>().ShowAsync(dialog, RootGrid.XamlRoot);
            if (result == ContentDialogResult.Primary)
            {
                await ViewModel.SaveDraftCommand.ExecuteAsync(null);
                _closeConfirmed = !ViewModel.IsDirty;
            }
            else if (result == ContentDialogResult.Secondary)
            {
                _closeConfirmed = true;
            }
        }
        finally
        {
            _closePromptOpen = false;
        }

        if (_closeConfirmed) Close();
    }
}

/// <summary>A suggestion as shown in the recipient dropdown (ToString is what the list displays).</summary>
internal sealed record RecipientSuggestionItem(RecipientSuggestion Suggestion)
{
    public override string ToString() => Suggestion.IsContact ? Suggestion.Text : Suggestion.Text + "  · recent";
}
