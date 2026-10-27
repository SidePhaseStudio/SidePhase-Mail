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
}
