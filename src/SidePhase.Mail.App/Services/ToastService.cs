namespace SidePhase.Mail.App.Services;

public enum ToastKind
{
    Info,
    Success,
    Warning,
    Error,
}

public sealed record ToastRequest(string Message, ToastKind Kind, string? ActionText, Func<Task>? Action, TimeSpan Duration);

/// <summary>Short, non-blocking messages shown at the bottom of the main window, optionally with an Undo action.</summary>
public sealed class ToastService
{
    public event EventHandler<ToastRequest>? ToastRequested;

    public void Show(string message, ToastKind kind = ToastKind.Info) =>
        ToastRequested?.Invoke(this, new ToastRequest(message, kind, null, null, TimeSpan.FromSeconds(5)));

    public void ShowWithAction(string message, ToastKind kind, string actionText, Func<Task> action) =>
        ToastRequested?.Invoke(this, new ToastRequest(message, kind, actionText, action, TimeSpan.FromSeconds(10)));

    public void ShowWithUndo(string message, Func<Task> undo) =>
        ToastRequested?.Invoke(this, new ToastRequest(message, ToastKind.Success, "Undo", undo, TimeSpan.FromSeconds(8)));
}
