using System;
using Microsoft.Win32;

namespace Offstage;

/// <summary>Manages the per-user "Run at login" entry (HKCU ...\CurrentVersion\Run).</summary>
internal static class StartupManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Offstage";

    public static bool IsEnabled()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(ValueName) is string;
        }
        catch
        {
            return false;
        }
    }

    public static bool TrySetEnabled(bool enabled)
    {
        try
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath);

            if (enabled)
            {
                string? exe = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exe))
                    return false;
                key.SetValue(ValueName, $"\"{exe}\"");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }
}
