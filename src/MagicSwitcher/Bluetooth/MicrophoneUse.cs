using MagicSwitcher.Infrastructure;
using Microsoft.Win32;

namespace MagicSwitcher.Bluetooth;

/// <summary>
/// Which apps are recording from a microphone right now (a call in Teams, Zoom, a browser…). Windows keeps this for the
/// microphone privacy indicator in the user's registry: under CapabilityAccessManager\ConsentStore\microphone every app
/// that used a microphone has LastUsedTimeStart and LastUsedTimeStop (FILETIME), and the stop time is 0 while the app
/// is still recording. Packaged apps (new Teams) are subkeys named after the package, desktop apps are under
/// NonPackaged with the path of the exe ('\' replaced by '#').
/// </summary>
internal static class MicrophoneUse
{
    private const string ConsentStore = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";

    // An app that crashed while recording never writes its stop time; do not let such an entry block repairs for ever.
    private static readonly TimeSpan StaleAfter = TimeSpan.FromHours(12);

    /// <summary>Names of the apps that are recording now; empty if none, or if it cannot be told.</summary>
    public static IReadOnlyList<string> AppsRecording()
    {
        var apps = new List<string>();
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(ConsentStore);
            if (root is null)
            {
                return apps;
            }

            foreach (string name in root.GetSubKeyNames())
            {
                using var key = root.OpenSubKey(name);
                if (key is null)
                {
                    continue;
                }

                if (name.Equals("NonPackaged", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (string path in key.GetSubKeyNames())
                    {
                        using var app = key.OpenSubKey(path);
                        if (app is not null && IsRecording(app))
                        {
                            apps.Add(Path.GetFileNameWithoutExtension(path.Replace('#', '\\')));
                        }
                    }
                }
                else if (IsRecording(key))
                {
                    // "MSTeams_8wekyb3d8bbwe" -> "MSTeams"
                    int underscore = name.IndexOf('_');
                    apps.Add(underscore > 0 ? name[..underscore] : name);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Info($"Cannot tell which apps use the microphone: {ex.GetType().Name}: {ex.Message}");
        }

        return apps.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static bool IsRecording(RegistryKey app)
    {
        if (app.GetValue("LastUsedTimeStart") is not long start || start <= 0
            || app.GetValue("LastUsedTimeStop") is not long stop || stop != 0)
        {
            return false;
        }

        try
        {
            return DateTime.UtcNow - DateTime.FromFileTimeUtc(start) < StaleAfter;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }
}
