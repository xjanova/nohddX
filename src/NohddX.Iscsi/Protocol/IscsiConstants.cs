namespace NohddX.Iscsi.Protocol;

/// <summary>
/// Constants for the iSCSI protocol: opcodes, SCSI commands, login stages, and status codes.
/// </summary>
public static class IscsiConstants
{
    public const int DefaultPort = 3260;
    public const int HeaderSize = 48;
    public const int SectorSize = 512;

    // Opcodes (initiator -> target)
    public const byte OpcodeLoginRequest = 0x03;
    public const byte OpcodeTextRequest = 0x04;
    public const byte OpcodeScsiCommand = 0x01;
    public const byte OpcodeScsiDataOut = 0x05;
    public const byte OpcodeLogoutRequest = 0x06;
    public const byte OpcodeNopOut = 0x00;

    // Opcodes (target -> initiator)
    public const byte OpcodeLoginResponse = 0x23;
    public const byte OpcodeTextResponse = 0x24;
    public const byte OpcodeScsiResponse = 0x21;
    public const byte OpcodeScsiDataIn = 0x25;
    public const byte OpcodeR2T = 0x31;
    public const byte OpcodeLogoutResponse = 0x26;
    public const byte OpcodeNopIn = 0x20;
    public const byte OpcodeReject = 0x3F;

    // SCSI Commands
    public const byte ScsiTestUnitReady = 0x00;
    public const byte ScsiInquiry = 0x12;
    public const byte ScsiReadCapacity10 = 0x25;
    public const byte ScsiRead10 = 0x28;
    public const byte ScsiWrite10 = 0x2A;
    public const byte ScsiRead16 = 0x88;
    public const byte ScsiWrite16 = 0x8A;
    public const byte ScsiReadCapacity16 = 0x9E;
    public const byte ScsiModeSense6 = 0x1A;
    public const byte ScsiReportLuns = 0xA0;

    // Login stages (encoded in CSG/NSG fields)
    public const byte LoginStageSecurityNegotiation = 0;
    public const byte LoginStageOperationalNegotiation = 1;
    public const byte LoginStageFullFeaturePhase = 3;

    // SCSI status codes
    public const byte StatusGood = 0x00;
    public const byte StatusCheckCondition = 0x02;

    // Maximum data segment we accept per PDU (256 KB)
    public const int MaxDataSegmentLength = 262144;
}
