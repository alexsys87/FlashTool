using System.Windows;
using FlashTool.App.Localization;
using FlashTool.Core.IO;

namespace FlashTool.App.Services;

/// <summary>
/// Retry policy for device I/O used by all operations: automatic retries of
/// the same request, then a "Retry / Cancel operation" question to the user.
/// </summary>
public sealed class IoErrorPrompt
{
    private readonly DialogService _dialogs;
    private readonly OperationState _operation;

    public IoErrorPrompt(DialogService dialogs, OperationState operation)
    {
        _dialogs = dialogs;
        _operation = operation;
        Policy = IoRetryPolicy.Default with { AskUser = AskAsync };
    }

    public IoRetryPolicy Policy { get; }

    /// <summary>Called from a worker thread; shows the question on the UI thread and waits for the answer.</summary>
    private Task<bool> AskAsync(IoErrorContext context, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromResult(false);
        }

        return Application.Current.Dispatcher.InvokeAsync(async () =>
        {
            _operation.SetPaused(true);
            try
            {
                var write = context.Kind == IoOperationKind.Write;
                var message = Loc.Format(
                    "IoError_Message",
                    Loc.Get(write ? "IoError_VerbWrite" : "IoError_VerbRead"),
                    Formatting.Size(context.Length),
                    $"0x{context.Offset:X} ({Formatting.Size(context.Offset)})",
                    context.Attempts,
                    context.Error.Message);
                return await _dialogs.ConfirmAsync(
                    Loc.Get(write ? "IoError_WriteTitle" : "IoError_ReadTitle"),
                    message,
                    Loc.Get("IoError_Retry"),
                    destructive: false,
                    closeText: Loc.Get("IoError_Abort"));
            }
            finally
            {
                _operation.SetPaused(false);
            }
        }).Task.Unwrap();
    }
}
