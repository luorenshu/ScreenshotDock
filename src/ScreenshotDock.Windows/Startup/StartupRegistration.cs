using Microsoft.Win32;

namespace ScreenshotDock.Windows.Startup;

public interface IStartupRegistration
{
    bool IsEnabled { get; }
    void SetEnabled(bool enabled);
}

public sealed class StartupRegistration : IStartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private readonly string _executable;
    private readonly string _registryPath;
    private readonly string _valueName;
    private string Command => $"\"{_executable}\" --startup";

    public StartupRegistration() : this(Environment.ProcessPath
        ?? throw new InvalidOperationException("无法找到当前程序路径。"), RunKey) { }

    public StartupRegistration(string executable, string registryPath, string valueName = "ScreenshotDock")
    {
        _executable = Path.GetFullPath(executable);
        _registryPath = registryPath;
        _valueName = valueName;
    }

    public bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(_registryPath);
            return string.Equals(key?.GetValue(_valueName) as string, Command, StringComparison.OrdinalIgnoreCase);
        }
    }

    public void SetEnabled(bool enabled)
    {
        if (enabled)
        {
            if (!File.Exists(_executable) || !string.Equals(Path.GetExtension(_executable), ".exe", StringComparison.OrdinalIgnoreCase)
                || string.Equals(Path.GetFileName(_executable), "dotnet.exe", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("请使用发布版 ScreenshotDock.exe 设置开机启动。");
            if (Command.Length > 260) throw new InvalidOperationException("程序路径过长，请将软件移到较短的路径后重试。");
            using var key = Registry.CurrentUser.CreateSubKey(_registryPath, writable: true)
                ?? throw new IOException("无法写入开机启动设置。");
            key.SetValue(_valueName, Command, RegistryValueKind.String);
        }
        else
        {
            using var key = Registry.CurrentUser.OpenSubKey(_registryPath, writable: true);
            key?.DeleteValue(_valueName, throwOnMissingValue: false);
        }
    }
}
