using MagicSwitcher.Infrastructure;

namespace MagicSwitcher.Service;

/// <summary>What the service is doing about a keyboard that does not work, for the status window on the sign-in screen.</summary>
internal enum KeyboardPhase
{
    /// <summary>Nothing to show.</summary>
    None,
    /// <summary>It does not answer: asleep, off, away or busy with another host.</summary>
    Waiting,
    /// <summary>It is awake and still ours, but idle: it connects when a key is pressed.</summary>
    Idle,
    /// <summary>Another host took it, and it does not accept a new pairing now: it has to be turned off and on.</summary>
    PowerCycle,
    /// <summary>Pairing it again: it must stay on.</summary>
    Pairing,
    /// <summary>Paired, the keyboard service is starting.</summary>
    Connecting,
    Connected,
}

/// <summary>
/// The current <see cref="KeyboardPhase"/> in a file (%ProgramData%\MagicSwitcher\status.txt): written by the service,
/// polled by the status window, which runs in another session.
/// </summary>
internal static class KeyboardStatus
{
    private static readonly Lock Sync = new();

    public static string FilePath { get; } = Path.Combine(AppPaths.MachineDataDirectory, "status.txt");

    public static void Write(KeyboardPhase phase, string name)
    {
        lock (Sync)
        {
            // The status window reads the file twice a second: a collision is retried.
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    if (phase == KeyboardPhase.None)
                    {
                        File.Delete(FilePath);
                        return;
                    }

                    Directory.CreateDirectory(AppPaths.MachineDataDirectory);
                    string temp = FilePath + ".tmp";
                    File.WriteAllText(temp, $"{phase}|{name}");
                    File.Move(temp, FilePath, overwrite: true);
                    return;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    if (attempt >= 5)
                    {
                        Log.Info($"Status file: {ex.Message}");
                        return;
                    }

                    Thread.Sleep(100);
                }
            }
        }
    }

    /// <summary>null when there is nothing to show (or the file is being replaced right now).</summary>
    public static (KeyboardPhase Phase, string Name)? Read()
    {
        try
        {
            // Share everything, so that reading never keeps the service from replacing or deleting the file.
            string text;
            using (var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream))
            {
                text = reader.ReadToEnd();
            }

            string[] parts = text.Split('|', 2);
            if (parts.Length != 2 || !Enum.TryParse<KeyboardPhase>(parts[0], out var phase) || phase == KeyboardPhase.None)
            {
                return null;
            }

            // The name ends up on the sign-in screen: keep it a short, single-line device name whatever the file says.
            string name = new(parts[1].Where(c => !char.IsControl(c)).Take(64).ToArray());
            return (phase, name);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
