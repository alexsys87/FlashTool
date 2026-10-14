using System.IO;
using System.Net;
using System.Net.Http;
using System.Windows;
using System.Windows.Threading;
using FlashTool.App.Localization;
using FlashTool.App.Services;
using FlashTool.App.ViewModels;
using FlashTool.App.Views;
using FlashTool.App.Views.Pages;
using FlashTool.Core.Containers;
using FlashTool.Core.Devices;
using FlashTool.Platform.Windows;
using Microsoft.Extensions.DependencyInjection;
using Wpf.Ui;

namespace FlashTool.App;

public partial class App : Application
{
    private ServiceProvider? _services;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;

        // 7z.dll ships in x64/x86 sub-folders (7z.Libs package); without it 7z is simply unavailable.
        var arch = Environment.Is64BitProcess ? "x64" : "x86";
        SevenZipContainerHandler.Initialize(Path.Combine(AppContext.BaseDirectory, arch, "7z.dll"));

        _services = ConfigureServices();
        var settings = _services.GetRequiredService<SettingsService>();
        AppearanceService.ApplyLanguage(settings.Settings.Language);

        var window = _services.GetRequiredService<MainWindow>();
        var appearance = _services.GetRequiredService<AppearanceService>();
        appearance.Attach(window);
        appearance.ApplyTheme(settings.Settings.Theme);
        _services.GetRequiredService<DeviceChangeMonitor>().Attach(window);

        MainWindow = window;
        window.Show();
        _ = _services.GetRequiredService<DeviceListService>().RefreshAsync();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _services?.Dispose();
        base.OnExit(e);
    }

    private static ServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();

        services.AddSingleton(ContainerRegistry.Default);
        services.AddSingleton<IDiskManager, WindowsDiskManager>();
        services.AddSingleton(_ =>
        {
            // No automatic decompression: image bytes must arrive exactly as stored on the server.
            var handler = new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.None,
                PooledConnectionLifetime = TimeSpan.FromMinutes(10),
                ConnectTimeout = TimeSpan.FromSeconds(30),
            };
            var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(100) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("FlashTool/1.0");
            return client;
        });

        services.AddSingleton<SettingsService>();
        services.AddSingleton<AppearanceService>();
        services.AddSingleton<DeviceChangeMonitor>();
        services.AddSingleton<DeviceListService>();
        services.AddSingleton<OperationState>();
        services.AddSingleton<IContentDialogService, ContentDialogService>();
        services.AddSingleton<DialogService>();

        services.AddSingleton<WritePageViewModel>();
        services.AddSingleton<ReadPageViewModel>();
        services.AddSingleton<SettingsPageViewModel>();
        services.AddSingleton<WritePage>();
        services.AddSingleton<ReadPage>();
        services.AddSingleton<SettingsPage>();
        services.AddSingleton<MainWindow>();

        return services.BuildServiceProvider();
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(Formatting.Error(e.Exception), Loc.Get("Error_Title"), MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
