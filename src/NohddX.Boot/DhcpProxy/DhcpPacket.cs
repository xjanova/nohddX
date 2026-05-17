using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace NohddX.Boot.DhcpProxy;

/// <summary>
/// Parses and builds DHCP packets for PXE proxy operations.
/// Handles the 240-byte fixed header and variable-length DHCP options.
/// </summary>
public class DhcpPacket
{
    // DHCP magic cookie identifying DHCP options section
    private static readonly byte[] MagicCookie = { 0x63, 0x82, 0x53, 0x63 };

    private const int FixedHeaderLength = 240;
    private const int ClientHardwareAddressFieldLength = 16;
    private const int ServerHostNameFieldLength = 64;
    private const int BootFileNameFieldLength = 128;

    // DHCP option codes
    private const byte OptionEnd = 255;
    private const byte OptionPad = 0;
    private const byte OptionMessageType = 53;
    private const byte OptionServerIdentifier = 54;
    private const byte OptionVendorClassIdentifier = 60;
    private const byte OptionClientArchitecture = 93;

    public byte Op { get; set; }           // 1=BOOTREQUEST, 2=BOOTREPLY
    public byte HType { get; set; }        // 1=Ethernet
    public byte HLen { get; set; }         // 6 for MAC addresses
    public byte Hops { get; set; }
    public uint TransactionId { get; set; }
    public ushort Seconds { get; set; }
    public ushort Flags { get; set; }
    public IPAddress ClientIp { get; set; } = IPAddress.Any;
    public IPAddress YourIp { get; set; } = IPAddress.Any;
    public IPAddress ServerIp { get; set; } = IPAddress.Any;
    public IPAddress GatewayIp { get; set; } = IPAddress.Any;
    public byte[] ClientHardwareAddress { get; set; } = new byte[ClientHardwareAddressFieldLength];
    public string ServerHostName { get; set; } = "";
    public string BootFileName { get; set; } = "";
    public Dictionary<byte, byte[]> Options { get; set; } = new();

    /// <summary>
    /// Returns the MAC address as a hyphen-separated uppercase hex string (e.g., "AA-BB-CC-DD-EE-FF").
    /// </summary>
    public string GetMacAddress()
    {
        return string.Join("-", ClientHardwareAddress.Take(HLen).Select(b => b.ToString("X2")));
    }

    /// <summary>
    /// Gets the DHCP message type from option 53.
    /// Returns null if option 53 is not present.
    /// 1=DISCOVER, 2=OFFER, 3=REQUEST, 4=DECLINE, 5=ACK, 6=NAK, 7=RELEASE, 8=INFORM
    /// </summary>
    public byte? GetMessageType()
    {
        return Options.TryGetValue(OptionMessageType, out var v) && v.Length > 0 ? v[0] : null;
    }

    /// <summary>
    /// Gets the vendor class identifier from option 60 (e.g., "PXEClient:Arch:00000:UNDI:002001").
    /// </summary>
    public string? GetVendorClassIdentifier()
    {
        return Options.TryGetValue(OptionVendorClassIdentifier, out var v) ? Encoding.ASCII.GetString(v) : null;
    }

    /// <summary>
    /// Gets the client system architecture type from option 93.
    /// 0=Intel x86PC, 6=EFI IA32, 7=EFI BC, 9=EFI x86-64
    /// </summary>
    public ushort? GetClientArchitecture()
    {
        return Options.TryGetValue(OptionClientArchitecture, out var v) && v.Length >= 2
            ? BinaryPrimitives.ReadUInt16BigEndian(v)
            : null;
    }

    /// <summary>
    /// Parses a DHCP packet from raw bytes received on the wire.
    /// </summary>
    /// <param name="data">Raw UDP payload containing the DHCP packet.</param>
    /// <returns>A parsed <see cref="DhcpPacket"/> instance.</returns>
    /// <exception cref="ArgumentException">Thrown when data is too short or magic cookie is invalid.</exception>
    public static DhcpPacket Parse(byte[] data)
    {
        if (data.Length < FixedHeaderLength)
            throw new ArgumentException($"DHCP packet too short: {data.Length} bytes (minimum {FixedHeaderLength}).");

        var packet = new DhcpPacket
        {
            Op = data[0],
            HType = data[1],
            HLen = Math.Min(data[2], (byte)ClientHardwareAddressFieldLength),
            Hops = data[3],
            TransactionId = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(4)),
            Seconds = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(8)),
            Flags = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(10)),
            ClientIp = new IPAddress(data.AsSpan(12, 4)),
            YourIp = new IPAddress(data.AsSpan(16, 4)),
            ServerIp = new IPAddress(data.AsSpan(20, 4)),
            GatewayIp = new IPAddress(data.AsSpan(24, 4)),
        };

        // Copy client hardware address (bytes 28-43)
        Array.Copy(data, 28, packet.ClientHardwareAddress, 0, ClientHardwareAddressFieldLength);

        // Server host name (bytes 44-107, null-terminated ASCII)
        packet.ServerHostName = ReadNullTerminatedAscii(data, 44, ServerHostNameFieldLength);

        // Boot file name (bytes 108-235, null-terminated ASCII)
        packet.BootFileName = ReadNullTerminatedAscii(data, 108, BootFileNameFieldLength);

        // Verify magic cookie at bytes 236-239
        if (data.Length >= 240 &&
            data[236] == MagicCookie[0] && data[237] == MagicCookie[1] &&
            data[238] == MagicCookie[2] && data[239] == MagicCookie[3])
        {
            // Parse DHCP options starting at byte 240
            ParseOptions(data.AsSpan(240), packet.Options);
        }

        return packet;
    }

    /// <summary>
    /// Serializes this DHCP packet back to wire format bytes.
    /// </summary>
    public byte[] ToBytes()
    {
        using var ms = new MemoryStream(512);
        using var writer = new BinaryWriter(ms);

        // Fixed header (bytes 0-27)
        writer.Write(Op);
        writer.Write(HType);
        writer.Write(HLen);
        writer.Write(Hops);

        Span<byte> buf4 = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(buf4, TransactionId);
        writer.Write(buf4);

        Span<byte> buf2 = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(buf2, Seconds);
        writer.Write(buf2);

        BinaryPrimitives.WriteUInt16BigEndian(buf2, Flags);
        writer.Write(buf2);

        writer.Write(ClientIp.GetAddressBytes());
        writer.Write(YourIp.GetAddressBytes());
        writer.Write(ServerIp.GetAddressBytes());
        writer.Write(GatewayIp.GetAddressBytes());

        // Client hardware address (16 bytes)
        writer.Write(ClientHardwareAddress);

        // Server host name (64 bytes, null-padded)
        WriteFixedAscii(writer, ServerHostName, ServerHostNameFieldLength);

        // Boot file name (128 bytes, null-padded)
        WriteFixedAscii(writer, BootFileName, BootFileNameFieldLength);

        // Magic cookie
        writer.Write(MagicCookie);

        // DHCP options
        foreach (var (code, value) in Options)
        {
            if (code == OptionEnd || code == OptionPad) continue;
            writer.Write(code);
            writer.Write((byte)value.Length);
            writer.Write(value);
        }

        // End option
        writer.Write(OptionEnd);

        return ms.ToArray();
    }

    private static void ParseOptions(ReadOnlySpan<byte> data, Dictionary<byte, byte[]> options)
    {
        int offset = 0;
        while (offset < data.Length)
        {
            byte code = data[offset++];

            if (code == OptionEnd)
                break;

            if (code == OptionPad)
                continue;

            if (offset >= data.Length) break;
            byte length = data[offset++];

            if (offset + length > data.Length) break;

            options[code] = data.Slice(offset, length).ToArray();
            offset += length;
        }
    }

    private static string ReadNullTerminatedAscii(byte[] data, int offset, int maxLength)
    {
        int end = offset;
        int limit = Math.Min(offset + maxLength, data.Length);
        while (end < limit && data[end] != 0) end++;
        return end > offset ? Encoding.ASCII.GetString(data, offset, end - offset) : "";
    }

    private static void WriteFixedAscii(BinaryWriter writer, string value, int fieldLength)
    {
        var bytes = new byte[fieldLength];
        if (!string.IsNullOrEmpty(value))
        {
            var encoded = Encoding.ASCII.GetBytes(value);
            Array.Copy(encoded, bytes, Math.Min(encoded.Length, fieldLength));
        }
        writer.Write(bytes);
    }
}
