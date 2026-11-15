using System.Diagnostics;
using MagicSwitcher.Infrastructure;
using MagicSwitcher.Localization;
using MagicSwitcher.UI;

namespace MagicSwitcher.Service;

/// <summary>
/// Shows what the service is doing about the keyboard on the Windows sign-in and lock screens, where nothing else can:
/// without it the user cannot tell "turn the keyboard off and on" from "pairing, keep it on" and keeps switching it.
/// <para>
/// The service starts <c>MagicSwitcher.exe --status-overlay</c> with its own SYSTEM token in the console session, on the
/// Winlogon desktop (the one that shows the sign-in and lock screens; while the user works, that desktop is not visible,
/// and the tray app reports instead). The window only displays text from <see cref="KeyboardStatus"/>: it takes no
/// input, cannot be activated and is transparent to the mouse, because it runs as SYSTEM on the secure desktop.
/// It closes itself when there is nothing to show.
/// </para>
/// </summary>
internal sealed class StatusOverlay
{
    public const string OverlayArgument = "--status-overlay";
    private const string WinlogonDesktop = @"winsta0\winlogon";

    private readonly Lock _sync = new();
    private Process? _process;
    private int _version;

    public void Update(string deviceName, KeyboardPhase phase)
    {
        int version;
        lock (_sync)
        {
            version = ++_version;
            KeyboardStatus.Write(phase, deviceName);
            if (phase != KeyboardPhase.None)
            {
                EnsureRunningLocked();
            }
        }

        if (phase == KeyboardPhase.Connected)
        {
            // Good news needs a few seconds on the screen, then the window goes away.
            _ = Task.Delay(TimeSpan.FromSeconds(4)).ContinueWith(_ =>
            {
                lock (_sync)
                {
                    if (version == _version)
                    {
                        KeyboardStatus.Write(KeyboardPhase.None, "");
                    }
                }
            });
        }
    }

    public void Stop()
    {
        lock (_sync)
        {
            _version++;
            KeyboardStatus.Write(KeyboardPhase.None, "");
            try
            {
                if (_process is { HasExited: false } process)
                {
                    process.Kill();
                }
            }
            catch (Exception ex)
            {
                Log.Error("Failed to stop the status window", ex);
            }

            _process?.Dispose();
            _process = null;
        }
    }

    private void EnsureRunningLocked()
    {
        if (_process is { HasExited: false })
        {
            return;
        }

        _process?.Dispose();
        _process = null;
        try
        {
            uint session = RemapHelper.ActiveConsoleSession;
            if (session == 0xFFFFFFFF)
            {
                return; // no console session (yet)
            }

            string exe = Environment.ProcessPath ?? throw new InvalidOperationException("Unknown executable path.");
            int pid = RemapHelper.CreateProcessInSession(exe, $"\"{exe}\" {OverlayArgument}", session, WinlogonDesktop);
            _process = Process.GetProcessById(pid);
            Log.Info($"Status window started in session {session} (process {pid})");
        }
        catch (Exception ex)
        {
            Log.Error("Failed to start the status window", ex);
        }
    }

    /// <summary>The status window process.</summary>
    public static int RunOverlay()
    {
        AppPaths.UseMachineData();
        Log.UseFile("overlay-log.txt");
        if (KeyboardStatus.Read() is null)
        {
            return 0;
        }

        Log.Info($"Status window started: {KeyboardStatus.Read()}");
        AppLanguage.Apply(BluetoothService.LoadLanguage());
        ApplicationConfiguration.Initialize();
        Application.Run(new StatusOverlayForm());
        Log.Info("Status window closed");
        return 0;
    }
}
