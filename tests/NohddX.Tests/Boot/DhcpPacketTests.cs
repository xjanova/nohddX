using System.Net;
using FluentAssertions;
using NohddX.Boot.DhcpProxy;
using Xunit;

namespace NohddX.Tests.Boot;

public class DhcpPacketTests
{
    [Fact]
    public void Build_and_parse_round_trip_preserves_fields()
    {
        var original = new DhcpPacket
        {
            Op = 1,
            HType = 1,
            HLen = 6,
            Hops = 0,
            TransactionId = 0xDEADBEEFu,
            Seconds = 4,
            Flags = 0x8000,
            ClientIp = IPAddress.Parse("192.168.1.55"),
            YourIp = IPAddress.Any,
            ServerIp = IPAddress.Parse("192.168.1.1"),
            GatewayIp = IPAddress.Any,
            BootFileName = "snponly.efi"
        };

        // Set MAC AA-BB-CC-DD-EE-FF in client hardware addr
        var mac = new byte[] { 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF };
        Array.Copy(mac, original.ClientHardwareAddress, mac.Length);

        // Add a couple of options
        original.Options[53] = new byte[] { 1 }; // DISCOVER
        original.Options[60] = System.Text.Encoding.ASCII.GetBytes("PXEClient:Arch:00007:UNDI:003016");
        original.Options[93] = new byte[] { 0x00, 0x07 }; // EFI BC = UEFI

        var bytes = original.ToBytes();
        var parsed = DhcpPacket.Parse(bytes);

        parsed.Op.Should().Be(original.Op);
        parsed.HLen.Should().Be(original.HLen);
        parsed.TransactionId.Should().Be(original.TransactionId);
        parsed.ClientIp.Should().Be(original.ClientIp);
        parsed.ServerIp.Should().Be(original.ServerIp);
        parsed.BootFileName.Should().Be(original.BootFileName);
        parsed.GetMacAddress().Should().Be("AA-BB-CC-DD-EE-FF");
        parsed.GetMessageType().Should().Be(1);
        parsed.GetVendorClassIdentifier().Should().StartWith("PXEClient");
        parsed.GetClientArchitecture().Should().Be(7);
    }

    [Fact]
    public void Parse_oversized_options_does_not_throw()
    {
        // Truncated/garbled packet: just the magic cookie and an END
        var minimal = new byte[244];
        minimal[236] = 0x63;
        minimal[237] = 0x82;
        minimal[238] = 0x53;
        minimal[239] = 0x63;
        minimal[240] = 255; // OptionEnd
        minimal[0] = 1; // BOOTREQUEST
        minimal[1] = 1; // Ethernet
        minimal[2] = 6;

        Action act = () => DhcpPacket.Parse(minimal);
        act.Should().NotThrow();
    }
}
