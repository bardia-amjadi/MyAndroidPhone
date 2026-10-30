using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using MyAndroidPhone.Android;
using MyAndroidPhone.Core.Devices;
using MyAndroidPhone.Core.Features;
using MyAndroidPhone.Core.Files;
using MyAndroidPhone.Core.Logging;
using MyAndroidPhone.Core.Platform;
using MyAndroidPhone.Core.Processes;
using MyAndroidPhone.Core.Settings;
using MyAndroidPhone.Core.Tools;
using MyAndroidPhone.Core.Transfers;
using MyAndroidPhone.Desktop.Services;
using MyAndroidPhone.Desktop.ViewModels;
using MyAndroidPhone.Desktop.Views;
using MyAndroidPhone.Infrastructure.Diagnostics;
using MyAndroidPhone.Infrastructure.Logging;
using MyAndroidPhone.Infrastructure.Platform;
using MyAndroidPhone.Infrastructure.Processes;
using MyAndroidPhone.Infrastructure.Storage;
using MyAndroidPhone.Tools;

namespace MyAndroidPhone.Desktop;

public partial class App : Application
{
    private readonly ServiceProvider _services;
    private readonly RollingFileLoggerProvider _logProvider;
    private MainWindow? _window;

    public App()
    {
        InitializeComponent();

        var paths = new AppPaths();
        paths.EnsureDirectories();

        // Logging is configured from the persisted settings before the container exists, so startup itself is logged.
        var settings = JsonFile.ReadOrDefault<AppSettings>(paths.SettingsFile);
        _logProvider = new RollingFileLoggerProvider(new RollingFileLoggerOptions
        {
            Directory = paths.LogsDirectory,
            MaxFileSizeBytes = Math.Clamp(settings.LogFileSizeMegabytes, 1, 100) * 1024L * 1024L,
            RetainedFileCount = Math.Clamp(settings.LogRetainedFiles, 1, 30),
            MinimumLevel = settings.LogVerbosity == LogVerbosity.Verbose ? LogLevel.Debug : LogLevel.Information
        });

        var services = new ServiceCollection();
        ConfigureServices(services, paths);
        _services = services.BuildServiceProvider();

        UnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            _logProvider.CreateLogger("AppDomain").LogCritical(e.ExceptionObject as Exception, "Unhandled exception (terminating={Terminating})", e.IsTerminating);
        TaskScheduler.UnobservedTaskException += (_, e) =>
            _logProvider.CreateLogger("TaskScheduler").LogError(e.Exception, "Unobserved task exception");
    }

    private void ConfigureServices(IServiceCollection services, AppPaths paths)
    {
        services.AddSingleton(paths);
        services.AddSingleton<IRecentLogSource>(_logProvider);
        services.AddLogging(builder =>
        {
            builder.ClearProviders();
            builder.SetMinimumLevel(LogLevel.Trace); // filtering is done by the provider's own minimum level
            builder.AddProvider(_logProvider);
        });

        // Core services
        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<ISetupStateService, SetupStateService>();
        services.AddSingleton<IProcessRunner, ProcessRunner>();
        services.AddSingleton<ISystemCompatibilityService, SystemCompatibilityService>();
        services.AddSingleton<IToolService, ToolService>();
        services.AddSingleton<IAdbClient, AdbClient>();
        services.AddSingleton<DeviceStoreService>();
        services.AddSingleton<IDeviceService, DeviceService>();
        services.AddSingleton<IFileExplorerService, FileExplorerService>();
        services.AddSingleton<ITransferService, TransferService>();
        services.AddSingleton<IFeatureRegistry>(_ => BuiltInFeatures.CreateRegistry());
        services.AddSingleton<DiagnosticsReportBuilder>();

        // UI services
        services.AddSingleton<WindowContext>();
        services.AddSingleton<IUiDispatcher, UiDispatcher>();
        services.AddSingleton<INotificationService, NotificationService>();
        services.AddSingleton<ThemeService>();
        services.AddSingleton<IFilePickerService, FilePickerService>();
        services.AddSingleton<IFolderLauncher, FolderLauncher>();
        services.AddSingleton<ToolActions>();
        services.AddSingleton<ModuleViewFactory>();

        // View models and views are created per use (a module window can be closed and reopened).
        services.AddTransient<ShellViewModel>();
        services.AddTransient<ToolManagerViewModel>();
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<DiagnosticsViewModel>();
        services.AddTransient<DeviceCenterViewModel>();
        services.AddTransient<FileExplorerViewModel>();
        services.AddTransient<TransferCenterViewModel>();
        services.AddTransient<SetupWizardViewModel>();
        services.AddTransient<ShellView>();
        services.AddTransient<SetupWizardView>();
        services.AddTransient<ToolManagerView>();
        services.AddTransient<SettingsView>();
        services.AddTransient<DiagnosticsView>();
        services.AddTransient<DeviceCenterView>();
        services.AddTransient<FileExplorerView>();
        services.AddTransient<TransferCenterView>();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var logger = _services.GetRequiredService<ILogger<App>>();
        var compatibility = _services.GetRequiredService<ISystemCompatibilityService>().Check();
        logger.LogInformation(
            "My Android Phone {Version} starting on {Os} ({Architecture}); supported={Supported}",
            DiagnosticsReportBuilder.ApplicationVersion, compatibility.OsDescription, compatibility.Architecture, compatibility.IsSupported);
        foreach (var issue in compatibility.Issues)
        {
            logger.LogWarning("Compatibility issue: {Issue}", issue);
        }

        _window = new MainWindow(_services);
        _services.GetRequiredService<WindowContext>().MainWindow = _window;
        _window.Closed += OnWindowClosed;
        _window.Activate();
        _window.ShowStartup();

        _services.GetRequiredService<IDeviceService>().StartMonitoring();
        _ = RefreshToolsAsync(logger);
    }

    private async Task RefreshToolsAsync(ILogger logger)
    {
        try
        {
            await _services.GetRequiredService<IToolService>().RefreshAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Initial tool detection failed.");
            _services.GetRequiredService<INotificationService>()
                .Post(NotificationSeverity.Error, "Tool detection failed", ex.Message);
        }
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        _services.GetRequiredService<ILogger<App>>().LogInformation("Main window closed; shutting down.");
        _services.Dispose();
        _logProvider.Dispose();
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        // Logged, not swallowed: the exception is left unhandled so a corrupt UI state is never carried on silently.
        _logProvider.CreateLogger("Xaml").LogCritical(e.Exception, "Unhandled UI exception: {Message}", e.Message);
    }
}
