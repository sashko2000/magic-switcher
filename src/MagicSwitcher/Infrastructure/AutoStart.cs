using Microsoft.Win32;

namespace MagicSwitcher.Infrastructure;

/// <summary>Autostart via HKCU\...\Run (no administrator rights required).</summary>
internal static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "MagicSwitcher";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string value
                && value.Contains(Environment.ProcessPath ?? "\0", StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// For the copy installed by the installer: start with Windows from the first run on (a keyboard utility that is
    /// not running does nothing), and take over an autostart entry that still points to an older, portable copy —
    /// otherwise that copy would keep starting instead of the installed one.
    /// </summary>
    public static void FollowInstalledCopy(bool firstRun)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            bool hasEntry = key?.GetValue(ValueName) is string;
            if ((hasEntry && !IsEnabled) || (!hasEntry && firstRun))
            {
                Set(true);
                Log.Info("Autostart now points to the installed copy");
            }
        }
        catch (Exception ex)
        {
            Log.Error("Failed to update autostart", ex);
        }
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (enabled && Environment.ProcessPath is { } path)
        {
            key.SetValue(ValueName, $"\"{path}\"");
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
