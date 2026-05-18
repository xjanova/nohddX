using System.Text.Json;
using NohddX.Api.DTOs;

namespace NohddX.Api.Storage;

/// <summary>
/// Parses <c>smartctl --json</c> output into our wire shape. Kept separate
/// from the shell-out path so the parser can be unit-tested with a captured
/// JSON sample (the shell call itself requires a Linux box with smartctl
/// installed and at least one disk, which CI may not have).
///
/// Compatible with smartctl 7.x JSON schema. Earlier versions had no JSON
/// flag and aren't supported.
/// </summary>
public static class SmartctlJsonParser
{
    /// <summary>
    /// Parses <c>smartctl --scan -j</c> output and returns the list of
    /// device paths (e.g. <c>["/dev/sda", "/dev/nvme0"]</c>).
    /// </summary>
    public static IReadOnlyList<string> ParseScan(string json)
    {
        var devices = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("devices", out var arr) ||
                arr.ValueKind != JsonValueKind.Array)
            {
                return devices;
            }
            foreach (var d in arr.EnumerateArray())
            {
                if (d.TryGetProperty("name", out var name) &&
                    name.ValueKind == JsonValueKind.String)
                {
                    var path = name.GetString();
                    if (!string.IsNullOrEmpty(path)) devices.Add(path);
                }
            }
        }
        catch (JsonException)
        {
            // Garbled / partial output — return whatever we collected.
        }
        return devices;
    }

    /// <summary>
    /// Parses <c>smartctl -a -j /dev/&lt;dev&gt;</c> output into a single
    /// <see cref="PhysicalDiskResponse"/>. Returns null if the JSON is
    /// not recognisable as a smartctl info blob (missing model + serial).
    /// </summary>
    public static PhysicalDiskResponse? ParseInfo(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string? model = TryGetString(root, "model_name");
            string? serial = TryGetString(root, "serial_number");

            // If we got NEITHER model nor serial it's almost certainly not
            // a real disk info response — could be an error blob.
            if (string.IsNullOrEmpty(model) && string.IsNullOrEmpty(serial))
                return null;

            long? sizeBytes = null;
            if (root.TryGetProperty("user_capacity", out var cap) &&
                cap.TryGetProperty("bytes", out var bytes) &&
                bytes.ValueKind == JsonValueKind.Number)
            {
                sizeBytes = bytes.GetInt64();
            }

            uint? temperatureC = null;
            if (root.TryGetProperty("temperature", out var temp) &&
                temp.TryGetProperty("current", out var cur) &&
                cur.ValueKind == JsonValueKind.Number)
            {
                int c = cur.GetInt32();
                if (c > 0 && c < 150) temperatureC = (uint)c;
            }

            bool? smartPassed = null;
            string? smartReason = null;
            if (root.TryGetProperty("smart_status", out var st))
            {
                if (st.TryGetProperty("passed", out var p) && p.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    smartPassed = p.GetBoolean();
                // Some smartctl versions emit a "msg" or "string" under
                // smart_status when failed; surface that for ops to see.
                smartReason = TryGetString(st, "string") ?? TryGetString(st, "msg");
            }

            string? protocol = null;
            if (root.TryGetProperty("device", out var dev))
                protocol = TryGetString(dev, "protocol") ?? TryGetString(dev, "type");

            // SATA / SAS rotation flag — drives that report 1 RPM are SSDs;
            // anything else is HDD. Some NVMe devices omit this entirely.
            string? mediaType = null;
            if (root.TryGetProperty("rotation_rate", out var rr) && rr.ValueKind == JsonValueKind.Number)
            {
                int rpm = rr.GetInt32();
                mediaType = rpm == 0 ? "SSD" : $"HDD ({rpm} RPM)";
            }
            else if (string.Equals(protocol, "NVMe", StringComparison.OrdinalIgnoreCase))
            {
                mediaType = "NVMe SSD";
            }

            string status = smartPassed switch
            {
                true => "OK",
                false => "FAILING",
                null => "Unknown",
            };

            string health = smartPassed switch
            {
                true => "Good",
                false => "Failing",
                null => "Good", // be optimistic when smartctl can't tell
            };

            return new PhysicalDiskResponse(
                Model: string.IsNullOrEmpty(model) ? "Unknown" : model,
                SerialNumber: serial,
                InterfaceType: protocol,
                MediaType: mediaType,
                SizeBytes: sizeBytes,
                TemperatureCelsius: temperatureC,
                Status: status,
                Health: health,
                PredictFailure: smartPassed == false,
                SmartReason: smartReason);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? TryGetString(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}
