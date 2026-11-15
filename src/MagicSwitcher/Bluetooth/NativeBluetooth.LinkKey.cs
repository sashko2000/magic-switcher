using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MagicSwitcher.Bluetooth;

/// <summary>Whether a paired device still accepts the link key Windows has stored for it.</summary>
internal enum LinkKeyTest
{
    /// <summary>The link was authenticated with the stored key: the device is still ours.</summary>
    Accepted,
    /// <summary>The device answers but rejects the stored key: another host paired with it since.</summary>
    Rejected,
    /// <summary>The device could not be reached, so nothing can be said.</summary>
    Unreachable,
    /// <summary>Bluetooth is not up (right after boot or wake): the test could not even start.</summary>
    NotReady,
}

internal static partial class NativeBluetooth
{
    /// <summary>
    /// Tests the stored link key without touching the pairing (measured with tools/KeyProbe on a Magic Keyboard).
    /// <para>
    /// An RFCOMM connection that requires authentication makes Windows authenticate the link first. A keyboard has no
    /// RFCOMM service, so the connection fails either way (WSAENETUNREACH), but what is left differs: with a good key
    /// the link stays up for a few seconds; with a key the device rejects, Windows drops the link at once (and keeps
    /// the pairing). The same connection without authentication leaves the link up in both cases, which tells
    /// "rejected" from "did not answer at all".
    /// </para>
    /// Blocking: 1–2 seconds when the device answers, longer when it does not.
    /// </summary>
    public static LinkKeyTest TestLinkKey(ulong address, out string details)
    {
        EnsureWinsock();
        const int WsaENetDown = 10050;
        var (errorWithKey, secondsWithKey) = TryConnect(address, authenticate: true);
        if (errorWithKey == WsaENetDown)
        {
            details = "Bluetooth is not ready yet (WSAENETDOWN)";
            return LinkKeyTest.NotReady;
        }

        if (GetState(address).Connected)
        {
            details = $"authenticated connection: error {errorWithKey} after {secondsWithKey:0.0} s, the link stays up";
            return LinkKeyTest.Accepted;
        }

        var (errorPlain, secondsPlain) = TryConnect(address, authenticate: false);
        bool reachable = GetState(address).Connected;
        details = $"authenticated connection: error {errorWithKey} after {secondsWithKey:0.0} s, the link is dropped; " +
                  $"plain connection: error {errorPlain} after {secondsPlain:0.0} s, the link {(reachable ? "stays up" : "does not come up")}";
        return reachable ? LinkKeyTest.Rejected : LinkKeyTest.Unreachable;
    }

    /// <summary>One RFCOMM connection attempt to channel 1. Returns the Winsock error (0 if it connected) and the time it took.</summary>
    private static (int Error, double Seconds) TryConnect(ulong address, bool authenticate)
    {
        const int AfBth = 32, SockStream = 1, BthProtoRfcomm = 3;
        const int SoBthAuthenticate = unchecked((int)0x80000001);

        IntPtr socket = WinsockSocket(AfBth, SockStream, BthProtoRfcomm);
        if (socket == new IntPtr(-1))
        {
            return (Marshal.GetLastPInvokeError(), 0);
        }

        try
        {
            if (authenticate)
            {
                int on = 1;
                if (setsockopt(socket, BthProtoRfcomm, SoBthAuthenticate, ref on, sizeof(int)) != 0)
                {
                    return (Marshal.GetLastPInvokeError(), 0);
                }
            }

            // SOCKADDR_BTH, packed: family (2), address (8), service class GUID (16, zero), port (4).
            var name = new byte[30];
            BitConverter.GetBytes((ushort)AfBth).CopyTo(name, 0);
            BitConverter.GetBytes(address).CopyTo(name, 2);
            BitConverter.GetBytes(1u).CopyTo(name, 26);

            var watch = Stopwatch.StartNew();
            int result = connect(socket, name, name.Length);
            int error = result == 0 ? 0 : Marshal.GetLastPInvokeError();
            return (error, watch.Elapsed.TotalSeconds);
        }
        finally
        {
            closesocket(socket);
        }
    }

    private static readonly Lock WinsockLock = new();
    private static bool _winsockStarted;

    private static void EnsureWinsock()
    {
        lock (WinsockLock)
        {
            if (!_winsockStarted)
            {
                WSAStartup(0x0202, out _);
                _winsockStarted = true;
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WSADATA
    {
        public ushort wVersion, wHighVersion;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 408)] public byte[] rest;
    }

    [DllImport("ws2_32.dll")]
    private static extern int WSAStartup(ushort version, out WSADATA data);

    [DllImport("ws2_32.dll", EntryPoint = "socket", SetLastError = true)]
    private static extern IntPtr WinsockSocket(int addressFamily, int type, int protocol);

    [DllImport("ws2_32.dll", SetLastError = true)]
    private static extern int setsockopt(IntPtr socket, int level, int name, ref int value, int length);

    [DllImport("ws2_32.dll", SetLastError = true)]
    private static extern int connect(IntPtr socket, byte[] name, int length);

    [DllImport("ws2_32.dll")]
    private static extern int closesocket(IntPtr socket);
}
