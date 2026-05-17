using System.Text;
using FluentAssertions;
using NohddX.Iscsi.Protocol;
using Xunit;

namespace NohddX.Tests.Iscsi;

/// <summary>
/// Verifies the Castagnoli CRC-32C implementation against published test
/// vectors. iSCSI digest correctness depends entirely on this being right —
/// a wrong polynomial here means every digested PDU is rejected on the wire.
/// </summary>
public class Crc32CTests
{
    [Theory]
    // Test vector from RFC 3720 Appendix B and Wikipedia: "123456789" -> 0xE3069283
    [InlineData("123456789", 0xE3069283u)]
    // Empty input -> initial 0xFFFFFFFF XOR 0xFFFFFFFF = 0
    [InlineData("", 0x00000000u)]
    // Single byte "a" -> 0xC1D04330 (computable independently)
    [InlineData("a", 0xC1D04330u)]
    public void Compute_matches_published_test_vectors(string ascii, uint expected)
    {
        var bytes = Encoding.ASCII.GetBytes(ascii);
        Crc32C.Compute(bytes).Should().Be(expected,
            "wrong CRC32C means every digested iSCSI PDU on the wire would be rejected");
    }

    [Fact]
    public void Compute_is_deterministic()
    {
        // Same input must always produce the same hash — no hidden state.
        var data = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x00, 0x11, 0x22, 0x33 };
        var first = Crc32C.Compute(data);
        var second = Crc32C.Compute(data);
        second.Should().Be(first);
    }

    [Fact]
    public void Compute_is_sensitive_to_single_bit_changes()
    {
        // The whole point of a CRC is that a 1-bit flip changes the result.
        var a = new byte[] { 0x00, 0x00, 0x00, 0x00 };
        var b = new byte[] { 0x00, 0x00, 0x00, 0x01 };
        Crc32C.Compute(a).Should().NotBe(Crc32C.Compute(b));
    }
}
