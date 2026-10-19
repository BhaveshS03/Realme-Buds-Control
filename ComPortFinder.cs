using System.Management;
using System.Text.RegularExpressions;

namespace RealmeBuds;

/// <summary>
/// Finds the Bluetooth virtual COM port Windows created for a paired device
/// (same WMI lookup the Python tools use: the device address is embedded in the PNP id).
/// </summary>
public static class ComPortFinder
{
    public static Task<string?> FindAsync(ulong address) => Task.Run<string?>(() =>
    {
        try
        {
            string mac = address.ToString("X12");
            using var searcher = new ManagementObjectSearcher("SELECT DeviceID, PNPDeviceID FROM Win32_SerialPort");
            foreach (ManagementObject o in searcher.Get())
            {
                string pnp = o["PNPDeviceID"] as string ?? "";
                var m = Regex.Match(pnp, "&([0-9A-Fa-f]{12})_");
                if (m.Success && m.Groups[1].Value.Equals(mac, StringComparison.OrdinalIgnoreCase))
                    return o["DeviceID"] as string;
            }
        }
        catch
        {
            // WMI unavailable: caller falls back to the RFCOMM socket
        }
        return null;
    });
}
