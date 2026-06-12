using Microsoft.Win32;
using System.Diagnostics;

namespace Tachion.Windows;

public static class StartupService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "tachion";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, false);
        return key?.GetValue(AppName) is string;
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, true) ?? Registry.CurrentUser.CreateSubKey(RunKey, true);
        if (!enabled)
        {
            key.DeleteValue(AppName, false);
            return;
        }
        var exe = Process.GetCurrentProcess().MainModule?.FileName ?? Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exe)) return;
        key.SetValue(AppName, "\"" + exe + "\"");
    }
}
