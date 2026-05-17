using System.Net.Sockets;

namespace NohddX.Iscsi.Session;

/// <summary>
/// Tracks the state of a single iSCSI connection/session from an initiator.
/// </summary>
public class IscsiSession
{
    public string SessionId { get; }
    public string InitiatorName { get; set; } = "";
    public string TargetName { get; set; } = "";
    public string ClientId { get; set; } = "";
    public Stream? DiskStream { get; set; }

    /// <summary>Status sequence number (target -> initiator).</summary>
    public uint StatSN { get; set; } = 1;

    /// <summary>Expected command sequence number from initiator.</summary>
    public uint ExpCmdSN { get; set; } = 1;

    /// <summary>Command window size.</summary>
    public uint MaxCmdSN { get; set; } = 32;

    /// <summary>
    /// Per-RFC 3720, the largest Data-In data segment we may send to the
    /// initiator in one PDU. Negotiated during login; iPXE typically offers
    /// 8 KB. Defaults to 8 KB so we are safe before negotiation completes.
    /// </summary>
    public int MaxRecvDataSegmentLength { get; set; } = 8192;

    /// <summary>Whether the session has completed login and entered full feature phase.</summary>
    public bool IsFullFeaturePhase { get; set; }

    /// <summary>
    /// Per-direction digest negotiation results from login (RFC 3720 §12.1).
    /// Only consulted for non-login PDUs; login PDUs themselves are never
    /// digested. Both default to false ("None") so a session that hasn't
    /// negotiated yet behaves like an old client.
    /// </summary>
    public bool HeaderDigestEnabled { get; set; }
    public bool DataDigestEnabled { get; set; }

    // ── CHAP state (only used when NohddX:Iscsi:ChapEnabled is true) ─────
    // The CHAP handshake spans multiple Login PDUs so we have to remember
    // the challenge we issued in step 2 when verifying the response in step 3.

    /// <summary>Set true once CHAP authentication has succeeded (or when CHAP is disabled).</summary>
    public bool AuthCompleted { get; set; }

    /// <summary>The 1-byte CHAP_I (identifier) we sent in our challenge.</summary>
    public byte ChapChallengeId { get; set; }

    /// <summary>The CHAP_C (challenge bytes) we sent. Null until we issue it.</summary>
    public byte[]? ChapChallenge { get; set; }

    /// <summary>
    /// In-flight SCSI writes that we are collecting Data-Out PDUs for, keyed
    /// by the initiator's task tag. A write enters this dict when its inline
    /// data is shorter than ExpectedDataTransferLength and we issue an R2T;
    /// it leaves the dict when the last Data-Out arrives and we send SCSI
    /// Response.
    /// </summary>
    public Dictionary<uint, PendingWrite> PendingWrites { get; } = new();

    /// <summary>Counter for assigning unique TargetTransferTag values to R2T PDUs.</summary>
    public uint NextR2tTag { get; set; } = 1;

    public DateTime ConnectedAt { get; }
    public DateTime LastActivity { get; set; }

    public TcpClient? TcpClient { get; set; }

    /// <summary>
    /// Lock used to serialize writes to the network stream for this session.
    /// </summary>
    public SemaphoreSlim WriteLock { get; } = new(1, 1);

    public IscsiSession(string sessionId)
    {
        SessionId = sessionId ?? throw new ArgumentNullException(nameof(sessionId));
        ConnectedAt = DateTime.UtcNow;
        LastActivity = DateTime.UtcNow;
    }

    /// <summary>
    /// Clean up resources held by this session.
    /// </summary>
    public async Task CloseAsync()
    {
        if (DiskStream != null)
        {
            await DiskStream.DisposeAsync();
            DiskStream = null;
        }

        try
        {
            TcpClient?.Close();
        }
        catch
        {
            // Best-effort close
        }

        TcpClient = null;
        WriteLock.Dispose();
    }
}
