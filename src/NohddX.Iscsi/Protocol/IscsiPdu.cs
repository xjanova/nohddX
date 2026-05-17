using System.Buffers.Binary;
using System.Text;
using NohddX.Iscsi.Session;

namespace NohddX.Iscsi.Protocol;

/// <summary>
/// Represents an iSCSI Protocol Data Unit (PDU).
/// Handles parsing and serialisation of the 48-byte Basic Header Segment (BHS)
/// plus a variable-length data segment.
/// </summary>
public class IscsiPdu
{
    // ---- BHS fields ----
    public byte Opcode { get; set; }
    public bool Final { get; set; }
    public bool Immediate { get; set; }
    public byte TotalAhsLength { get; set; }
    public uint DataSegmentLength { get; set; }
    public ulong LUN { get; set; }
    public uint InitiatorTaskTag { get; set; }
    public uint TargetTransferTag { get; set; } = 0xFFFFFFFF;

    // Command/status sequence numbers
    public uint CmdSN { get; set; }
    public uint ExpStatSN { get; set; }
    public uint StatSN { get; set; }
    public uint ExpCmdSN { get; set; }
    public uint MaxCmdSN { get; set; }

    // Login-specific
    public byte CurrentStage { get; set; }
    public byte NextStage { get; set; }
    public bool Transit { get; set; }
    public ushort ConnectionId { get; set; }
    public byte StatusClass { get; set; }
    public byte StatusDetail { get; set; }
    public ushort ISID_A { get; set; }
    public uint ISID_B { get; set; }
    public ushort TSIH { get; set; }

    // SCSI-specific
    public byte ScsiStatus { get; set; }
    public uint DataResidualCount { get; set; }
    public bool ReadFlag { get; set; }
    public bool WriteFlag { get; set; }
    public uint ExpectedDataTransferLength { get; set; }

    // R2T / Data-Out-specific (per RFC 3720 §10.7 / §10.8)
    public uint R2TSN { get; set; }
    public uint BufferOffset { get; set; }
    public uint DesiredDataTransferLength { get; set; }
    public uint DataSN { get; set; }

    /// <summary>Raw 48-byte header bytes.</summary>
    public byte[] HeaderBytes { get; set; } = new byte[IscsiConstants.HeaderSize];

    /// <summary>Data segment (variable length, may be empty).</summary>
    public byte[] DataSegment { get; set; } = Array.Empty<byte>();

    /// <summary>
    /// Parse a PDU from a 48-byte header and the accompanying data segment.
    /// </summary>
    public static IscsiPdu Parse(byte[] header, byte[] data)
    {
        if (header.Length < IscsiConstants.HeaderSize)
            throw new ArgumentException("Header must be at least 48 bytes.", nameof(header));

        var pdu = new IscsiPdu();
        Array.Copy(header, pdu.HeaderBytes, IscsiConstants.HeaderSize);

        pdu.Immediate = (header[0] & 0x40) != 0;
        pdu.Opcode = (byte)(header[0] & 0x3F);
        pdu.Final = (header[1] & 0x80) != 0;

        // Byte 1 flags depending on opcode
        pdu.ReadFlag = (header[1] & 0x40) != 0;
        pdu.WriteFlag = (header[1] & 0x20) != 0;

        // Login-specific: Transit (T) bit and CSG/NSG
        pdu.Transit = (header[1] & 0x80) != 0;
        pdu.CurrentStage = (byte)((header[1] >> 2) & 0x03);
        pdu.NextStage = (byte)(header[1] & 0x03);

        pdu.TotalAhsLength = header[4];

        // Data segment length: bytes 5-7 (24-bit big-endian)
        pdu.DataSegmentLength = (uint)((header[5] << 16) | (header[6] << 8) | header[7]);

        // LUN: bytes 8-15
        pdu.LUN = BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(8));

        // Initiator Task Tag: bytes 16-19
        pdu.InitiatorTaskTag = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(16));

        // Target Transfer Tag: bytes 20-23
        pdu.TargetTransferTag = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(20));

        // CmdSN: bytes 24-27
        pdu.CmdSN = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(24));

        // ExpStatSN / StatSN: bytes 28-31
        pdu.ExpStatSN = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(28));
        pdu.StatSN = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(28));

        // Bytes 32-35 (opcode-dependent: ExpCmdSN for responses, or additional fields)
        pdu.ExpCmdSN = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(32));

        // Bytes 36-39 (MaxCmdSN for responses, ExpectedDataTransferLength for commands)
        pdu.MaxCmdSN = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(36));
        pdu.ExpectedDataTransferLength = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(36));

        // Login-specific: ISID (bytes 8-13), TSIH (bytes 14-15), CID (bytes 20-21)
        if (pdu.Opcode == IscsiConstants.OpcodeLoginRequest || pdu.Opcode == IscsiConstants.OpcodeLoginResponse)
        {
            pdu.ISID_A = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(8));
            pdu.ISID_B = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(10));
            pdu.TSIH = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(14));
            pdu.ConnectionId = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(20));
            pdu.StatusClass = header[36];
            pdu.StatusDetail = header[37];
        }

        // SCSI Response: status in byte 3
        if (pdu.Opcode == IscsiConstants.OpcodeScsiResponse || pdu.Opcode == IscsiConstants.OpcodeScsiDataIn)
        {
            pdu.ScsiStatus = header[3];
        }

        // SCSI Command: Expected Data Transfer Length at bytes 20-23 (per RFC 3720 this is at offset 20 for commands)
        if (pdu.Opcode == IscsiConstants.OpcodeScsiCommand)
        {
            pdu.ExpectedDataTransferLength = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(20));
        }

        // SCSI Data-Out: TargetTransferTag is the same R2T tag we issued; the
        // BufferOffset (bytes 40-43) and DataSN (bytes 36-39) tell us where
        // this chunk lives in the original write request.
        if (pdu.Opcode == IscsiConstants.OpcodeScsiDataOut)
        {
            pdu.DataSN = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(36));
            pdu.BufferOffset = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(40));
            // F bit (Final) in Data-Out indicates "last PDU of this sequence"
            pdu.Final = (header[1] & 0x80) != 0;
        }

        pdu.DataSegment = data;

        return pdu;
    }

    /// <summary>
    /// Build a login response PDU.
    /// </summary>
    public static IscsiPdu BuildLoginResponse(IscsiPdu request, IscsiSession session, byte statusClass,
        byte statusDetail, byte currentStage, byte nextStage, bool transit, string? textData = null)
    {
        var response = new IscsiPdu
        {
            Opcode = IscsiConstants.OpcodeLoginResponse,
            Final = true,
            Transit = transit,
            CurrentStage = currentStage,
            NextStage = nextStage,
            InitiatorTaskTag = request.InitiatorTaskTag,
            StatusClass = statusClass,
            StatusDetail = statusDetail,
            StatSN = session.StatSN++,
            ExpCmdSN = session.ExpCmdSN,
            MaxCmdSN = session.ExpCmdSN + session.MaxCmdSN,
            ISID_A = request.ISID_A,
            ISID_B = request.ISID_B,
            TSIH = 1, // assigned TSIH
        };

        if (textData != null)
        {
            response.DataSegment = Encoding.UTF8.GetBytes(textData);
            response.DataSegmentLength = (uint)response.DataSegment.Length;
        }

        return response;
    }

    /// <summary>
    /// Build a SCSI response PDU.
    /// </summary>
    public static IscsiPdu BuildScsiResponse(IscsiPdu request, IscsiSession session, byte scsiStatus)
    {
        return new IscsiPdu
        {
            Opcode = IscsiConstants.OpcodeScsiResponse,
            Final = true,
            ScsiStatus = scsiStatus,
            InitiatorTaskTag = request.InitiatorTaskTag,
            StatSN = session.StatSN++,
            ExpCmdSN = session.ExpCmdSN,
            MaxCmdSN = session.ExpCmdSN + session.MaxCmdSN,
        };
    }

    /// <summary>
    /// Build a Data-In PDU carrying read data. <paramref name="dataSn"/> must
    /// reset to 0 at the start of each burst (sequence) and increment by 1
    /// for every PDU within the burst; <paramref name="bufferOffset"/> is the
    /// running offset in the SCSI read buffer. <paramref name="finalPdu"/>
    /// marks the last PDU of the burst — the very last burst additionally
    /// carries the SCSI status byte and the S-bit (handled by ToBytes()).
    /// </summary>
    public static IscsiPdu BuildDataIn(
        IscsiPdu request,
        IscsiSession session,
        byte[] data,
        bool finalPdu,
        byte scsiStatus = IscsiConstants.StatusGood,
        uint dataSn = 0,
        uint bufferOffset = 0)
    {
        var pdu = new IscsiPdu
        {
            Opcode = IscsiConstants.OpcodeScsiDataIn,
            Final = finalPdu,
            ScsiStatus = finalPdu ? scsiStatus : (byte)0,
            InitiatorTaskTag = request.InitiatorTaskTag,
            TargetTransferTag = 0xFFFFFFFF,
            StatSN = finalPdu ? session.StatSN++ : session.StatSN,
            ExpCmdSN = session.ExpCmdSN,
            MaxCmdSN = session.ExpCmdSN + session.MaxCmdSN,
            DataSegment = data,
            DataSegmentLength = (uint)data.Length,
            DataSN = dataSn,
            BufferOffset = bufferOffset,
        };
        return pdu;
    }

    /// <summary>
    /// Build an R2T (Ready To Transfer) PDU. Issued in response to a SCSI
    /// Write that didn't carry all of its data inline — tells the initiator
    /// "send me bytes [bufferOffset, bufferOffset+desiredLength) using
    /// targetTransferTag in subsequent Data-Out PDUs".
    /// </summary>
    public static IscsiPdu BuildR2T(
        IscsiPdu request,
        IscsiSession session,
        uint targetTransferTag,
        uint r2tSn,
        uint bufferOffset,
        uint desiredLength)
    {
        return new IscsiPdu
        {
            Opcode = IscsiConstants.OpcodeR2T,
            Final = true,
            InitiatorTaskTag = request.InitiatorTaskTag,
            TargetTransferTag = targetTransferTag,
            // R2T does NOT advance StatSN (RFC 3720 §10.8) — it's tracked only.
            StatSN = session.StatSN,
            ExpCmdSN = session.ExpCmdSN,
            MaxCmdSN = session.ExpCmdSN + session.MaxCmdSN,
            R2TSN = r2tSn,
            BufferOffset = bufferOffset,
            DesiredDataTransferLength = desiredLength,
        };
    }

    /// <summary>
    /// Build a NOP-In response.
    /// </summary>
    public static IscsiPdu BuildNopIn(IscsiPdu request, IscsiSession session)
    {
        return new IscsiPdu
        {
            Opcode = IscsiConstants.OpcodeNopIn,
            Final = true,
            InitiatorTaskTag = request.InitiatorTaskTag,
            TargetTransferTag = 0xFFFFFFFF,
            StatSN = session.StatSN++,
            ExpCmdSN = session.ExpCmdSN,
            MaxCmdSN = session.ExpCmdSN + session.MaxCmdSN,
        };
    }

    /// <summary>
    /// Build a logout response PDU.
    /// </summary>
    public static IscsiPdu BuildLogoutResponse(IscsiPdu request, IscsiSession session)
    {
        return new IscsiPdu
        {
            Opcode = IscsiConstants.OpcodeLogoutResponse,
            Final = true,
            InitiatorTaskTag = request.InitiatorTaskTag,
            StatSN = session.StatSN++,
            ExpCmdSN = session.ExpCmdSN,
            MaxCmdSN = session.ExpCmdSN + session.MaxCmdSN,
        };
    }

    /// <summary>
    /// Build a Text response PDU.
    /// </summary>
    public static IscsiPdu BuildTextResponse(IscsiPdu request, IscsiSession session, string textData)
    {
        var data = Encoding.UTF8.GetBytes(textData);
        return new IscsiPdu
        {
            Opcode = IscsiConstants.OpcodeTextResponse,
            Final = true,
            InitiatorTaskTag = request.InitiatorTaskTag,
            TargetTransferTag = 0xFFFFFFFF,
            StatSN = session.StatSN++,
            ExpCmdSN = session.ExpCmdSN,
            MaxCmdSN = session.ExpCmdSN + session.MaxCmdSN,
            DataSegment = data,
            DataSegmentLength = (uint)data.Length,
        };
    }

    /// <summary>
    /// Serialize this PDU to a byte array ready for transmission.
    /// The result is the 48-byte BHS + data segment (padded to 4-byte boundary).
    /// </summary>
    public byte[] ToBytes()
    {
        var header = new byte[IscsiConstants.HeaderSize];

        // Byte 0: Immediate flag + opcode
        header[0] = Opcode;
        if (Immediate)
            header[0] |= 0x40;

        // Byte 1: Final/Transit bit + opcode-specific flags
        if (Opcode == IscsiConstants.OpcodeLoginResponse)
        {
            if (Transit)
                header[1] |= 0x80;
            header[1] |= (byte)((CurrentStage & 0x03) << 2);
            header[1] |= (byte)(NextStage & 0x03);
        }
        else
        {
            if (Final)
                header[1] |= 0x80;
        }

        // Byte 3: Status — for SCSI Response always; for Data-In only when
        // the S-bit is set (meaning "this PDU carries the SCSI status, this
        // is the last PDU of the entire command"). For burst-final-but-not-
        // command-final Data-In, the caller passes ScsiStatus=0xFF as a
        // "no status" sentinel; byte 3 stays 0 (reserved) and the S-bit
        // is left clear.
        if (Opcode == IscsiConstants.OpcodeScsiResponse)
        {
            header[3] = ScsiStatus;
        }
        else if (Opcode == IscsiConstants.OpcodeScsiDataIn && Final && ScsiStatus != 0xFF)
        {
            header[1] |= 0x01; // S bit
            header[3] = ScsiStatus;
        }

        // Total AHS length: byte 4
        header[4] = TotalAhsLength;

        // Data segment length: bytes 5-7 (24-bit big-endian)
        uint dsLen = (uint)(DataSegment?.Length ?? 0);
        header[5] = (byte)((dsLen >> 16) & 0xFF);
        header[6] = (byte)((dsLen >> 8) & 0xFF);
        header[7] = (byte)(dsLen & 0xFF);

        // Login response: ISID + TSIH
        if (Opcode == IscsiConstants.OpcodeLoginResponse)
        {
            BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(8), ISID_A);
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(10), ISID_B);
            BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(14), TSIH);
        }

        // Initiator Task Tag: bytes 16-19
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(16), InitiatorTaskTag);

        // Target Transfer Tag: bytes 20-23
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(20), TargetTransferTag);

        // StatSN: bytes 24-27
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(24), StatSN);

        // ExpCmdSN: bytes 28-31
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(28), ExpCmdSN);

        // MaxCmdSN: bytes 32-35
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(32), MaxCmdSN);

        // Login response: StatusClass/StatusDetail at 36-37
        if (Opcode == IscsiConstants.OpcodeLoginResponse)
        {
            header[36] = StatusClass;
            header[37] = StatusDetail;
        }

        // R2T-specific fields per RFC 3720 §10.8: R2TSN at 36-39, BufferOffset
        // at 40-43, DesiredDataTransferLength at 44-47. Without these, the
        // initiator can't know which byte range it must send next, and the
        // write hangs.
        if (Opcode == IscsiConstants.OpcodeR2T)
        {
            // R2T MaxCmdSN already at 32-35 from the generic writer above —
            // overwrite bytes 36+ which the generic writer left for other uses.
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(36), R2TSN);
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(40), BufferOffset);
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(44), DesiredDataTransferLength);
        }

        // Data-In per RFC 3720 §10.7: DataSN at 36-39 (resets to 0 at the
        // start of each burst, increments per-PDU), BufferOffset at 40-43
        // (running offset in the SCSI read buffer). Without these, multi-PDU
        // reads where the initiator computes the buffer position from
        // BufferOffset would put the second-and-later PDUs at offset 0,
        // corrupting the reassembled buffer.
        if (Opcode == IscsiConstants.OpcodeScsiDataIn)
        {
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(36), DataSN);
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(40), BufferOffset);
        }

        // Build output: header + data segment padded to 4-byte boundary
        int paddedDataLen = PadTo4(dsLen);
        var result = new byte[IscsiConstants.HeaderSize + paddedDataLen];
        Array.Copy(header, 0, result, 0, IscsiConstants.HeaderSize);

        if (DataSegment != null && DataSegment.Length > 0)
        {
            Array.Copy(DataSegment, 0, result, IscsiConstants.HeaderSize, DataSegment.Length);
        }

        return result;
    }

    /// <summary>
    /// Parse iSCSI text key=value pairs from a data segment.
    /// Each pair is null-terminated.
    /// </summary>
    public static Dictionary<string, string> ParseTextData(byte[] data)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (data == null || data.Length == 0)
            return result;

        var text = Encoding.UTF8.GetString(data);
        var pairs = text.Split('\0', StringSplitOptions.RemoveEmptyEntries);

        foreach (var pair in pairs)
        {
            int eq = pair.IndexOf('=');
            if (eq > 0)
            {
                string key = pair[..eq];
                string value = pair[(eq + 1)..];
                result[key] = value;
            }
        }

        return result;
    }

    /// <summary>
    /// Build iSCSI text key=value data segment.
    /// </summary>
    public static string BuildTextData(Dictionary<string, string> parameters)
    {
        var sb = new StringBuilder();
        foreach (var kvp in parameters)
        {
            sb.Append(kvp.Key);
            sb.Append('=');
            sb.Append(kvp.Value);
            sb.Append('\0');
        }
        return sb.ToString();
    }

    private static int PadTo4(uint length)
    {
        return (int)((length + 3) & ~3u);
    }
}
