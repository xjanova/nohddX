using System.Buffers.Binary;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NohddX.Iscsi.Handlers;
using NohddX.Iscsi.Protocol;
using NohddX.Iscsi.Session;
using Xunit;

namespace NohddX.Tests.Iscsi;

/// <summary>
/// Multi-PDU and multi-burst Data-In framing for SCSI Read. Verifies that
/// when a single Read-10 spans more than one burst, the target obeys
/// RFC 3720 §10.7 / §12.13: F-bit on each burst-final PDU, DataSN reset to
/// 0 per burst, BufferOffset advancing continuously, S-bit only on the
/// command-final PDU.
/// </summary>
public class BurstReadTests
{
    private const int Sector = 512;

    [Fact]
    public async Task Single_pdu_read_sets_F_and_S_bits_with_zero_offsets()
    {
        // Smallest case: read fits in one Data-In, so F=1, S=1, DataSN=0, BufferOffset=0.
        var disk = NewDisk(64 * 1024, fillPattern: true);
        var session = NewSession(disk, maxRecv: 8192, maxBurst: 262144);
        var handler = new ScsiCommandHandler(NullLogger<ScsiCommandHandler>.Instance);

        var pdus = await Invoke(handler, BuildRead10(lba: 0, sectors: 1, itt: 0x100), session);

        pdus.Should().HaveCount(1);
        AssertWireFields(pdus[0], expectedDataSn: 0, expectedBufferOffset: 0,
                         expectedFBit: true, expectedSBit: true);
    }

    [Fact]
    public async Task Multi_pdu_single_burst_resets_DataSN_per_pdu_not_per_read()
    {
        // 8 sectors = 4096 bytes, max payload 1024 -> 4 PDUs, all in one
        // burst (burst=262144 fits). DataSN should be 0,1,2,3 and only the
        // last PDU has F=1 (burst-final == command-final here).
        var disk = NewDisk(64 * 1024, fillPattern: true);
        var session = NewSession(disk, maxRecv: 1024, maxBurst: 262144);
        var handler = new ScsiCommandHandler(NullLogger<ScsiCommandHandler>.Instance);

        var pdus = await Invoke(handler, BuildRead10(lba: 0, sectors: 8, itt: 0x200), session);

        pdus.Should().HaveCount(4);

        for (int i = 0; i < 4; i++)
        {
            bool isLast = i == 3;
            AssertWireFields(pdus[i],
                expectedDataSn: (uint)i,
                expectedBufferOffset: (uint)(i * 1024),
                expectedFBit: isLast,
                expectedSBit: isLast);
        }
    }

    [Fact]
    public async Task Multi_burst_read_resets_DataSN_at_each_burst_boundary()
    {
        // Read 8 KB with maxRecv=1024 (1 KB/PDU) and maxBurst=2048 (2 KB/burst).
        // That yields 4 bursts of 2 PDUs each:
        //   burst 0: PDUs (DataSN 0, offset    0, F=0) (DataSN 1, offset 1024, F=1, S=0)
        //   burst 1: PDUs (DataSN 0, offset 2048, F=0) (DataSN 1, offset 3072, F=1, S=0)
        //   burst 2: PDUs (DataSN 0, offset 4096, F=0) (DataSN 1, offset 5120, F=1, S=0)
        //   burst 3: PDUs (DataSN 0, offset 6144, F=0) (DataSN 1, offset 7168, F=1, S=1)
        var disk = NewDisk(64 * 1024, fillPattern: true);
        var session = NewSession(disk, maxRecv: 1024, maxBurst: 2048);
        var handler = new ScsiCommandHandler(NullLogger<ScsiCommandHandler>.Instance);

        var pdus = await Invoke(handler, BuildRead10(lba: 0, sectors: 16, itt: 0x300), session);

        pdus.Should().HaveCount(8);

        // PDU index, expected DataSN, expected BufferOffset, F, S
        var expected = new (uint DataSn, uint Offset, bool F, bool S)[]
        {
            (0, 0,    false, false),
            (1, 1024, true,  false),  // burst 0 end
            (0, 2048, false, false),
            (1, 3072, true,  false),  // burst 1 end
            (0, 4096, false, false),
            (1, 5120, true,  false),  // burst 2 end
            (0, 6144, false, false),
            (1, 7168, true,  true),   // command final
        };

        for (int i = 0; i < expected.Length; i++)
        {
            var (sn, off, f, s) = expected[i];
            AssertWireFields(pdus[i],
                expectedDataSn: sn,
                expectedBufferOffset: off,
                expectedFBit: f,
                expectedSBit: s);
        }
    }

    [Fact]
    public async Task Bytes_reassembled_in_BufferOffset_order_match_disk_content()
    {
        // Belt-and-braces correctness: every PDU's payload must land at its
        // claimed BufferOffset, and concatenating them by BufferOffset must
        // reproduce the source disk bytes exactly.
        var disk = NewDisk(64 * 1024, fillPattern: true);
        var session = NewSession(disk, maxRecv: 1024, maxBurst: 2048);
        var handler = new ScsiCommandHandler(NullLogger<ScsiCommandHandler>.Instance);

        var pdus = await Invoke(handler, BuildRead10(lba: 10, sectors: 16, itt: 0x400), session);

        var reassembled = new byte[16 * Sector];
        foreach (var pdu in pdus)
        {
            uint offset = ReadUInt32BE(pdu.ToBytes(), 40);
            Array.Copy(pdu.DataSegment, 0, reassembled, (int)offset, pdu.DataSegment.Length);
        }

        long diskOffset = 10 * Sector;
        var expected = new byte[16 * Sector];
        disk.Position = diskOffset;
        disk.Read(expected, 0, expected.Length);

        reassembled.Should().Equal(expected,
            "byte-level reassembly of Data-In PDUs must match the source disk content");
    }

    // ── Helpers ────────────────────────────────────────────────────────

    private static MemoryStream NewDisk(int size, bool fillPattern)
    {
        var buf = new byte[size];
        if (fillPattern)
        {
            // Deterministic non-zero pattern so a misplaced BufferOffset
            // would be visible in the reassembly test.
            for (int i = 0; i < buf.Length; i++) buf[i] = (byte)((i * 31 + 7) & 0xFF);
        }
        return new MemoryStream(buf, writable: true);
    }

    private static IscsiSession NewSession(MemoryStream disk, int maxRecv, int maxBurst) =>
        new(Guid.NewGuid().ToString())
        {
            DiskStream = disk,
            IsFullFeaturePhase = true,
            AuthCompleted = true,
            MaxRecvDataSegmentLength = maxRecv,
            MaxBurstLength = maxBurst,
        };

    private static IscsiPdu BuildRead10(long lba, uint sectors, uint itt)
    {
        var header = new byte[IscsiConstants.HeaderSize];
        header[0] = IscsiConstants.OpcodeScsiCommand;
        header[1] = 0x80; // F
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(16), itt);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(20), sectors * (uint)Sector);
        header[32] = IscsiConstants.ScsiRead10;
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(34), (uint)lba);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(39), (ushort)sectors);
        return IscsiPdu.Parse(header, Array.Empty<byte>());
    }

    private static Task<List<IscsiPdu>> Invoke(ScsiCommandHandler h, IscsiPdu pdu, IscsiSession s) =>
        h.HandleCommandAsync(pdu, s);

    /// <summary>
    /// Assert the on-the-wire bytes (not just the PDU object) carry the right
    /// DataSN, BufferOffset, F-bit and S-bit. Bypasses the object model so a
    /// regression in ToBytes() can't hide a bug.
    /// </summary>
    private static void AssertWireFields(IscsiPdu pdu, uint expectedDataSn, uint expectedBufferOffset,
        bool expectedFBit, bool expectedSBit)
    {
        var bytes = pdu.ToBytes();
        bool fBit = (bytes[1] & 0x80) != 0;
        bool sBit = (bytes[1] & 0x01) != 0;
        uint dataSn = ReadUInt32BE(bytes, 36);
        uint bufferOffset = ReadUInt32BE(bytes, 40);

        fBit.Should().Be(expectedFBit, "F-bit on PDU");
        sBit.Should().Be(expectedSBit, "S-bit on PDU");
        dataSn.Should().Be(expectedDataSn, "DataSN at byte 36-39");
        bufferOffset.Should().Be(expectedBufferOffset, "BufferOffset at byte 40-43");

        if (expectedSBit)
            bytes[3].Should().Be(IscsiConstants.StatusGood, "status byte set on command-final PDU");
        else
            bytes[3].Should().Be(0, "status byte is reserved (0) when S=0");
    }

    private static uint ReadUInt32BE(byte[] buf, int offset) =>
        BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(offset));
}
