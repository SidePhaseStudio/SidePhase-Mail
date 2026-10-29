namespace SidePhase.Mail.App.Services;

/// <summary>
/// Messages the user has just deleted, archived or moved, hidden from the message list straight away while the server
/// does the work (which can take a second or two). When the action finishes they're shown again: moved messages are
/// no longer in the view by then, and any the server refused come back, with its error on screen.
/// </summary>
public sealed class PendingRemovals
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, int> _hidden = [];

    /// <summary>Raised when the set of hidden messages changes.</summary>
    public event EventHandler? Changed;

    /// <summary>Hides the messages until the returned handle is disposed.</summary>
    public IDisposable Hide(IEnumerable<Guid> messageIds)
    {
        var ids = messageIds.Distinct().ToList();
        if (ids.Count == 0) return new Release(() => { });
        lock (_gate)
        {
            foreach (var id in ids) _hidden[id] = _hidden.GetValueOrDefault(id) + 1;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return new Release(() =>
        {
            lock (_gate)
            {
                foreach (var id in ids)
                {
                    if (_hidden.TryGetValue(id, out var count) && count > 1) _hidden[id] = count - 1;
                    else _hidden.Remove(id);
                }
            }

            Changed?.Invoke(this, EventArgs.Empty);
        });
    }

    public IReadOnlySet<Guid> Snapshot()
    {
        lock (_gate) return _hidden.Keys.ToHashSet();
    }

    private sealed class Release(Action release) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0) release();
        }
    }
}
