using System.Buffers.Binary;
using FluentAssertions;
using NohddX.Iscsi.Protocol;
using Xunit;

namespace NohddX.Tests.Iscsi;

/// <summary>
/// Byte-exact tests for <see cref="IscsiPdu.WriteFramedAsync"/> — the on-wire
/// splicing of CRC32C HeaderDigest / DataDigest values. The unit tests in
/// <see cref="DigestNegotiationTests"/> verify the LOGIN side (we pick CRC32C
/// when offered); these verify the FRAMING side (once picked, the bytes go
/// to the wire at the right offsets with the right CRCs).
///
/// A regression here is silent: an off-by-one in the digest offset means
/// the initiator drops every PDU after login and the boot fails halfway
/// through with no obvious error.
/// </summary>
public class DigestFramingTests
{
    private const int Hsz = IscsiConstants.HeaderSize; // 48

    [Fact]
    public async Task No_digests_writes_exact_pdu_bytes_unchanged()
    {
        var pdu = MakePdu(headerByte0: IscsiConstants.OpcodeScsiResponse, dataLen: 16);

        var written = await WriteToMemoryAsync(pdu, headerDigest: false, dataDigest: false);

        written.Length.Should().Be(pdu.Length,
            "no digests means we write the PDU bytes verbatim, no extra trailers");
        written.Should().Equal(pdu, "byte-for-byte unchanged");
    }

    [Fact]
    public async Task HeaderDigest_appends_4_bytes_of_CRC32C_after_BHS()
    {
        var pdu = MakePdu(headerByte0: IscsiConstants.OpcodeScsiDataIn, dataLen: 32);

        var written = await WriteToMemoryAsync(pdu, headerDigest: true, dataDigest: false);

        // Expected layout: [48 BHS][4 HD][32 padded data]
        written.Length.Should().Be(Hsz + 4 + 32);

        var bhsSentBack = written.AsSpan(0, Hsz).ToArray();
        bhsSentBack.Should().Equal(pdu.AsSpan(0, Hsz).ToArray(), "BHS bytes copied through verbatim");

        // HD is CRC32C over the 48 BHS bytes ONLY (not over the digest itself).
        uint expectedHd = Crc32C.Compute(pdu.AsSpan(0, Hsz));
        uint actualHd = BinaryPrimitives.ReadUInt32BigEndian(written.AsSpan(Hsz, 4));
        actualHd.Should().Be(expectedHd, "HD is CRC32C of the 48-byte BHS, big-endian");

        var dataSentBack = written.AsSpan(Hsz + 4, 32).ToArray();
        dataSentBack.Should().Equal(pdu.AsSpan(Hsz, 32).ToArray(), "data segment unchanged");
    }

    [Fact]
    public async Task DataDigest_appends_4_bytes_after_padded_data()
    {
        var pdu = MakePdu(headerByte0: IscsiConstants.OpcodeScsiDataIn, dataLen: 32);

        var written = await WriteToMemoryAsync(pdu, headerDigest: false, dataDigest: true);

        // Expected: [48 BHS][32 data][4 DD]
        written.Length.Should().Be(Hsz + 32 + 4);

        // DD is CRC32C of the padded data segment ONLY.
        uint expectedDd = Crc32C.Compute(pdu.AsSpan(Hsz, 32));
        uint actualDd = BinaryPrimitives.ReadUInt32BigEndian(written.AsSpan(Hsz + 32, 4));
        actualDd.Should().Be(expectedDd, "DD is CRC32C of the padded data segment, big-endian");
    }

    [Fact]
    public async Task Both_digests_produce_full_layout_BHS_HD_data_DD()
    {
        var pdu = MakePdu(headerByte0: IscsiConstants.OpcodeScsiDataIn, dataLen: 16);

        // Precompute CRCs OUTSIDE the await so we don't hold Span<byte>
        // across an async boundary (forbidden in C# 12).
        var expectedHd = Crc32C.Compute(pdu.AsSpan(0, Hsz));
        var expectedDd = Crc32C.Compute(pdu.AsSpan(Hsz, 16));
        var expectedData = pdu.AsSpan(Hsz, 16).ToArray();

        var written = await WriteToMemoryAsync(pdu, headerDigest: true, dataDigest: true);

        // Full layout: [48 BHS][4 HD][16 data][4 DD] = 72
        written.Length.Should().Be(Hsz + 4 + 16 + 4);

        BinaryPrimitives.ReadUInt32BigEndian(written.AsSpan(Hsz, 4)).Should().Be(expectedHd);
        BinaryPrimitives.ReadUInt32BigEndian(written.AsSpan(Hsz + 4 + 16, 4)).Should().Be(expectedDd);

        // Data is at offset Hsz+4 = 52, NOT at Hsz=48 — easy to get wrong
        // and not catch unless you look for it specifically.
        var dataInWritten = written.AsSpan(Hsz + 4, 16).ToArray();
        dataInWritten.Should().Equal(expectedData, "data segment lives at offset 52, after the HD");
    }

    [Fact]
    public async Task DataDigest_with_zero_length_data_writes_no_trailer()
    {
        // SCSI Response with no data segment. DataDigest is requested but
        // there's nothing to digest — RFC 3720 §10.2.2 says we must NOT
        // emit a DD trailer for empty data. Otherwise the initiator reads
        // 4 mystery bytes as the next PDU's BHS prefix.
        var pdu = MakePdu(headerByte0: IscsiConstants.OpcodeScsiResponse, dataLen: 0);

        var written = await WriteToMemoryAsync(pdu, headerDigest: false, dataDigest: true);

        written.Length.Should().Be(Hsz, "no data -> no DD trailer");
    }

    [Fact]
    public async Task HeaderDigest_only_with_zero_data_writes_BHS_plus_HD()
    {
        var pdu = MakePdu(headerByte0: IscsiConstants.OpcodeScsiResponse, dataLen: 0);

        var written = await WriteToMemoryAsync(pdu, headerDigest: true, dataDigest: false);

        // [48 BHS][4 HD]
        written.Length.Should().Be(Hsz + 4);
        uint hd = BinaryPrimitives.ReadUInt32BigEndian(written.AsSpan(Hsz, 4));
        hd.Should().Be(Crc32C.Compute(pdu.AsSpan(0, Hsz)));
    }

    [Theory]
    [InlineData(0)]        // empty data
    [InlineData(1)]        // single byte (padded to 4)
    [InlineData(4)]        // exactly aligned
    [InlineData(7)]        // unaligned (padded to 8)
    [InlineData(8192)]     // typical iPXE max segment
    public async Task DataDigest_covers_padded_data_at_all_sizes(int dataLen)
    {
        // MakePdu rounds dataLen up to a 4-byte multiple (matches what
        // ToBytes does in production). The on-wire data length is the
        // padded value, not the input.
        var paddedLen = dataLen == 0 ? 0 : (dataLen + 3) & ~3;
        var pdu = MakePdu(headerByte0: IscsiConstants.OpcodeScsiDataIn, dataLen: dataLen);
        var expectedDd = paddedLen == 0 ? 0u : Crc32C.Compute(pdu.AsSpan(Hsz, paddedLen));

        var written = await WriteToMemoryAsync(pdu, headerDigest: false, dataDigest: true);

        if (paddedLen == 0)
        {
            written.Length.Should().Be(Hsz);
            return;
        }

        written.Length.Should().Be(Hsz + paddedLen + 4);
        uint dd = BinaryPrimitives.ReadUInt32BigEndian(written.AsSpan(Hsz + paddedLen, 4));
        dd.Should().Be(expectedDd);
    }

    [Fact]
    public async Task WriteFramedAsync_rejects_input_shorter_than_BHS()
    {
        var truncated = new byte[10];
        var ms = new MemoryStream();

        Func<Task> act = async () =>
            await IscsiPdu.WriteFramedAsync(ms, truncated, headerDigest: false, dataDigest: false);

        await act.Should().ThrowAsync<ArgumentException>(
            "an under-48-byte buffer can't be a valid PDU — fail loudly rather than send garbage");
    }

    // ── Helpers ────────────────────────────────────────────────────────

    /// <summary>
    /// Constructs a fake PDU byte buffer: 48-byte BHS with a recognizable
    /// pattern + a data segment of <paramref name="dataLen"/> bytes (already
    /// padded to a 4-byte boundary; caller passes the padded size). The BHS
    /// data-length bytes (5-7) get set to <paramref name="dataLen"/> so any
    /// downstream sanity check sees the right shape.
    /// </summary>
    private static byte[] MakePdu(byte headerByte0, int dataLen)
    {
        // Padded dataLen must be a 4-byte multiple to mirror what ToBytes()
        // emits in real life.
        if (dataLen % 4 != 0 && dataLen != 0)
            dataLen = (dataLen + 3) & ~3;

        var pdu = new byte[Hsz + dataLen];
        pdu[0] = headerByte0;
        pdu[1] = 0x80;
        // Data segment length in bytes 5-7 (24-bit BE)
        pdu[5] = (byte)((dataLen >> 16) & 0xFF);
        pdu[6] = (byte)((dataLen >> 8) & 0xFF);
        pdu[7] = (byte)(dataLen & 0xFF);

        // Recognizable BHS body so CRC values are sensitive to ordering.
        for (int i = 8; i < Hsz; i++) pdu[i] = (byte)((i * 13) & 0xFF);

        // Recognizable data body.
        for (int i = 0; i < dataLen; i++) pdu[Hsz + i] = (byte)((i * 17 + 1) & 0xFF);

        return pdu;
    }

    private static async Task<byte[]> WriteToMemoryAsync(
        byte[] pduBytes, bool headerDigest, bool dataDigest)
    {
        using var ms = new MemoryStream();
        await IscsiPdu.WriteFramedAsync(ms, pduBytes, headerDigest, dataDigest);
        return ms.ToArray();
    }
}
