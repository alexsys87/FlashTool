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

        // Without 7z.dll / liblzma the corresponding formats are simply reported as unavailable.
        NativeLibraries.Initialize();

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

        // "FlashTool.exe image.img.xz" (or a file dropped onto the exe) opens the image right away.
        if (e.Args.Length > 0)
        {
            var write = _services.GetRequiredService<WritePageViewModel>();
            write.SourceText = e.Args[0];
            _ = write.LoadSourceAsync();
        }
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
#if DEBUG
        if (Environment.GetEnvironmentVariable("FLASHTOOL_DEMO") == "1")
        {
            services.AddSingleton<IDiskManager, DemoDiskManager>();
        }
        else
#endif
        {
            services.AddSingleton<IDiskManager, WindowsDiskManager>();
        }
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
        var log = WriteErrorLog(e.Exception);
        var message = Formatting.Error(e.Exception);
        if (log != null)
        {
            message += Environment.NewLine + Environment.NewLine + log;
        }

        MessageBox.Show(message, Loc.Get("Error_Title"), MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    /// <summary>Appends the full exception to %LOCALAPPDATA%\FlashTool\error.log; returns the log path.</summary>
    private static string? WriteErrorLog(Exception exception)
    {
        try
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FlashTool");
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, "error.log");
            File.AppendAllText(path, $"[{DateTime.Now:O}] {exception}{Environment.NewLine}{Environment.NewLine}");
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
