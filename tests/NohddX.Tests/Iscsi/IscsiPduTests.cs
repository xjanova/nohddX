using FluentAssertions;
using NohddX.Iscsi.Protocol;
using Xunit;

namespace NohddX.Tests.Iscsi;

public class IscsiPduTests
{
    [Fact]
    public void ParseTextData_extracts_key_value_pairs()
    {
        // iSCSI text data: "Key1=Val1\0Key2=Val2\0"
        var data = System.Text.Encoding.ASCII.GetBytes(
            "InitiatorName=iqn.1993-08.org.debian:01:foo\0" +
            "TargetName=iqn.2024.com.nohddx:client-1\0" +
            "MaxRecvDataSegmentLength=8192\0");

        var parsed = IscsiPdu.ParseTextData(data);

        parsed.Should().ContainKey("InitiatorName")
            .WhoseValue.Should().Be("iqn.1993-08.org.debian:01:foo");
        parsed.Should().ContainKey("TargetName")
            .WhoseValue.Should().Be("iqn.2024.com.nohddx:client-1");
        parsed.Should().ContainKey("MaxRecvDataSegmentLength")
            .WhoseValue.Should().Be("8192");
    }

    [Fact]
    public void BuildTextData_round_trips_through_ParseTextData()
    {
        var dict = new Dictionary<string, string>
        {
            ["HeaderDigest"] = "None",
            ["DataDigest"] = "None",
            ["MaxRecvDataSegmentLength"] = "65536"
        };

        var text = IscsiPdu.BuildTextData(dict);
        var data = System.Text.Encoding.ASCII.GetBytes(text);
        var parsed = IscsiPdu.ParseTextData(data);

        parsed.Should().BeEquivalentTo(dict);
    }

    [Fact]
    public void Parse_short_header_throws()
    {
        var shortHeader = new byte[10];
        Action act = () => IscsiPdu.Parse(shortHeader, Array.Empty<byte>());
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Parse_extracts_opcode_and_immediate_flag()
    {
        var header = new byte[IscsiConstants.HeaderSize];
        // Opcode = 0x05 (SCSI Command), Immediate = 1
        header[0] = 0x40 | 0x05;
        header[1] = 0x80; // Final
        var pdu = IscsiPdu.Parse(header, Array.Empty<byte>());

        pdu.Opcode.Should().Be(0x05);
        pdu.Immediate.Should().BeTrue();
        pdu.Final.Should().BeTrue();
    }
}
