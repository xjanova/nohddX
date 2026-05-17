using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace NohddX.ClientMgmt.WakeOnLan;

public class WolService
{
    private readonly ILogger<WolService> _logger;

    public WolService(ILogger<WolService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Sends a Wake-on-LAN magic packet to wake a single machine by MAC address.
    /// </summary>
    public async Task WakeAsync(string macAddress, CancellationToken ct = default)
    {
        var macBytes = ParseMac(macAddress);

        // Build magic packet: 6 bytes of 0xFF followed by 16 repetitions of the MAC
        var magicPacket = new byte[102];
        for (int i = 0; i < 6; i++)
        {
            magicPacket[i] = 0xFF;
        }

        for (int i = 0; i < 16; i++)
        {
            Array.Copy(macBytes, 0, magicPacket, 6 + i * 6, 6);
        }

        // Send via UDP broadcast on port 9
        using var udp = new UdpClient();
        udp.EnableBroadcast = true;
        await udp.SendAsync(magicPacket, magicPacket.Length,
            new IPEndPoint(IPAddress.Broadcast, 9));

        _logger.LogInformation("WoL magic packet sent to {Mac}", macAddress);
    }

    /// <summary>
    /// Sends Wake-on-LAN magic packets to multiple machines with a small inter-packet delay.
    /// </summary>
    public async Task WakeMultipleAsync(
        IEnumerable<string> macAddresses,
        CancellationToken ct = default)
    {
        foreach (var mac in macAddresses)
        {
            ct.ThrowIfCancellationRequested();
            await WakeAsync(mac, ct);
            await Task.Delay(50, ct); // Small delay between packets to avoid flooding
        }
    }

    /// <summary>
    /// Parses a MAC address string in various formats into a 6-byte array.
    /// Accepts formats: AA:BB:CC:DD:EE:FF, AA-BB-CC-DD-EE-FF, AABBCCDDEEFF
    /// </summary>
    private static byte[] ParseMac(string mac)
    {
        var cleaned = mac.Replace(":", "").Replace("-", "").Replace(".", "");
        if (cleaned.Length != 12)
        {
            throw new ArgumentException($"Invalid MAC address format: {mac}");
        }

        var bytes = new byte[6];
        for (int i = 0; i < 6; i++)
        {
            bytes[i] = Convert.ToByte(cleaned.Substring(i * 2, 2), 16);
        }

        return bytes;
    }
}
