using System.ComponentModel;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FlashTool.App.Localization;
using FlashTool.App.Services;
using FlashTool.Core.Containers;

namespace FlashTool.App.ViewModels;

public sealed partial class SettingsPageViewModel : LocalizedViewModel
{
    private readonly SettingsService _settings;
    private readonly AppearanceService _appearance;
    private readonly ContainerRegistry _registry;
    private readonly EraseFeature _erase;
    private readonly FreeBlockSkipping _freeBlocks;
    private readonly BmapFeature _bmap;
    private readonly DeviceTestFeature _deviceTest;
    private readonly DifferentialWriteFeature _differential;
    private readonly DialogService _dialogs;
    private readonly OperationState _operation;
    private readonly UpdateService _updates;

    [ObservableProperty]
    private LocalizedOption<ThemeMode> _selectedTheme;

    [ObservableProperty]
    private LocalizedOption<LanguageMode> _selectedLanguage;

    public SettingsPageViewModel(
        SettingsService settings,
        AppearanceService appearance,
        ContainerRegistry registry,
        DeviceListService devices,
        EraseFeature erase,
        FreeBlockSkipping freeBlocks,
        BmapFeature bmap,
        DeviceTestFeature deviceTest,
        DifferentialWriteFeature differential,
        DialogService dialogs,
        OperationState operation,
        UpdateService updates)
    {
        _settings = settings;
        _appearance = appearance;
        _registry = registry;
        _erase = erase;
        _freeBlocks = freeBlocks;
        _bmap = bmap;
        _deviceTest = deviceTest;
        _differential = differential;
        _dialogs = dialogs;
        _operation = operation;
        _updates = updates;
        Updates = updates;
        Devices = devices;
        operation.PropertyChanged += OnOperationChanged;
        updates.PropertyChanged += OnUpdatesChanged;

        Themes = Enum.GetValues<ThemeMode>().Select(t => new LocalizedOption<ThemeMode>(t, $"Theme_{t}")).ToList();
        Languages =
        [
            new LocalizedOption<LanguageMode>(LanguageMode.System, "Language_System"),
            new LocalizedOption<LanguageMode>(LanguageMode.English, "Language_English"),
            new LocalizedOption<LanguageMode>(LanguageMode.Russian, "Language_Russian"),
        ];

        _selectedTheme = Themes.First(t => t.Value == settings.Settings.Theme);
        _selectedLanguage = Languages.First(l => l.Value == settings.Settings.Language);
    }

    public DeviceListService Devices { get; }

    public UpdateService Updates { get; }

    public IReadOnlyList<LocalizedOption<ThemeMode>> Themes { get; }

    public IReadOnlyList<LocalizedOption<LanguageMode>> Languages { get; }

    public bool SkipFreeBlocks
    {
        get => _freeBlocks.IsEnabled;
        set
        {
            _freeBlocks.IsEnabled = value;
            OnPropertyChanged();
        }
    }

    public bool DifferentialWrite
    {
        get => _differential.IsEnabled;
        set
        {
            _differential.IsEnabled = value;
            OnPropertyChanged();
        }
    }

    public bool DeviceTestEnabled
    {
        get => _deviceTest.IsEnabled;
        set
        {
            _deviceTest.IsEnabled = value;
            OnPropertyChanged();
        }
    }

    public bool UseBmap
    {
        get => _bmap.UseBmap;
        set
        {
            _bmap.UseBmap = value;
            OnPropertyChanged();
        }
    }

    public bool CreateBmap
    {
        get => _bmap.CreateBmap;
        set
        {
            _bmap.CreateBmap = value;
            OnPropertyChanged();
        }
    }

    /// <summary>State of the switch; it is changed only through <see cref="ToggleEraseCommand"/>.</summary>
    public bool EraseEnabled => _erase.IsEnabled;

    public string VersionText => Loc.Format(
        "Settings_Version",
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?",
        RuntimeInformation.FrameworkDescription.Replace(".NET ", string.Empty, StringComparison.Ordinal));

    public static bool IsAdministrator
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    public string AdminText => Loc.Get(IsAdministrator ? "Settings_Admin" : "Settings_NotAdmin");

    public string FormatsText => Loc.Format(
        "Settings_Formats",
        string.Join(", ", _registry.Handlers.Where(h => h.IsAvailable).Select(h => h.Format == ContainerFormat.Raw ? "img/iso" : h.Extensions[0].TrimStart('.'))));

    private bool CanToggleErase() => !_operation.IsBusy;

    private bool CanCheckForUpdates() => !_updates.IsBusy;

    private bool CanDownloadUpdate() => _updates.CanDownload;

    private bool CanInstallUpdate() => _updates.HasDownload && !_operation.IsBusy;

    [RelayCommand(CanExecute = nameof(CanCheckForUpdates))]
    private Task CheckForUpdatesAsync() => _updates.CheckAsync();

    [RelayCommand(CanExecute = nameof(CanDownloadUpdate))]
    private Task DownloadUpdateAsync() => _updates.DownloadAsync();

    [RelayCommand]
    private void CancelUpdate() => _updates.Cancel();

    [RelayCommand]
    private void OpenReleasePage() => _updates.OpenReleasePage();

    [RelayCommand]
    private void ShowUpdateInFolder() => _updates.ShowDownloadInFolder();

    /// <summary>Runs the downloaded MSI and closes FlashTool so the installer can replace it.</summary>
    [RelayCommand(CanExecute = nameof(CanInstallUpdate))]
    private async Task InstallUpdateAsync()
    {
        if (await _dialogs.ConfirmAsync(Loc.Get("Update_InstallConfirmTitle"), Loc.Get("Update_InstallConfirmMessage"), Loc.Get("Update_Install"), destructive: false)
            && _updates.StartInstaller())
        {
            System.Windows.Application.Current.Shutdown();
        }
    }

    private void OnUpdatesChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(UpdateService.State) or nameof(UpdateService.LastCheck) or nameof(UpdateService.Download))
        {
            CheckForUpdatesCommand.NotifyCanExecuteChanged();
            DownloadUpdateCommand.NotifyCanExecuteChanged();
            InstallUpdateCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>Turning the "Erase" page on needs a confirmation; turning it off does not.</summary>
    [RelayCommand(CanExecute = nameof(CanToggleErase))]
    private async Task ToggleEraseAsync()
    {
        _erase.IsEnabled = !_erase.IsEnabled
            && await _dialogs.ConfirmAsync(Loc.Get("Settings_EraseConfirmTitle"), Loc.Get("Settings_EraseConfirmMessage"), Loc.Get("Settings_EraseConfirmButton"));

        // The switch has already flipped itself; bring it back in line when the dialog was cancelled.
        OnPropertyChanged(nameof(EraseEnabled));
    }

    private void OnOperationChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(OperationState.IsBusy))
        {
            ToggleEraseCommand.NotifyCanExecuteChanged();
            InstallUpdateCommand.NotifyCanExecuteChanged();
        }
    }

    partial void OnSelectedThemeChanged(LocalizedOption<ThemeMode> value)
    {
        _settings.Settings.Theme = value.Value;
        _settings.Save();
        _appearance.ApplyTheme(value.Value);
    }

    partial void OnSelectedLanguageChanged(LocalizedOption<LanguageMode> value)
    {
        _settings.Settings.Language = value.Value;
        _settings.Save();
        AppearanceService.ApplyLanguage(value.Value);
    }
}
