using Microsoft.UI.Dispatching;

namespace SidePhase.Mail.App.Services;

/// <summary>Marshals work onto the UI thread. Data sources may raise events from background threads.</summary>
public interface IUiDispatcher
{
    void Post(Action action);
}

public sealed class UiDispatcher : IUiDispatcher
{
    private readonly DispatcherQueue _queue = DispatcherQueue.GetForCurrentThread()
        ?? throw new InvalidOperationException("UiDispatcher must be created on the UI thread.");

    public void Post(Action action)
    {
        if (_queue.HasThreadAccess) action();
        else _queue.TryEnqueue(() => action());
    }
}
