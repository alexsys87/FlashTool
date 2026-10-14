using System.IO;
using System.Windows.Controls;
using FlashTool.App.Localization;
using Microsoft.Win32;
using Wpf.Ui;
using Wpf.Ui.Controls;

namespace FlashTool.App.Services;

/// <summary>Confirmation dialogs (Fluent ContentDialog) and file pickers.</summary>
public sealed class DialogService
{
    private readonly IContentDialogService _contentDialogs;

    public DialogService(IContentDialogService contentDialogs) => _contentDialogs = contentDialogs;

    public async Task<bool> ConfirmAsync(string title, string message, string primaryText, bool destructive = true, string? closeText = null)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new System.Windows.Controls.TextBlock { Text = message, TextWrapping = System.Windows.TextWrapping.Wrap, MaxWidth = 460 },
            PrimaryButtonText = primaryText,
            CloseButtonText = closeText ?? Loc.Get("Cancel"),
            DefaultButton = ContentDialogButton.Close,
            PrimaryButtonAppearance = destructive ? ControlAppearance.Danger : ControlAppearance.Primary,
        };

        var result = await _contentDialogs.ShowAsync(dialog, CancellationToken.None);
        return result == ContentDialogResult.Primary;
    }

    public string? PickImageFile(string? initialFolder)
    {
        var dialog = new OpenFileDialog
        {
            Title = Loc.Get("Dialog_SelectImage"),
            Filter = $"{Loc.Get("Dialog_ImageFilter")}|*.img;*.iso;*.bin;*.raw;*.dd;*.zip;*.xz;*.7z;*.gz|{Loc.Get("Dialog_AllFiles")}|*.*",
            CheckFileExists = true,
        };
        if (!string.IsNullOrEmpty(initialFolder) && Directory.Exists(initialFolder))
        {
            dialog.InitialDirectory = initialFolder;
        }

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? PickSaveFile(string? suggestedPath, string formatName, string extension)
    {
        var dialog = new SaveFileDialog
        {
            Title = Loc.Get("Dialog_SaveImage"),
            Filter = $"{formatName}|*{extension}|{Loc.Get("Dialog_AllFiles")}|*.*",
            OverwritePrompt = false,
            AddExtension = false,
        };

        if (!string.IsNullOrEmpty(suggestedPath))
        {
            var folder = Path.GetDirectoryName(suggestedPath);
            if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
            {
                dialog.InitialDirectory = folder;
            }

            dialog.FileName = Path.GetFileName(suggestedPath);
        }

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
}
