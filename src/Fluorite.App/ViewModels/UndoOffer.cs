using Microsoft.UI.Dispatching;

namespace Fluorite.App.ViewModels;

/// <summary>
/// "Removed “X”. Undo" for a few seconds after something was removed without asking first. Then (or when the bar is
/// closed, the next removal comes, or the app exits) the removal is final. The countdown waits while the pointer is
/// over the bar.
/// </summary>
public sealed partial class UndoOffer
{
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(10);

    /// <summary>Offers still open, so they stay alive after their page is gone, and so exiting can finish them.</summary>
    private static readonly List<UndoOffer> s_open = [];

    private readonly Func<Task> _undo;
    private readonly Action? _commit;
    private readonly DispatcherQueueTimer _timer;
    private bool _done;

    /// <param name="undo">Puts things back (on the UI thread).</param>
    /// <param name="commit">Makes it final; runs on a worker thread (on the UI thread when the app exits).</param>
    public UndoOffer(string message, Func<Task> undo, Action? commit = null)
    {
        Message = message;
        _undo = undo;
        _commit = commit;
        _timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _timer.Interval = Window;
        _timer.IsRepeating = false;
        _timer.Tick += (_, _) => _ = CommitAsync();
        _timer.Start();
        s_open.Add(this);
    }

    public string Message { get; }

    /// <summary>Undone or final: the bar goes away.</summary>
    public event EventHandler? Finished;

    public async Task UndoAsync()
    {
        if (Finish())
            await _undo();
    }

    public async Task CommitAsync()
    {
        if (Finish() && _commit is { } commit)
            await Task.Run(commit);
    }

    public void Pause() => _timer.Stop();

    /// <summary>A few seconds more after the pointer leaves the bar.</summary>
    public void Resume()
    {
        if (!_done)
        {
            _timer.Interval = TimeSpan.FromSeconds(4);
            _timer.Start();
        }
    }

    /// <summary>On exit: whatever could still be undone is final.</summary>
    public static void CommitAll()
    {
        foreach (var offer in s_open.ToList())
        {
            if (offer.Finish())
                offer._commit?.Invoke();
        }
    }

    private bool Finish()
    {
        if (_done)
            return false;
        _done = true;
        _timer.Stop();
        s_open.Remove(this);
        Finished?.Invoke(this, EventArgs.Empty);
        return true;
    }
}
