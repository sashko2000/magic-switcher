using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using MagicSwitcher.Infrastructure;

namespace MagicSwitcher.Keyboard;

/// <summary>
/// Is a Bluetooth Apple keyboard plugged in with its cable right now? A Magic Keyboard on a cable turns its Bluetooth
/// off and works as a USB keyboard: it is "not connected" as a Bluetooth device, but there is nothing to restore.
/// <para>
/// The two faces of the keyboard share the product ID: Bluetooth <c>BTHENUM\{…1124…}_VID&amp;0001004C_PID&amp;029C\…&lt;address&gt;…</c>,
/// USB <c>USB\VID_05AC&amp;PID_029C…</c>. The Bluetooth device node stays in the registry while the keyboard is away, so
/// the product ID of an address is known even then. (Two keyboards of the same model cannot be told apart this way.)
/// </para>
/// </summary>
internal static partial class AppleUsbKeyboard
{
    public static bool IsConnectedByCable(ulong bluetoothAddress)
    {
        try
        {
            string address = bluetoothAddress.ToString("X12");
            string? productId = null;
            foreach (string id in DeviceIds("BTHENUM", presentOnly: false))
            {
                // Vendor 004C (Bluetooth SIG numbering) or 05AC (USB numbering): Apple.
                var match = AppleBluetoothId().Match(id);
                if (match.Success && id.Contains(address, StringComparison.OrdinalIgnoreCase))
                {
                    productId = match.Groups[1].Value;
                    break;
                }
            }

            return productId is not null
                   && DeviceIds("USB", presentOnly: true).Any(id => id.StartsWith($@"USB\VID_05AC&PID_{productId}", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            Log.Info($"USB keyboard check failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    [GeneratedRegex(@"_VID&(?:0001004C|000205AC)_PID&([0-9A-F]{4})", RegexOptions.IgnoreCase)]
    private static partial Regex AppleBluetoothId();

    /// <summary>Device instance IDs of an enumerator ("USB", "BTHENUM"), optionally only of devices that are present.</summary>
    private static List<string> DeviceIds(string enumerator, bool presentOnly)
    {
        const uint FilterEnumerator = 0x00000001, FilterPresent = 0x00000100;
        const int Success = 0, BufferSmall = 0x1A;
        uint flags = FilterEnumerator | (presentOnly ? FilterPresent : 0);
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (CM_Get_Device_ID_List_SizeW(out uint length, enumerator, flags) != Success || length <= 1)
            {
                return [];
            }

            var buffer = new char[length];
            int result = CM_Get_Device_ID_ListW(enumerator, buffer, length, flags);
            if (result == Success)
            {
                return new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries).ToList();
            }

            if (result != BufferSmall)
            {
                return [];
            }
        }

        return [];
    }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_ID_List_SizeW(out uint length, string? filter, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_ID_ListW(string? filter, [Out] char[] buffer, uint length, uint flags);
}
