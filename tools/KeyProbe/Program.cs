// KeyProbe: tries to authenticate the Bluetooth link to a paired keyboard with the key Windows has stored, and logs
// what happens to the connection attempt and to the pairing.
//
//   KeyProbe [label] [--address XX:XX:XX:XX:XX:XX]
//
// Without --address it takes the keyboard chosen in MagicSwitcher (%AppData%\MagicSwitcher\settings.json).
// The label (any words) is written to the log to tell the runs apart, e.g. "ours idle" or "after phone".
// Log: %AppData%\MagicSwitcher\keyprobe-log.txt (appended).
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace KeyProbe;

internal static class Program
{
    private static StreamWriter? _log;

    private static int Main(string[] args)
    {
        string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MagicSwitcher");
        Directory.CreateDirectory(directory);
        _log = new StreamWriter(Path.Combine(directory, "keyprobe-log.txt"), append: true, Encoding.UTF8) { AutoFlush = true };

        ulong? address = null;
        var label = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--address" && i + 1 < args.Length)
            {
                address = ParseAddress(args[++i]);
            }
            else
            {
                label.Add(args[i]);
            }
        }

        address ??= ReadKeyboardFromSettings(directory);
        if (address is not { } target)
        {
            Write("No keyboard: choose one in MagicSwitcher or pass --address XX:XX:XX:XX:XX:XX");
            return 1;
        }

        Write($"==== KeyProbe: {Format(target)} — {(label.Count > 0 ? string.Join(' ', label) : "(no label)")} ====");
        if (WSAStartup(0x0202, out _) != 0)
        {
            Write("WSAStartup failed");
            return 1;
        }

        Write($"state before: {State(target)}");

        // 1. The test itself: authentication required.
        Connect(target, authenticate: true);
        foreach (int wait in new[] { 0, 3, 5, 7, 15 }) // states at 0, 3, 8, 15 and 30 s after the attempt
        {
            Thread.Sleep(wait * 1000);
            Write($"state: {State(target)}");
        }

        // 2. For comparison: the same connection without authentication.
        Connect(target, authenticate: false);
        foreach (int wait in new[] { 0, 3, 7 })
        {
            Thread.Sleep(wait * 1000);
            Write($"state: {State(target)}");
        }

        Write("done");
        return 0;
    }

    /// <summary>One blocking RFCOMM connection attempt to channel 1 (the keyboard has no RFCOMM, so it always fails).</summary>
    private static void Connect(ulong address, bool authenticate)
    {
        const int AfBth = 32, SockStream = 1, BthProtoRfcomm = 3;
        const int SoBthAuthenticate = unchecked((int)0x80000001);

        IntPtr socket = WsaSocket(AfBth, SockStream, BthProtoRfcomm);
        if (socket == new IntPtr(-1))
        {
            Write($"socket failed: {WSAGetLastError()}");
            return;
        }

        try
        {
            if (authenticate)
            {
                int on = 1;
                int result = setsockopt(socket, BthProtoRfcomm, SoBthAuthenticate, ref on, sizeof(int));
                Write($"setsockopt(SO_BTH_AUTHENTICATE) -> {(result == 0 ? "ok" : $"error {WSAGetLastError()}")}");
            }

            // SOCKADDR_BTH, packed: family (2), address (8), service class GUID (16, zero), port (4).
            var name = new byte[30];
            BitConverter.GetBytes((ushort)AfBth).CopyTo(name, 0);
            BitConverter.GetBytes(address).CopyTo(name, 2);
            BitConverter.GetBytes(1u).CopyTo(name, 26);

            var watch = Stopwatch.StartNew();
            int connected = connect(socket, name, name.Length);
            int error = connected == 0 ? 0 : WSAGetLastError();
            Write($"connect ({(authenticate ? "authentication required" : "no authentication")}) -> " +
                  $"{(connected == 0 ? "CONNECTED" : $"error {error} ({Describe(error)})")} after {watch.Elapsed.TotalSeconds:0.0} s");
        }
        finally
        {
            closesocket(socket);
        }
    }

    private static string Describe(int error) => error switch
    {
        10013 => "WSAEACCES",
        10048 => "WSAEADDRINUSE",
        10049 => "WSAEADDRNOTAVAIL",
        10050 => "WSAENETDOWN",
        10051 => "WSAENETUNREACH",
        10053 => "WSAECONNABORTED",
        10054 => "WSAECONNRESET",
        10060 => "WSAETIMEDOUT",
        10061 => "WSAECONNREFUSED",
        10064 => "WSAEHOSTDOWN",
        10065 => "WSAEHOSTUNREACH",
        10108 => "WSASERVICE_NOT_FOUND",
        _ => new System.ComponentModel.Win32Exception(error).Message,
    };

    private static string State(ulong address)
    {
        var parameters = new BLUETOOTH_FIND_RADIO_PARAMS { dwSize = (uint)Marshal.SizeOf<BLUETOOTH_FIND_RADIO_PARAMS>() };
        var find = BluetoothFindFirstRadio(ref parameters, out var radio);
        if (find == IntPtr.Zero)
        {
            return "no Bluetooth radio";
        }

        BluetoothFindRadioClose(find);
        try
        {
            var info = new BLUETOOTH_DEVICE_INFO { dwSize = (uint)Marshal.SizeOf<BLUETOOTH_DEVICE_INFO>(), Address = address };
            int error = BluetoothGetDeviceInfo(radio, ref info);
            return error == 0
                ? $"connected {info.fConnected}, remembered {info.fRemembered}, paired {info.fAuthenticated}"
                : $"unknown to Windows (error {error})";
        }
        finally
        {
            CloseHandle(radio);
        }
    }

    private static ulong? ReadKeyboardFromSettings(string directory)
    {
        try
        {
            using var settings = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "settings.json")));
            foreach (var device in settings.RootElement.GetProperty("Devices").EnumerateArray())
            {
                if (device.TryGetProperty("RestoreAtStartup", out var restore) && restore.GetBoolean())
                {
                    return device.GetProperty("Address").GetUInt64();
                }
            }
        }
        catch (Exception ex)
        {
            Write($"settings.json: {ex.Message}");
        }

        return null;
    }

    private static ulong ParseAddress(string text) =>
        ulong.Parse(text.Replace(":", "").Replace("-", ""), NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    private static string Format(ulong address) =>
        string.Join(':', Enumerable.Range(0, 6).Select(i => ((address >> (8 * (5 - i))) & 0xFF).ToString("X2")));

    private static void Write(string text)
    {
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {text}";
        Console.WriteLine(line);
        _log?.WriteLine(line);
    }

    #region P/Invoke

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEMTIME
    {
        public ushort wYear, wMonth, wDayOfWeek, wDay, wHour, wMinute, wSecond, wMilliseconds;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct BLUETOOTH_DEVICE_INFO
    {
        public uint dwSize;
        public ulong Address;
        public uint ulClassofDevice;
        [MarshalAs(UnmanagedType.Bool)] public bool fConnected;
        [MarshalAs(UnmanagedType.Bool)] public bool fRemembered;
        [MarshalAs(UnmanagedType.Bool)] public bool fAuthenticated;
        public SYSTEMTIME stLastSeen;
        public SYSTEMTIME stLastUsed;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 248)] public string szName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BLUETOOTH_FIND_RADIO_PARAMS
    {
        public uint dwSize;
    }

    [DllImport("bthprops.cpl", SetLastError = true)]
    private static extern IntPtr BluetoothFindFirstRadio(ref BLUETOOTH_FIND_RADIO_PARAMS pbtfrp, out IntPtr phRadio);

    [DllImport("bthprops.cpl", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BluetoothFindRadioClose(IntPtr hFind);

    [DllImport("bthprops.cpl", CharSet = CharSet.Unicode)]
    private static extern int BluetoothGetDeviceInfo(IntPtr hRadio, ref BLUETOOTH_DEVICE_INFO pbtdi);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct WSADATA
    {
        public ushort wVersion, wHighVersion;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 408)] public byte[] rest;
    }

    [DllImport("ws2_32.dll")]
    private static extern int WSAStartup(ushort version, out WSADATA data);

    [DllImport("ws2_32.dll", EntryPoint = "socket", SetLastError = true)]
    private static extern IntPtr WsaSocket(int addressFamily, int type, int protocol);

    [DllImport("ws2_32.dll", SetLastError = true)]
    private static extern int setsockopt(IntPtr socket, int level, int name, ref int value, int length);

    [DllImport("ws2_32.dll", SetLastError = true)]
    private static extern int connect(IntPtr socket, byte[] name, int length);

    [DllImport("ws2_32.dll")]
    private static extern int closesocket(IntPtr socket);

    /// <summary>The Winsock error of the last call (the runtime saves it right after the call returns).</summary>
    private static int WSAGetLastError() => Marshal.GetLastPInvokeError();

    #endregion
}
