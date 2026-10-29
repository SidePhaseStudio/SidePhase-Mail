using Microsoft.Extensions.DependencyInjection;
using SidePhase.Mail.App.ViewModels;
using SidePhase.Mail.App.Views;
using SidePhase.Mail.Core.Models;

namespace SidePhase.Mail.App.Services;

/// <summary>Opens secondary windows (compose) with view models resolved from the container.</summary>
public sealed class WindowService(IServiceProvider services)
{
    public void OpenCompose(DraftMessage draft)
    {
        var viewModel = ActivatorUtilities.CreateInstance<ComposeViewModel>(services, draft);
        var window = new ComposeWindow(viewModel);
        window.Activate();
    }

    /// <summary>Opens a message (or conversation) in its own window.</summary>
    public void OpenMessage(MessageSummary message, IReadOnlyList<MessageSummary>? thread)
    {
        var viewModel = ActivatorUtilities.CreateInstance<ReadingPaneViewModel>(services);
        var window = new MessageWindow(viewModel, message, thread);
        window.Activate();

        // The click that opened it (a double-click in the list) finishes after this and would hand focus back to the
        // main window, leaving the new window behind it: bring it to the front once that input has been processed.
        window.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => WindowHelper.BringToFront(window));
    }
}
