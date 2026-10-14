using System.Windows.Shell;
using CommunityToolkit.Mvvm.ComponentModel;

namespace FlashTool.App.Services;

/// <summary>
/// Global "one disk operation at a time" state. Also drives the taskbar progress.
/// </summary>
public sealed partial class OperationState : ObservableObject
{
    private CancellationTokenSource? _cts;
    private TaskCompletionSource? _finished;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private double _taskbarProgress;

    [ObservableProperty]
    private TaskbarItemProgressState _taskbarState = TaskbarItemProgressState.None;

    public CancellationToken Begin()
    {
        if (IsBusy)
        {
            throw new InvalidOperationException("An operation is already running.");
        }

        _cts = new CancellationTokenSource();
        _finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        IsBusy = true;
        TaskbarProgress = 0;
        TaskbarState = TaskbarItemProgressState.Indeterminate;
        return _cts.Token;
    }

    public void Report(double fraction)
    {
        TaskbarProgress = fraction;
        TaskbarState = TaskbarItemProgressState.Normal;
    }

    /// <summary>Shows the operation as paused (e.g. while an I/O error question is open).</summary>
    public void SetPaused(bool paused)
        => TaskbarState = paused ? TaskbarItemProgressState.Paused : TaskbarItemProgressState.Normal;

    public void End(bool failed)
    {
        IsBusy = false;
        TaskbarState = failed ? TaskbarItemProgressState.Error : TaskbarItemProgressState.None;
        TaskbarProgress = failed ? 1 : 0;
        _cts?.Dispose();
        _cts = null;
        _finished?.TrySetResult();
    }

    public void Cancel() => _cts?.Cancel();

    /// <summary>Cancels the running operation and waits until it has stopped.</summary>
    public Task CancelAndWaitAsync()
    {
        Cancel();
        return _finished?.Task ?? Task.CompletedTask;
    }
}
