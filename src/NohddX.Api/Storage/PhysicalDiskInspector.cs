using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using NohddX.Api.DTOs;

namespace NohddX.Api.Storage;

/// <summary>
/// Enumerates physical disks (NOT volumes — that's what
/// <see cref="System.IO.DriveInfo"/> covers, and the
/// <c>/api/storage/disks</c> endpoint already returns those). This is the
/// SMART-equivalent view: model, serial, interface, temperature when
/// available, and a per-disk health bucket derived from
/// <c>MSStorageDriver_FailurePredictStatus</c>.
///
/// Linux / macOS hosts get an empty list — the API still works, the client
/// just sees no physical-disk rows. A future Linux implementation could
/// shell out to <c>smartctl --json</c> and parse the output.
/// </summary>
public static class PhysicalDiskInspector
{
    public static IReadOnlyList<PhysicalDiskResponse> Enumerate()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return Array.Empty<PhysicalDiskResponse>();

        try
        {
            return EnumerateWindows();
        }
        catch
        {
            // WMI can throw under restricted accounts, in containers without
            // the WMI service, etc. Better to return an empty list than to
            // 500 the entire storage page.
            return Array.Empty<PhysicalDiskResponse>();
        }
    }

    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<PhysicalDiskResponse> EnumerateWindows()
    {
        var rows = new List<PhysicalDiskResponse>();

        // Map device-id -> SMART status (root\WMI namespace, separate from
        // the default root\CIMV2 where Win32_DiskDrive lives).
        var smartByInstance = QuerySmartStatuses();
        var tempByInstance = QueryTemperatures();

        using var searcher = new ManagementObjectSearcher(
            "SELECT Model, SerialNumber, InterfaceType, MediaType, Size, " +
            "Status, PNPDeviceID FROM Win32_DiskDrive");

        foreach (ManagementObject mo in searcher.Get())
        {
            using (mo)
            {
                var pnp = (mo["PNPDeviceID"] as string ?? "").Replace('\\', '\\').Replace("\\", "\\\\");
                // SMART rows key by InstanceName which usually looks like
                // "<pnp-device-id>_0" — we match by prefix.
                string? smartReason = null;
                bool predictFailure = false;
                foreach (var kv in smartByInstance)
                {
                    if (kv.Key.StartsWith(pnp, StringComparison.OrdinalIgnoreCase))
                    {
                        predictFailure = kv.Value.PredictFailure;
                        smartReason = kv.Value.Reason;
                        break;
                    }
                }
                uint? temperatureC = null;
                foreach (var kv in tempByInstance)
                {
                    if (kv.Key.StartsWith(pnp, StringComparison.OrdinalIgnoreCase))
                    {
                        temperatureC = kv.Value;
                        break;
                    }
                }

                long? sizeBytes = mo["Size"] is ulong u ? (long?)u : null;
                string health = !string.Equals(mo["Status"] as string, "OK", StringComparison.OrdinalIgnoreCase)
                    ? "Failing"
                    : predictFailure ? "Warning"
                    : "Good";

                rows.Add(new PhysicalDiskResponse(
                    Model: (mo["Model"] as string ?? "Unknown").Trim(),
                    SerialNumber: (mo["SerialNumber"] as string)?.Trim(),
                    InterfaceType: mo["InterfaceType"] as string,
                    MediaType: mo["MediaType"] as string,
                    SizeBytes: sizeBytes,
                    TemperatureCelsius: temperatureC,
                    Status: mo["Status"] as string ?? "Unknown",
                    Health: health,
                    PredictFailure: predictFailure,
                    SmartReason: smartReason));
            }
        }

        return rows;
    }

    [SupportedOSPlatform("windows")]
    private static Dictionary<string, (bool PredictFailure, string? Reason)> QuerySmartStatuses()
    {
        var result = new Dictionary<string, (bool, string?)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var scope = new ManagementScope(@"\\.\root\WMI");
            scope.Connect();

            using var searcher = new ManagementObjectSearcher(scope,
                new ObjectQuery("SELECT InstanceName, PredictFailure, Reason FROM MSStorageDriver_FailurePredictStatus"));

            foreach (ManagementObject mo in searcher.Get())
            {
                using (mo)
                {
                    var name = mo["InstanceName"] as string;
                    if (string.IsNullOrEmpty(name)) continue;
                    bool pred = mo["PredictFailure"] is bool b && b;
                    string? reason = mo["Reason"]?.ToString();
                    result[name] = (pred, reason);
                }
            }
        }
        catch
        {
            // WMI namespace can be unavailable in containers; ignore.
        }
        return result;
    }

    [SupportedOSPlatform("windows")]
    private static Dictionary<string, uint> QueryTemperatures()
    {
        var result = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var scope = new ManagementScope(@"\\.\root\WMI");
            scope.Connect();

            // MSStorageDriver_ATAPISmartData carries the raw 512-byte SMART
            // attribute table; byte at offset 0x4 of the temperature attribute
            // (id 0xC2) holds the current temperature in degrees Celsius.
            // The exact offset depends on which attributes the drive reports,
            // so we walk the attribute list.
            using var searcher = new ManagementObjectSearcher(scope,
                new ObjectQuery("SELECT InstanceName, VendorSpecific FROM MSStorageDriver_ATAPISmartData"));

            foreach (ManagementObject mo in searcher.Get())
            {
                using (mo)
                {
                    var name = mo["InstanceName"] as string;
                    if (string.IsNullOrEmpty(name)) continue;
                    if (mo["VendorSpecific"] is not byte[] data) continue;

                    // SMART data: 2 byte header, then 30 12-byte attribute entries.
                    // Attribute entry layout: [id(1)][flags(2)][current(1)][worst(1)][raw(6)][reserved(1)]
                    for (int i = 2; i + 12 <= data.Length; i += 12)
                    {
                        byte attrId = data[i];
                        if (attrId == 0xC2) // Temperature
                        {
                            // Raw value at offset i+5; LSB is current celsius
                            uint temp = data[i + 5];
                            if (temp > 0 && temp < 150) // sanity clamp
                                result[name] = temp;
                            break;
                        }
                    }
                }
            }
        }
        catch
        {
            // Temperature isn't critical — skip silently if WMI rejects.
        }
        return result;
    }
}
