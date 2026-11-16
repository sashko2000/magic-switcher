using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using MagicSwitcher.Infrastructure;
using MagicSwitcher.Keyboard;

namespace MagicSwitcher.Service;

/// <summary>
/// Runs the <see cref="KeyRemapper"/> in the signed-in user's session as LocalSystem.
/// <para>
/// A keyboard hook in a normal (non-elevated) process does not see keys typed into windows that run as administrator,
/// and its replacement keys cannot reach them (User Interface Privilege Isolation). The service therefore starts
/// <c>MagicSwitcher.exe --remap-helper</c> from %ProgramFiles% with its own SYSTEM token, moved to the user's session
/// and desktop: the highest integrity level, so remapping works in every window. The helper reads the mappings from the
/// service configuration and is restarted when they change.
/// </para>
/// </summary>
internal sealed class RemapHelper : IDisposable
{
    public const string HelperArgument = "--remap-helper";

    /// <summary>The helper for the sign-in and lock screens: macros marked for them (see <see cref="RunSignInHelper"/>).</summary>
    public const string SignInArgument = "--signin-helper";

    private readonly string _argument;
    private readonly string _desktop;
    private readonly bool _needsUser;
    private readonly string _name;

    /// <summary>The key remapper in the signed-in user's session, on the user's desktop.</summary>
    public RemapHelper() : this(HelperArgument, @"winsta0\default", needsUser: true, "Key remapper")
    {
    }

    private RemapHelper(string argument, string desktop, bool needsUser, string name)
    {
        _argument = argument;
        _desktop = desktop;
        _needsUser = needsUser;
        _name = name;
    }

    /// <summary>
    /// The same thing on the Winlogon desktop (the sign-in and lock screens), with or without a signed-in user. A
    /// keyboard hook only sees the keys of its own desktop, so this helper is idle while the user works.
    /// </summary>
    public static RemapHelper ForSignInScreen() => new(SignInArgument, @"winsta0\winlogon", needsUser: false, "Sign-in screen helper");

    private readonly Lock _sync = new();
    private Process? _process;
    private uint _session = uint.MaxValue;

    /// <summary>Starts (or restarts) the helper in the active console session, or stops it if it is not needed.</summary>
    public void Update(bool needed, bool restart)
    {
        lock (_sync)
        {
            uint session = WTSGetActiveConsoleSessionId();
            bool wanted = needed && session != 0xFFFFFFFF && (!_needsUser || HasUser(session));
            bool running = _process is { HasExited: false };
            if (running && (!wanted || restart || session != _session))
            {
                StopLocked();
                running = false;
            }

            if (wanted && !running)
            {
                StartLocked(session);
            }
        }
    }

    public void Stop()
    {
        lock (_sync)
        {
            StopLocked();
        }
    }

    public void Dispose() => Stop();

    private void StartLocked(uint session)
    {
        try
        {
            string exe = Environment.ProcessPath ?? throw new InvalidOperationException("Unknown executable path.");
            int pid = CreateProcessInSession(exe, $"\"{exe}\" {_argument}", session, _desktop);
            _process = Process.GetProcessById(pid);
            _session = session;
            Log.Info($"{_name} started in session {session} (process {pid})");
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to start: {_name}", ex);
        }
    }

    private void StopLocked()
    {
        if (_process is { HasExited: false } process)
        {
            try
            {
                process.Kill();
                process.WaitForExit(3000);
                Log.Info($"{_name} stopped");
            }
            catch (Exception ex)
            {
                Log.Error($"Failed to stop: {_name}", ex);
            }
        }

        _process?.Dispose();
        _process = null;
    }

    /// <summary>A user is signed in to the session (not just the sign-in screen).</summary>
    private static bool HasUser(uint session)
    {
        if (!WTSQuerySessionInformation(IntPtr.Zero, session, 5 /* WTSUserName */, out var buffer, out _))
        {
            return false;
        }

        try
        {
            return !string.IsNullOrEmpty(Marshal.PtrToStringUni(buffer));
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    /// <summary>The session of the physical console; 0xFFFFFFFF if there is none.</summary>
    public static uint ActiveConsoleSession => WTSGetActiveConsoleSessionId();

    /// <summary>
    /// Starts a process with this service's (SYSTEM) token in another session, on the given desktop
    /// (<c>winsta0\default</c>: the user's desktop; <c>winsta0\winlogon</c>: the sign-in and lock screens).
    /// </summary>
    public static int CreateProcessInSession(string exe, string commandLine, uint session, string desktop)
    {
        const uint TokenAllAccess = 0xF01FF;
        if (!OpenProcessToken(Process.GetCurrentProcess().Handle, TokenAllAccess, out var own))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        try
        {
            if (!DuplicateTokenEx(own, TokenAllAccess, IntPtr.Zero, 2 /* SecurityImpersonation */, 1 /* TokenPrimary */, out var token))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            try
            {
                if (!SetTokenInformation(token, 12 /* TokenSessionId */, ref session, sizeof(uint)))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                var startup = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>(), lpDesktop = desktop };
                const uint CreateNoWindow = 0x08000000;
                if (!CreateProcessAsUser(token, exe, commandLine, IntPtr.Zero, IntPtr.Zero, false, CreateNoWindow, IntPtr.Zero,
                        Path.GetDirectoryName(exe), ref startup, out var info))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                CloseHandle(info.hThread);
                CloseHandle(info.hProcess);
                return info.dwProcessId;
            }
            finally
            {
                CloseHandle(token);
            }
        }
        finally
        {
            CloseHandle(own);
        }
    }

    /// <summary>The helper process: runs the remapper with the mappings from the service configuration until it is killed.</summary>
    public static int RunHelper()
    {
        AppPaths.UseMachineData();
        Log.UseFile("remap-log.txt");
        var mappings = BluetoothService.LoadKeyMap();
        using var remapper = new KeyRemapper(mappings, AppleKeyboardDriver.IsInstalled ? AppleTopRow.MachinePath : null,
            BluetoothService.LoadMacros().Where(m => m.InSession));
        remapper.Start();
        Thread.Sleep(Timeout.Infinite); // the service kills this process when the mappings change or the user signs out
        return 0;
    }

    /// <summary>
    /// The helper process on the sign-in and lock screens: only the macros marked for them, no remapping and no Fn Lock
    /// (those stay as they were: not on these screens).
    /// </summary>
    public static int RunSignInHelper()
    {
        AppPaths.UseMachineData();
        Log.UseFile("signin-log.txt");
        var macros = BluetoothService.LoadMacros().Where(m => m.OnSignInScreen && m.IsValid).ToList();
        if (macros.Count == 0)
        {
            return 0;
        }

        using var remapper = new KeyRemapper([], topRowStatePath: null, macros);
        remapper.Start();
        Thread.Sleep(Timeout.Infinite); // the service kills this process when the macros change
        return 0;
    }

    #region P/Invoke

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

    [DllImport("kernel32.dll")] private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool WTSQuerySessionInformation(IntPtr server, uint sessionId, int infoClass, out IntPtr buffer, out uint bytes);

    [DllImport("wtsapi32.dll")] private static extern void WTSFreeMemory(IntPtr memory);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(IntPtr token, uint access, IntPtr attributes, int impersonationLevel, int tokenType, out IntPtr newToken);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool SetTokenInformation(IntPtr token, int infoClass, ref uint info, int length);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessAsUser(IntPtr token, string applicationName, string commandLine, IntPtr processAttributes,
        IntPtr threadAttributes, bool inheritHandles, uint creationFlags, IntPtr environment, string? currentDirectory,
        ref STARTUPINFO startupInfo, out PROCESS_INFORMATION processInformation);

    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);

    #endregion
}
