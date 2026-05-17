namespace NohddX.Iscsi.Session;

/// <summary>
/// A SCSI Write currently waiting for more Data-Out PDUs to arrive. Created
/// when a Write-10/Write-16 arrives without all of its data inline, and the
/// target has issued an R2T asking the initiator to send the rest.
/// </summary>
public sealed class PendingWrite
{
    /// <summary>InitiatorTaskTag from the original Write command — used to dispatch incoming Data-Out PDUs.</summary>
    public uint InitiatorTaskTag { get; init; }

    /// <summary>LUN from the original Write command (echoed back in the eventual SCSI Response).</summary>
    public ulong Lun { get; init; }

    /// <summary>TargetTransferTag we put on the R2T; Data-Out PDUs from the initiator must carry this back.</summary>
    public uint TargetTransferTag { get; init; }

    /// <summary>Disk byte offset where the next chunk should land. Advances as Data-Out PDUs are written.</summary>
    public long DiskOffsetNext { get; set; }

    /// <summary>Total bytes the initiator promised to send (sectorCount * 512).</summary>
    public int TotalLength { get; init; }

    /// <summary>How many bytes we've actually written to disk so far.</summary>
    public int BytesReceived { get; set; }

    /// <summary>R2TSN counter — incremented if we ever issue multiple R2Ts for the same write.</summary>
    public uint R2TSN { get; set; }
}
