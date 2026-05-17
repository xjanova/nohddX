using System.Buffers.Binary;
using Microsoft.Extensions.Logging;
using NohddX.Iscsi.Protocol;
using NohddX.Iscsi.Session;

namespace NohddX.Iscsi.Handlers;

/// <summary>
/// Handles SCSI commands received inside iSCSI PDUs.
/// Supports the minimal set of commands required for PXE network boot:
/// Inquiry, ReadCapacity, Read, Write, TestUnitReady, ModeSense, ReportLuns.
/// </summary>
public class ScsiCommandHandler
{
    private readonly ILogger<ScsiCommandHandler> _logger;

    /// <summary>
    /// Hard upper bound on Data-In payload size. Per-session negotiation
    /// during login produces a smaller value in <see cref="IscsiSession.MaxRecvDataSegmentLength"/>;
    /// this constant exists only to cap pathological negotiations.
    /// </summary>
    private const int MaxDataInPayloadCap = 262144;

    public ScsiCommandHandler(ILogger<ScsiCommandHandler> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Dispatch a SCSI command PDU to the appropriate handler.
    /// Returns one or more response PDUs to send back to the initiator.
    /// </summary>
    public async Task<List<IscsiPdu>> HandleCommandAsync(IscsiPdu request, IscsiSession session)
    {
        byte[] cdb = ExtractCdb(request);
        if (cdb.Length == 0)
        {
            _logger.LogWarning("Empty CDB received from session {SessionId}.", session.SessionId);
            return HandleUnsupported(request, session, 0x00);
        }

        return cdb[0] switch
        {
            IscsiConstants.ScsiTestUnitReady => HandleTestUnitReady(request, session),
            IscsiConstants.ScsiInquiry => HandleInquiry(request, session),
            IscsiConstants.ScsiReadCapacity10 => HandleReadCapacity10(request, session),
            IscsiConstants.ScsiReadCapacity16 => HandleReadCapacity16(request, session),
            IscsiConstants.ScsiRead10 => await HandleRead10Async(request, session),
            IscsiConstants.ScsiWrite10 => await HandleWrite10Async(request, session),
            IscsiConstants.ScsiRead16 => await HandleRead16Async(request, session),
            IscsiConstants.ScsiWrite16 => await HandleWrite16Async(request, session),
            IscsiConstants.ScsiModeSense6 => HandleModeSense6(request, session),
            IscsiConstants.ScsiReportLuns => HandleReportLuns(request, session),
            _ => HandleUnsupported(request, session, cdb[0])
        };
    }

    /// <summary>
    /// Extract the 16-byte CDB from a SCSI Command PDU.
    /// Per RFC 7143, the CDB occupies bytes 32-47 of the BHS.
    /// </summary>
    private static byte[] ExtractCdb(IscsiPdu request)
    {
        if (request.HeaderBytes.Length < IscsiConstants.HeaderSize)
            return Array.Empty<byte>();

        var cdb = new byte[16];
        Array.Copy(request.HeaderBytes, 32, cdb, 0, 16);
        return cdb;
    }

    // ---------------------------------------------------------------
    // TEST UNIT READY (0x00)
    // ---------------------------------------------------------------
    private List<IscsiPdu> HandleTestUnitReady(IscsiPdu request, IscsiSession session)
    {
        byte status = session.DiskStream != null
            ? IscsiConstants.StatusGood
            : IscsiConstants.StatusCheckCondition;

        return new List<IscsiPdu> { IscsiPdu.BuildScsiResponse(request, session, status) };
    }

    // ---------------------------------------------------------------
    // INQUIRY (0x12)
    // ---------------------------------------------------------------
    private List<IscsiPdu> HandleInquiry(IscsiPdu request, IscsiSession session)
    {
        byte[] cdb = ExtractCdb(request);
        bool evpd = (cdb[1] & 0x01) != 0;
        byte pageCode = cdb[2];
        int allocationLength = BinaryPrimitives.ReadUInt16BigEndian(cdb.AsSpan(3));
        if (allocationLength == 0) allocationLength = 36;

        byte[] data;
        if (evpd)
        {
            data = BuildVpdPage(pageCode);
        }
        else
        {
            data = BuildStandardInquiry();
        }

        // Trim to allocation length
        if (data.Length > allocationLength)
        {
            var trimmed = new byte[allocationLength];
            Array.Copy(data, trimmed, allocationLength);
            data = trimmed;
        }

        var dataIn = IscsiPdu.BuildDataIn(request, session, data, finalPdu: true, IscsiConstants.StatusGood);
        return new List<IscsiPdu> { dataIn };
    }

    private static byte[] BuildStandardInquiry()
    {
        var data = new byte[36];
        data[0] = 0x00; // Peripheral qualifier = 0, device type = 0 (disk)
        data[1] = 0x00; // Not removable
        data[2] = 0x05; // SPC-3
        data[3] = 0x02; // Response data format = 2
        data[4] = 31;   // Additional length

        // Vendor identification (bytes 8-15): "NohddX  "
        SetAsciiField(data, 8, 8, "NohddX");
        // Product identification (bytes 16-31): "VirtualDisk     "
        SetAsciiField(data, 16, 16, "VirtualDisk");
        // Product revision (bytes 32-35): "1.0 "
        SetAsciiField(data, 32, 4, "1.0");

        return data;
    }

    private static byte[] BuildVpdPage(byte pageCode)
    {
        return pageCode switch
        {
            0x00 => BuildVpdSupportedPages(),
            0x83 => BuildVpdDeviceIdentification(),
            _ => BuildVpdSupportedPages()
        };
    }

    private static byte[] BuildVpdSupportedPages()
    {
        return new byte[]
        {
            0x00, // Peripheral qualifier/device type
            0x00, // Page code
            0x00, // Reserved
            0x02, // Page length
            0x00, // Supported VPD pages
            0x83, // Device identification
        };
    }

    private static byte[] BuildVpdDeviceIdentification()
    {
        // Minimal device identification page
        var data = new byte[8];
        data[0] = 0x00; // Peripheral qualifier/device type
        data[1] = 0x83; // Page code
        data[2] = 0x00; // Page length MSB
        data[3] = 0x04; // Page length LSB
        data[4] = 0x02; // ASCII, vendor-specific
        data[5] = 0x01; // T10 vendor ID
        data[6] = 0x00; // Reserved
        data[7] = 0x00; // Identifier length
        return data;
    }

    // ---------------------------------------------------------------
    // READ CAPACITY (10) (0x25)
    // ---------------------------------------------------------------
    private List<IscsiPdu> HandleReadCapacity10(IscsiPdu request, IscsiSession session)
    {
        if (session.DiskStream == null)
            return new List<IscsiPdu> { IscsiPdu.BuildScsiResponse(request, session, IscsiConstants.StatusCheckCondition) };

        long totalBytes = session.DiskStream.Length;
        long totalSectors = totalBytes / IscsiConstants.SectorSize;
        // ReadCapacity10 returns the LBA of the last sector (not count)
        uint lastLba = (uint)Math.Min(totalSectors - 1, uint.MaxValue);

        var data = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(0), lastLba);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(4), IscsiConstants.SectorSize);

        var dataIn = IscsiPdu.BuildDataIn(request, session, data, finalPdu: true, IscsiConstants.StatusGood);
        return new List<IscsiPdu> { dataIn };
    }

    // ---------------------------------------------------------------
    // READ CAPACITY (16) / SERVICE ACTION IN (0x9E)
    // ---------------------------------------------------------------
    private List<IscsiPdu> HandleReadCapacity16(IscsiPdu request, IscsiSession session)
    {
        byte[] cdb = ExtractCdb(request);
        byte serviceAction = (byte)(cdb[1] & 0x1F);

        // Service action 0x10 = Read Capacity 16
        if (serviceAction != 0x10)
            return HandleUnsupported(request, session, cdb[0]);

        if (session.DiskStream == null)
            return new List<IscsiPdu> { IscsiPdu.BuildScsiResponse(request, session, IscsiConstants.StatusCheckCondition) };

        long totalBytes = session.DiskStream.Length;
        long totalSectors = totalBytes / IscsiConstants.SectorSize;
        long lastLba = totalSectors - 1;

        var data = new byte[32];
        BinaryPrimitives.WriteInt64BigEndian(data.AsSpan(0), lastLba);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(8), IscsiConstants.SectorSize);

        var dataIn = IscsiPdu.BuildDataIn(request, session, data, finalPdu: true, IscsiConstants.StatusGood);
        return new List<IscsiPdu> { dataIn };
    }

    // ---------------------------------------------------------------
    // READ (10) (0x28)
    // ---------------------------------------------------------------
    private async Task<List<IscsiPdu>> HandleRead10Async(IscsiPdu request, IscsiSession session)
    {
        byte[] cdb = ExtractCdb(request);
        uint lba = BinaryPrimitives.ReadUInt32BigEndian(cdb.AsSpan(2));
        ushort transferLength = BinaryPrimitives.ReadUInt16BigEndian(cdb.AsSpan(7));
        return await ReadSectorsAsync(request, session, lba, transferLength);
    }

    // ---------------------------------------------------------------
    // READ (16) (0x88)
    // ---------------------------------------------------------------
    private async Task<List<IscsiPdu>> HandleRead16Async(IscsiPdu request, IscsiSession session)
    {
        byte[] cdb = ExtractCdb(request);
        long lba = BinaryPrimitives.ReadInt64BigEndian(cdb.AsSpan(2));
        uint transferLength = BinaryPrimitives.ReadUInt32BigEndian(cdb.AsSpan(10));
        return await ReadSectorsAsync(request, session, lba, transferLength);
    }

    private async Task<List<IscsiPdu>> ReadSectorsAsync(IscsiPdu request, IscsiSession session, long lba, uint sectorCount)
    {
        if (session.DiskStream == null)
            return new List<IscsiPdu> { IscsiPdu.BuildScsiResponse(request, session, IscsiConstants.StatusCheckCondition) };

        long diskOffset = lba * IscsiConstants.SectorSize;
        int totalBytes = (int)(sectorCount * IscsiConstants.SectorSize);

        if (totalBytes == 0)
            return new List<IscsiPdu> { IscsiPdu.BuildScsiResponse(request, session, IscsiConstants.StatusGood) };

        // Two-level chunking per RFC 3720:
        //   * MaxRecvDataSegmentLength caps a SINGLE PDU's data segment.
        //   * MaxBurstLength caps a SEQUENCE of PDUs the target may send
        //     before pausing (= "burst"). At each burst boundary F=1,
        //     DataSN resets to 0; only the LAST burst additionally sets the
        //     S-bit + SCSI status.
        // BufferOffset always advances regardless of burst boundary — it's
        // a running offset in the initiator's SCSI buffer.
        int maxPayload = Math.Clamp(session.MaxRecvDataSegmentLength, 512, MaxDataInPayloadCap);
        int maxBurst = Math.Clamp(session.MaxBurstLength, maxPayload, MaxDataInPayloadCap);

        var pdus = new List<IscsiPdu>();
        int bufferOffset = 0;
        int bytesRemaining = totalBytes;

        while (bytesRemaining > 0)
        {
            int burstBudget = Math.Min(bytesRemaining, maxBurst);
            uint dataSn = 0;

            while (burstBudget > 0)
            {
                int chunkSize = Math.Min(burstBudget, maxPayload);
                var buffer = new byte[chunkSize];

                session.DiskStream.Position = diskOffset + bufferOffset;
                int bytesRead = await session.DiskStream.ReadAsync(buffer.AsMemory(0, chunkSize));

                if (bytesRead < chunkSize)
                {
                    // Short read near end of disk — shrink buffer.
                    var actual = new byte[bytesRead];
                    Array.Copy(buffer, actual, bytesRead);
                    buffer = actual;
                }
                if (buffer.Length == 0)
                    break;

                burstBudget -= buffer.Length;
                bytesRemaining -= buffer.Length;

                bool isBurstFinal = burstBudget == 0 || bytesRemaining == 0;
                bool isCommandFinal = bytesRemaining == 0;

                // 0xFF is the "no SCSI status here" sentinel that ToBytes()
                // checks when deciding whether to set the S-bit + status byte.
                byte status = isCommandFinal ? IscsiConstants.StatusGood : (byte)0xFF;

                var dataIn = IscsiPdu.BuildDataIn(request, session, buffer,
                    finalPdu: isBurstFinal,
                    scsiStatus: status,
                    dataSn: dataSn,
                    bufferOffset: (uint)bufferOffset);

                pdus.Add(dataIn);
                bufferOffset += buffer.Length;
                dataSn++;
            }
        }

        return pdus;
    }

    // ---------------------------------------------------------------
    // WRITE (10) (0x2A)
    // ---------------------------------------------------------------
    private async Task<List<IscsiPdu>> HandleWrite10Async(IscsiPdu request, IscsiSession session)
    {
        byte[] cdb = ExtractCdb(request);
        uint lba = BinaryPrimitives.ReadUInt32BigEndian(cdb.AsSpan(2));
        ushort transferLength = BinaryPrimitives.ReadUInt16BigEndian(cdb.AsSpan(7));
        return await WriteSectorsAsync(request, session, lba, transferLength);
    }

    // ---------------------------------------------------------------
    // WRITE (16) (0x8A)
    // ---------------------------------------------------------------
    private async Task<List<IscsiPdu>> HandleWrite16Async(IscsiPdu request, IscsiSession session)
    {
        byte[] cdb = ExtractCdb(request);
        long lba = BinaryPrimitives.ReadInt64BigEndian(cdb.AsSpan(2));
        uint transferLength = BinaryPrimitives.ReadUInt32BigEndian(cdb.AsSpan(10));
        return await WriteSectorsAsync(request, session, lba, transferLength);
    }

    private async Task<List<IscsiPdu>> WriteSectorsAsync(IscsiPdu request, IscsiSession session, long lba, uint sectorCount)
    {
        if (session.DiskStream == null)
            return new List<IscsiPdu> { IscsiPdu.BuildScsiResponse(request, session, IscsiConstants.StatusCheckCondition) };

        long offset = lba * IscsiConstants.SectorSize;
        int totalBytes = (int)(sectorCount * IscsiConstants.SectorSize);
        byte[] inlineData = request.DataSegment;

        // Fast path: everything fits in immediate data (typical when the
        // initiator's first-burst is ≥ the write size). Write and we're done.
        if (inlineData.Length >= totalBytes)
        {
            session.DiskStream.Position = offset;
            await session.DiskStream.WriteAsync(inlineData.AsMemory(0, totalBytes));
            // NOTE: don't FlushAsync per write — CowDiskStream.FlushAsync would
            // re-serialise the BlockMap on every PDU. Dispose flushes.
            return new List<IscsiPdu> { IscsiPdu.BuildScsiResponse(request, session, IscsiConstants.StatusGood) };
        }

        // Slow path: write is larger than what the initiator put inline. Write
        // whatever inline data we got, then issue an R2T asking for the rest.
        // The Data-Out PDUs that follow will be picked up by HandleDataOutAsync
        // and the SCSI Response will be sent only when the last byte lands.
        if (inlineData.Length > 0)
        {
            session.DiskStream.Position = offset;
            await session.DiskStream.WriteAsync(inlineData);
        }

        var ttt = session.NextR2tTag++;
        session.PendingWrites[request.InitiatorTaskTag] = new PendingWrite
        {
            InitiatorTaskTag = request.InitiatorTaskTag,
            Lun = request.LUN,
            TargetTransferTag = ttt,
            DiskOffsetNext = offset + inlineData.Length,
            TotalLength = totalBytes,
            BytesReceived = inlineData.Length,
            R2TSN = 0,
        };

        uint desired = (uint)(totalBytes - inlineData.Length);
        var r2t = IscsiPdu.BuildR2T(request, session,
            targetTransferTag: ttt,
            r2tSn: 0,
            bufferOffset: (uint)inlineData.Length,
            desiredLength: desired);

        _logger.LogDebug(
            "Write needs R2T: ITT=0x{Itt:X8} have={Have} need={Total} -> requesting {Desired} bytes at offset {Offset}",
            request.InitiatorTaskTag, inlineData.Length, totalBytes, desired, inlineData.Length);

        return new List<IscsiPdu> { r2t };
    }

    // ---------------------------------------------------------------
    // SCSI Data-Out (0x05) — follow-up to R2T
    // ---------------------------------------------------------------

    /// <summary>
    /// Process a Data-Out PDU sent by the initiator in response to an R2T we
    /// previously issued. When the last PDU lands (F=1 and we've received the
    /// full requested length), this method returns a SCSI Response PDU that
    /// completes the original Write command.
    /// </summary>
    public async Task<List<IscsiPdu>> HandleDataOutAsync(IscsiPdu request, IscsiSession session)
    {
        if (!session.PendingWrites.TryGetValue(request.InitiatorTaskTag, out var pending))
        {
            _logger.LogWarning(
                "Data-Out for unknown ITT 0x{Itt:X8} on session {SessionId} — dropping",
                request.InitiatorTaskTag, session.SessionId);
            return new List<IscsiPdu>();
        }

        if (session.DiskStream == null)
        {
            session.PendingWrites.Remove(request.InitiatorTaskTag);
            return new List<IscsiPdu> { IscsiPdu.BuildScsiResponse(request, session, IscsiConstants.StatusCheckCondition) };
        }

        // Most initiators (Microsoft iSCSI, Linux open-iscsi) send Data-Out
        // sequentially with BufferOffset matching our running offset. If the
        // initiator does set BufferOffset, honour it — that's the RFC contract.
        if (request.DataSegment.Length > 0)
        {
            long diskOffset = pending.DiskOffsetNext;
            session.DiskStream.Position = diskOffset;
            await session.DiskStream.WriteAsync(request.DataSegment);

            pending.DiskOffsetNext += request.DataSegment.Length;
            pending.BytesReceived += request.DataSegment.Length;
        }

        // Initiator marks Final on the last Data-Out PDU of this burst.
        if (request.Final && pending.BytesReceived >= pending.TotalLength)
        {
            session.PendingWrites.Remove(request.InitiatorTaskTag);
            return new List<IscsiPdu> { IscsiPdu.BuildScsiResponse(request, session, IscsiConstants.StatusGood) };
        }

        // Not done yet — initiator will keep sending. Nothing to ACK.
        return new List<IscsiPdu>();
    }

    // ---------------------------------------------------------------
    // MODE SENSE (6) (0x1A)
    // ---------------------------------------------------------------
    private List<IscsiPdu> HandleModeSense6(IscsiPdu request, IscsiSession session)
    {
        // Return a minimal mode sense response
        var data = new byte[4];
        data[0] = 3;    // Mode data length (remaining bytes)
        data[1] = 0;    // Medium type (default)
        data[2] = 0x00; // Device-specific parameter: not write-protected
        data[3] = 0;    // Block descriptor length

        var dataIn = IscsiPdu.BuildDataIn(request, session, data, finalPdu: true, IscsiConstants.StatusGood);
        return new List<IscsiPdu> { dataIn };
    }

    // ---------------------------------------------------------------
    // REPORT LUNS (0xA0)
    // ---------------------------------------------------------------
    private List<IscsiPdu> HandleReportLuns(IscsiPdu request, IscsiSession session)
    {
        // Report a single LUN (LUN 0)
        var data = new byte[16];
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(0), 8); // LUN list length = 8 (one LUN entry)
        // Bytes 4-7: reserved
        // Bytes 8-15: LUN 0 (all zeros)

        var dataIn = IscsiPdu.BuildDataIn(request, session, data, finalPdu: true, IscsiConstants.StatusGood);
        return new List<IscsiPdu> { dataIn };
    }

    // ---------------------------------------------------------------
    // UNSUPPORTED
    // ---------------------------------------------------------------
    private List<IscsiPdu> HandleUnsupported(IscsiPdu request, IscsiSession session, byte opcode)
    {
        _logger.LogDebug("Unsupported SCSI command 0x{Opcode:X2} from session {SessionId}.",
            opcode, session.SessionId);

        // Return CHECK CONDITION with ILLEGAL REQUEST sense key
        var response = IscsiPdu.BuildScsiResponse(request, session, IscsiConstants.StatusCheckCondition);

        // Attach sense data (fixed format, ILLEGAL REQUEST / INVALID COMMAND OPERATION CODE)
        var senseData = new byte[18];
        senseData[0] = 0x70; // Current errors, fixed format
        senseData[2] = 0x05; // Sense key: ILLEGAL REQUEST
        senseData[7] = 10;   // Additional sense length
        senseData[12] = 0x20; // ASC: INVALID COMMAND OPERATION CODE
        senseData[13] = 0x00; // ASCQ

        response.DataSegment = senseData;
        response.DataSegmentLength = (uint)senseData.Length;

        return new List<IscsiPdu> { response };
    }

    /// <summary>
    /// Write a left-justified, space-padded ASCII field into a byte array.
    /// </summary>
    private static void SetAsciiField(byte[] buffer, int offset, int length, string value)
    {
        for (int i = 0; i < length; i++)
        {
            buffer[offset + i] = (byte)(i < value.Length ? value[i] : ' ');
        }
    }
}
