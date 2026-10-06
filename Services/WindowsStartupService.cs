using System.IO;
using Microsoft.Win32;

namespace Voca.Services;

public static class WindowsStartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Voca2";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(ValueName) is string value && !string.IsNullOrWhiteSpace(value);
    }

    /// <summary>The exe Windows starts at sign-in, or null when start-up is off.</summary>
    public static string? RegisteredPath()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(ValueName) is string value && !string.IsNullOrWhiteSpace(value) ? value.Trim().Trim('"') : null;
    }

    /// <summary>
    /// When start-up is on but points at another copy of Voca (e.g. an old build folder), points it at this
    /// one, so an outdated version does not start with Windows. Returns true when it changed.
    /// </summary>
    public static bool FollowCurrentExe()
    {
        var current = Environment.ProcessPath;
        var registered = RegisteredPath();
        if (registered is null || string.IsNullOrWhiteSpace(current) || !Updater.Enabled(current)) return false;
        if (string.Equals(Path.GetFullPath(registered), Path.GetFullPath(current), StringComparison.OrdinalIgnoreCase)) return false;
        SetEnabled(true);
        return true;
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException("Không thể mở cấu hình Startup của Windows.");

        if (!enabled)
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            return;
        }

        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath))
            throw new InvalidOperationException("Không xác định được đường dẫn ứng dụng.");

        key.SetValue(ValueName, $"\"{executablePath}\"", RegistryValueKind.String);
    }
}
