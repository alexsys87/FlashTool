using CommunityToolkit.Mvvm.ComponentModel;
using FlashTool.App.Localization;
using FlashTool.Core.Operations;
using Wpf.Ui.Controls;

namespace FlashTool.App.ViewModels;

/// <summary>Progress bar state of a running operation.</summary>
public sealed partial class ProgressViewModel : LocalizedViewModel
{
    private OperationProgress? _last;

    [ObservableProperty]
    private bool _isVisible;

    public string PhaseText => _last is { } p ? Formatting.Phase(p.Phase) : Formatting.Phase(OperationPhase.Preparing);

    public string Details => _last is { Phase: OperationPhase.Writing or OperationPhase.Reading or OperationPhase.Verifying } p ? Formatting.Progress(p) : string.Empty;

    public double Percent => (_last?.Fraction ?? 0) * 100;

    public string PercentText => _last is { Phase: OperationPhase.Writing or OperationPhase.Reading or OperationPhase.Verifying } p
        ? $"{p.Fraction * 100:0}%"
        : string.Empty;

    public bool IsIndeterminate => _last is null or { Phase: OperationPhase.Preparing or OperationPhase.Finalizing };

    public void Start()
    {
        _last = null;
        IsVisible = true;
        OnPropertyChanged(string.Empty);
    }

    public void Update(OperationProgress progress)
    {
        _last = progress;
        OnPropertyChanged(nameof(PhaseText));
        OnPropertyChanged(nameof(Details));
        OnPropertyChanged(nameof(Percent));
        OnPropertyChanged(nameof(PercentText));
        OnPropertyChanged(nameof(IsIndeterminate));
    }

    public void Stop() => IsVisible = false;
}

/// <summary>Outcome banner (InfoBar) of the last operation.</summary>
public sealed partial class ResultViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _isOpen;

    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private string _message = string.Empty;

    [ObservableProperty]
    private InfoBarSeverity _severity;

    public void Show(InfoBarSeverity severity, string title, string message)
    {
        Severity = severity;
        Title = title;
        Message = message;
        IsOpen = true;
    }

    public void Hide() => IsOpen = false;
}
