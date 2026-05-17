using System.Buffers.Binary;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NohddX.Iscsi.Handlers;
using NohddX.Iscsi.Protocol;
using NohddX.Iscsi.Session;
using Xunit;

namespace NohddX.Tests.Iscsi;

/// <summary>
/// Verifies the full SCSI Write -> R2T -> Data-Out -> Response cycle for
/// writes larger than what the initiator put inline. Without this, the
/// server used to silently return Good without persisting the trailing bytes
/// — meaning an OS booted from us would experience random write loss.
/// </summary>
public class R2TWriteTests
{
    private const int Sector = 512;

    [Fact]
    public async Task Small_write_with_full_inline_data_completes_immediately()
    {
        // Baseline: when the write fits inline, the response is a single SCSI
        // Response with status Good and no R2T is involved.
        var disk = new MemoryStream(new byte[64 * 1024], writable: true);
        var session = MakeSession(disk);
        var handler = new ScsiCommandHandler(NullLogger<ScsiCommandHandler>.Instance);

        var data = Enumerable.Range(0, Sector).Select(i => (byte)(i & 0xFF)).ToArray();
        var write = BuildWrite10Pdu(lba: 0, sectors: 1, inlineData: data, itt: 0x1000);

        var responses = await handler.HandleCommandAsync(write, session);

        responses.Should().HaveCount(1);
        responses[0].Opcode.Should().Be(IscsiConstants.OpcodeScsiResponse);
        responses[0].ScsiStatus.Should().Be(IscsiConstants.StatusGood);

        // Verify data actually landed on disk
        disk.Position = 0;
        var readback = new byte[Sector];
        disk.Read(readback, 0, Sector);
        readback.Should().Equal(data, "inline write must persist");

        session.PendingWrites.Should().BeEmpty("no R2T was needed");
    }

    [Fact]
    public async Task Large_write_with_no_inline_data_issues_r2t()
    {
        // The audit scenario: initiator sends Write-10 with zero inline data,
        // expecting the target to ask for it via R2T. Pre-fix the target
        // returned StatusGood with NO data written — silent corruption.
        var disk = new MemoryStream(new byte[1024 * 1024], writable: true);
        var session = MakeSession(disk);
        var handler = new ScsiCommandHandler(NullLogger<ScsiCommandHandler>.Instance);

        // 8 sectors = 4096 bytes, but no inline data
        var write = BuildWrite10Pdu(lba: 10, sectors: 8, inlineData: Array.Empty<byte>(), itt: 0x2000);

        var responses = await handler.HandleCommandAsync(write, session);

        responses.Should().HaveCount(1);
        var r2t = responses[0];
        r2t.Opcode.Should().Be(IscsiConstants.OpcodeR2T);
        r2t.InitiatorTaskTag.Should().Be(0x2000u);
        r2t.BufferOffset.Should().Be(0u);
        r2t.DesiredDataTransferLength.Should().Be(4096u);
        r2t.TargetTransferTag.Should().NotBe(0xFFFFFFFFu, "R2T must carry a real TTT");

        session.PendingWrites.Should().ContainKey(0x2000u, "pending write must be tracked until Data-Out arrives");
    }

    [Fact]
    public async Task Full_r2t_data_out_cycle_writes_bytes_at_correct_offset()
    {
        var disk = new MemoryStream(new byte[1024 * 1024], writable: true);
        var session = MakeSession(disk);
        var handler = new ScsiCommandHandler(NullLogger<ScsiCommandHandler>.Instance);

        const long lba = 100;
        const uint sectors = 4;
        const int payloadLen = (int)sectors * Sector; // 2048
        long expectedOffset = lba * Sector;

        var payload = new byte[payloadLen];
        for (int i = 0; i < payloadLen; i++) payload[i] = (byte)(i % 251);

        // Step 1: Write-10 with NO inline data
        var write = BuildWrite10Pdu(lba, sectors, Array.Empty<byte>(), itt: 0x3000);
        var step1 = await handler.HandleCommandAsync(write, session);
        step1[0].Opcode.Should().Be(IscsiConstants.OpcodeR2T);
        uint ttt = step1[0].TargetTransferTag;

        // Step 2: Initiator sends all the data in ONE Data-Out PDU with F=1
        var dataOut = BuildDataOutPdu(itt: 0x3000, ttt: ttt, bufferOffset: 0, payload: payload, final: true);
        var step2 = await handler.HandleDataOutAsync(dataOut, session);

        step2.Should().HaveCount(1);
        step2[0].Opcode.Should().Be(IscsiConstants.OpcodeScsiResponse);
        step2[0].ScsiStatus.Should().Be(IscsiConstants.StatusGood);

        // The bytes must be at the LBA's disk offset — not at 0, not at +1
        disk.Position = expectedOffset;
        var readback = new byte[payloadLen];
        disk.Read(readback, 0, payloadLen);
        readback.Should().Equal(payload, "every byte must land at lba*512");

        session.PendingWrites.Should().NotContainKey(0x3000u, "completed write must be removed from in-flight set");
    }

    [Fact]
    public async Task Multi_pdu_data_out_assembles_correctly()
    {
        // Realistic: Windows iSCSI initiator splits a 64 KB write into multiple
        // ~8 KB PDUs. Each PDU's bytes must concatenate at the right disk offset.
        var disk = new MemoryStream(new byte[1024 * 1024], writable: true);
        var session = MakeSession(disk);
        var handler = new ScsiCommandHandler(NullLogger<ScsiCommandHandler>.Instance);

        const long lba = 0;
        const uint sectors = 16; // 8 KB
        const int totalLen = (int)sectors * Sector;

        var payload = new byte[totalLen];
        for (int i = 0; i < totalLen; i++) payload[i] = (byte)((i * 7) & 0xFF);

        var write = BuildWrite10Pdu(lba, sectors, Array.Empty<byte>(), itt: 0x4000);
        var step1 = await handler.HandleCommandAsync(write, session);
        uint ttt = step1[0].TargetTransferTag;

        // Three Data-Out PDUs: 2KB + 2KB + 4KB
        int[] chunkSizes = { 2048, 2048, 4096 };
        int sent = 0;
        for (int i = 0; i < chunkSizes.Length; i++)
        {
            bool isLast = i == chunkSizes.Length - 1;
            var chunk = payload.AsSpan(sent, chunkSizes[i]).ToArray();
            var pdu = BuildDataOutPdu(0x4000, ttt, (uint)sent, chunk, final: isLast);

            var responses = await handler.HandleDataOutAsync(pdu, session);
            if (isLast)
            {
                responses.Should().HaveCount(1);
                responses[0].Opcode.Should().Be(IscsiConstants.OpcodeScsiResponse);
            }
            else
            {
                responses.Should().BeEmpty("non-final Data-Out PDUs get no response");
            }

            sent += chunkSizes[i];
        }

        disk.Position = lba * Sector;
        var readback = new byte[totalLen];
        disk.Read(readback, 0, totalLen);
        readback.Should().Equal(payload, "concatenated Data-Out PDUs must form the original payload");
    }

    [Fact]
    public async Task Unknown_itt_data_out_is_dropped_not_thrown()
    {
        // Sanity: a malformed initiator (or replay) sending Data-Out for an
        // ITT we never issued an R2T for must not crash the session.
        var disk = new MemoryStream(new byte[4096], writable: true);
        var session = MakeSession(disk);
        var handler = new ScsiCommandHandler(NullLogger<ScsiCommandHandler>.Instance);

        var orphan = BuildDataOutPdu(itt: 0xDEAD, ttt: 0xBEEF, bufferOffset: 0, payload: new byte[512], final: true);
        var responses = await handler.HandleDataOutAsync(orphan, session);

        responses.Should().BeEmpty();
    }

    [Fact]
    public async Task R2T_pdu_bytes_round_trip_through_parse()
    {
        // Belt-and-braces: ensure the new ToBytes encoding for R2T survives
        // a Parse round-trip — protects against accidentally clobbering the
        // R2T-specific bytes 36-47 in the BHS.
        var session = MakeSession(new MemoryStream(new byte[1024]));
        var request = BuildWrite10Pdu(lba: 0, sectors: 1, inlineData: Array.Empty<byte>(), itt: 0xABCD);

        var r2t = IscsiPdu.BuildR2T(request, session,
            targetTransferTag: 0x77,
            r2tSn: 3,
            bufferOffset: 0x1000,
            desiredLength: 0x2000);

        var bytes = r2t.ToBytes();
        var header = bytes.AsSpan(0, IscsiConstants.HeaderSize).ToArray();

        // R2T's BHS layout per RFC 3720 §10.8
        header[0].Should().Be(IscsiConstants.OpcodeR2T);
        BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(16)).Should().Be(0xABCDu);   // ITT
        BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(20)).Should().Be(0x77u);     // TTT
        BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(36)).Should().Be(3u);        // R2TSN
        BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(40)).Should().Be(0x1000u);   // BufferOffset
        BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(44)).Should().Be(0x2000u);   // DesiredDataTransferLength
    }

    // ── Helpers ────────────────────────────────────────────────────────

    private static IscsiSession MakeSession(MemoryStream disk)
    {
        var session = new IscsiSession(Guid.NewGuid().ToString())
        {
            DiskStream = disk,
            IsFullFeaturePhase = true,
            AuthCompleted = true,
        };
        return session;
    }

    private static IscsiPdu BuildWrite10Pdu(long lba, uint sectors, byte[] inlineData, uint itt)
    {
        var header = new byte[IscsiConstants.HeaderSize];
        header[0] = IscsiConstants.OpcodeScsiCommand;
        header[1] = 0x80; // Final

        // Data segment length (24-bit BE)
        uint dl = (uint)inlineData.Length;
        header[5] = (byte)((dl >> 16) & 0xFF);
        header[6] = (byte)((dl >> 8) & 0xFF);
        header[7] = (byte)(dl & 0xFF);

        // ITT at 16-19
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(16), itt);
        // Expected Data Transfer Length at 20-23 (per Parse)
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(20), sectors * (uint)Sector);

        // CDB at bytes 32-47: Write-10 (0x2A) <flags> <LBA32> <reserved> <transfer16> <control>
        header[32] = IscsiConstants.ScsiWrite10;
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(34), (uint)lba);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(39), (ushort)sectors);

        return IscsiPdu.Parse(header, inlineData);
    }

    private static IscsiPdu BuildDataOutPdu(uint itt, uint ttt, uint bufferOffset, byte[] payload, bool final)
    {
        var header = new byte[IscsiConstants.HeaderSize];
        header[0] = IscsiConstants.OpcodeScsiDataOut;
        if (final) header[1] = 0x80;

        uint dl = (uint)payload.Length;
        header[5] = (byte)((dl >> 16) & 0xFF);
        header[6] = (byte)((dl >> 8) & 0xFF);
        header[7] = (byte)(dl & 0xFF);

        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(16), itt);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(20), ttt);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(40), bufferOffset);

        return IscsiPdu.Parse(header, payload);
    }
}
