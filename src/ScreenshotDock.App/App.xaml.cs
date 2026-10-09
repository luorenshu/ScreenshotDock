using Serilog;

namespace ScreenshotDock.App;

public partial class App : Application
{
    private Mutex? _mutex;
    private AppController? _controller;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var launchAtLogon = e.Args.Contains("--startup", StringComparer.OrdinalIgnoreCase);
        _mutex = new Mutex(true, "Local\\ScreenshotDock.v1", out var firstInstance);
        if (!firstInstance)
        {
            if (!launchAtLogon) MessageBox.Show("ScreenshotDock 已在运行。请从系统托盘打开截图栏或设置。", "ScreenshotDock", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScreenshotDock");
        Directory.CreateDirectory(root);
        Log.Logger = new LoggerConfiguration().MinimumLevel.Information().WriteTo.File(Path.Combine(root, "logs", "app-.log"),
            rollingInterval: RollingInterval.Day, retainedFileCountLimit: 7, fileSizeLimitBytes: 2_000_000, rollOnFileSizeLimit: true).CreateLogger();
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error(args.Exception, "Unhandled UI operation");
            args.Handled = true;
            _controller?.ReportError(args.Exception);
        };
        try
        {
            _controller = new AppController(root);
            await _controller.StartAsync(showInitialDock: !launchAtLogon);
        }
        catch (Exception exception)
        {
            Log.Fatal(exception, "Startup failed");
            MessageBox.Show($"无法启动 ScreenshotDock：{exception.Message}", "ScreenshotDock", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _controller?.Dispose();
        _mutex?.Dispose();
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
