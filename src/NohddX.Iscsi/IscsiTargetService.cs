using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NohddX.Core.Configuration;
using NohddX.Core.Interfaces;
using NohddX.Iscsi.Handlers;
using NohddX.Iscsi.Protocol;
using NohddX.Iscsi.Session;

namespace NohddX.Iscsi;

/// <summary>
/// Background service that runs a lightweight iSCSI target server.
/// Implements <see cref="IIscsiTargetManager"/> for programmatic target registration/unregistration.
/// </summary>
public class IscsiTargetService : BackgroundService, IIscsiTargetManager
{
    private readonly ICowStorageEngine _cowEngine;
    private readonly IscsiSessionManager _sessionManager;
    private readonly ScsiCommandHandler _scsiHandler;
    private readonly TargetRegistry _targetRegistry;
    private readonly NohddxOptions _options;
    private readonly ILogger<IscsiTargetService> _logger;

    private TcpListener? _listener;
    private readonly SemaphoreSlim _connectionThrottle;

    public IscsiTargetService(
        ICowStorageEngine cowEngine,
        IscsiSessionManager sessionManager,
        ScsiCommandHandler scsiHandler,
        TargetRegistry targetRegistry,
        IOptions<NohddxOptions> options,
        ILogger<IscsiTargetService> logger)
    {
        _cowEngine = cowEngine ?? throw new ArgumentNullException(nameof(cowEngine));
        _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
        _scsiHandler = scsiHandler ?? throw new ArgumentNullException(nameof(scsiHandler));
        _targetRegistry = targetRegistry ?? throw new ArgumentNullException(nameof(targetRegistry));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _connectionThrottle = new SemaphoreSlim(_options.Iscsi.MaxConnections, _options.Iscsi.MaxConnections);
    }

    // ------------------------------------------------------------------
    // IIscsiTargetManager implementation (explicit to avoid hiding BackgroundService members)
    // ------------------------------------------------------------------

    Task IIscsiTargetManager.StartAsync(CancellationToken ct)
    {
        _logger.LogInformation("iSCSI Target Manager starting.");
        // Delegates to BackgroundService.StartAsync which triggers ExecuteAsync.
        return base.StartAsync(ct);
    }

    Task IIscsiTargetManager.StopAsync(CancellationToken ct)
    {
        _logger.LogInformation("iSCSI Target Manager stopping.");
        _listener?.Stop();
        return base.StopAsync(ct);
    }

    public Task<string> RegisterTargetAsync(string clientId, string baseImagePath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseImagePath);

        var iqn = $"{_options.Iscsi.IqnPrefix}:{clientId}";
        _targetRegistry.Register(clientId, baseImagePath, iqn);

        _logger.LogInformation("Registered iSCSI target {Iqn} for client {ClientId}, base image: {BaseImage}",
            iqn, clientId, baseImagePath);

        return Task.FromResult(iqn);
    }

    public Task UnregisterTargetAsync(string clientId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);

        if (_targetRegistry.Unregister(clientId))
        {
            _logger.LogInformation("Unregistered iSCSI target for client {ClientId}.", clientId);
        }
        else
        {
            _logger.LogWarning("Attempted to unregister unknown client {ClientId}.", clientId);
        }

        return Task.CompletedTask;
    }

    public int GetActiveSessionCount() => _sessionManager.GetSessionCount();

    // ------------------------------------------------------------------
    // BackgroundService
    // ------------------------------------------------------------------

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _listener = new TcpListener(IPAddress.Any, _options.Iscsi.Port);
        _listener.Start();

        _logger.LogInformation("iSCSI Target listening on port {Port} (max connections: {MaxConn}).",
            _options.Iscsi.Port, _options.Iscsi.MaxConnections);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }

                // Throttle connections
                if (!_connectionThrottle.Wait(0))
                {
                    _logger.LogWarning("Max connections ({Max}) reached. Rejecting connection from {Remote}.",
                        _options.Iscsi.MaxConnections, client.Client.RemoteEndPoint);
                    client.Close();
                    continue;
                }

                _ = HandleClientAsync(client, stoppingToken);
            }
        }
        finally
        {
            _listener.Stop();
            await _sessionManager.RemoveAllAsync();
            _logger.LogInformation("iSCSI Target stopped.");
        }
    }

    // ------------------------------------------------------------------
    // Per-client connection handler
    // ------------------------------------------------------------------

    private async Task HandleClientAsync(TcpClient tcpClient, CancellationToken ct)
    {
        var remoteEp = tcpClient.Client.RemoteEndPoint?.ToString() ?? "unknown";
        var session = _sessionManager.CreateSession();
        session.TcpClient = tcpClient;

        _logger.LogDebug("New iSCSI connection from {Remote}, session {SessionId}.", remoteEp, session.SessionId);

        try
        {
            tcpClient.NoDelay = true;
            tcpClient.ReceiveTimeout = 30_000;
            tcpClient.SendTimeout = 30_000;

            var stream = tcpClient.GetStream();

            while (tcpClient.Connected && !ct.IsCancellationRequested)
            {
                // 1. Read 48-byte BHS
                var header = new byte[IscsiConstants.HeaderSize];
                if (!await ReadExactAsync(stream, header, ct))
                    break;

                // 2. Parse data segment length from header bytes 5-7
                uint dataLen = (uint)((header[5] << 16) | (header[6] << 8) | header[7]);
                if (dataLen > IscsiConstants.MaxDataSegmentLength)
                {
                    _logger.LogWarning("Data segment too large ({Len} bytes) from {Remote}. Disconnecting.",
                        dataLen, remoteEp);
                    break;
                }

                // 3. Read data segment (padded to 4-byte boundary)
                int paddedLen = (int)((dataLen + 3) & ~3u);
                var dataSegment = Array.Empty<byte>();
                if (paddedLen > 0)
                {
                    var paddedData = new byte[paddedLen];
                    if (!await ReadExactAsync(stream, paddedData, ct))
                        break;

                    // Trim to actual length (remove padding)
                    dataSegment = new byte[dataLen];
                    Array.Copy(paddedData, dataSegment, dataLen);
                }

                // 4. Parse PDU
                var pdu = IscsiPdu.Parse(header, dataSegment);
                session.LastActivity = DateTime.UtcNow;

                // Update expected CmdSN tracking
                if (!pdu.Immediate)
                {
                    session.ExpCmdSN = pdu.CmdSN + 1;
                }

                // 5. Dispatch by opcode
                List<IscsiPdu> responses;
                try
                {
                    responses = pdu.Opcode switch
                    {
                        IscsiConstants.OpcodeLoginRequest => await HandleLoginAsync(pdu, session, ct),
                        IscsiConstants.OpcodeScsiCommand => await _scsiHandler.HandleCommandAsync(pdu, session),
                        IscsiConstants.OpcodeScsiDataOut => await _scsiHandler.HandleDataOutAsync(pdu, session),
                        IscsiConstants.OpcodeNopOut => HandleNopOut(pdu, session),
                        IscsiConstants.OpcodeLogoutRequest => HandleLogout(pdu, session),
                        IscsiConstants.OpcodeTextRequest => HandleTextRequest(pdu, session),
                        _ => HandleUnknownOpcode(pdu, session)
                    };
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error handling opcode 0x{Opcode:X2} from session {SessionId}.",
                        pdu.Opcode, session.SessionId);
                    break;
                }

                // 6. Send responses
                foreach (var response in responses)
                {
                    var responseBytes = response.ToBytes();
                    await session.WriteLock.WaitAsync(ct);
                    try
                    {
                        await stream.WriteAsync(responseBytes, ct);
                        await stream.FlushAsync(ct);
                    }
                    finally
                    {
                        session.WriteLock.Release();
                    }
                }

                // If we sent a logout response, close the connection
                if (pdu.Opcode == IscsiConstants.OpcodeLogoutRequest)
                {
                    _logger.LogDebug("Logout completed for session {SessionId}.", session.SessionId);
                    break;
                }
            }
        }
        catch (IOException ex) when (ex.InnerException is SocketException)
        {
            _logger.LogDebug("Connection reset by {Remote} (session {SessionId}).", remoteEp, session.SessionId);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Connection cancelled for session {SessionId}.", session.SessionId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled error in session {SessionId} from {Remote}.",
                session.SessionId, remoteEp);
        }
        finally
        {
            await _sessionManager.RemoveSessionAsync(session.SessionId);
            _connectionThrottle.Release();
            _logger.LogDebug("Session {SessionId} from {Remote} closed.", session.SessionId, remoteEp);
        }
    }

    // ------------------------------------------------------------------
    // Login handler
    // ------------------------------------------------------------------

    private async Task<List<IscsiPdu>> HandleLoginAsync(IscsiPdu request, IscsiSession session, CancellationToken ct)
    {
        var textParams = IscsiPdu.ParseTextData(request.DataSegment);

        // Extract initiator name and target name
        if (textParams.TryGetValue("InitiatorName", out var initiatorName))
        {
            session.InitiatorName = initiatorName;
        }

        if (textParams.TryGetValue("TargetName", out var targetName))
        {
            session.TargetName = targetName;
        }

        _logger.LogDebug("Login request from initiator '{Initiator}' for target '{Target}' (session {SessionId}).",
            session.InitiatorName, session.TargetName, session.SessionId);

        // Determine login stage
        byte csg = request.CurrentStage;
        byte nsg = request.NextStage;
        bool transit = request.Transit;

        // ── CHAP enforcement ───────────────────────────────────────────
        // When the operator turns ChapEnabled on, every Login that touches
        // the security stage (CSG=0) must complete the CHAP exchange before
        // we let the session transit to Operational, and any attempt to
        // bypass it (AuthMethod=None, or jumping straight to CSG=1) gets
        // rejected. Without this the config flag was a no-op (audit I1).
        if (_options.Iscsi.ChapEnabled && !session.AuthCompleted)
        {
            var chapResult = HandleChapAsync(request, session, textParams, csg);
            if (chapResult is not null)
                return chapResult;
            // chapResult == null means CHAP just completed; fall through to
            // operational negotiation in the same response.
        }

        // RFC 3720 §12.12: MaxRecvDataSegmentLength is declarative per
        // direction. The value we store on the session is what the *initiator*
        // can receive, so our Data-In chunker must cap at it. If the
        // initiator omits the key, RFC default is 8192 — NOT our 256 KB max.
        // Falling back to 256 KB would make us blast oversized PDUs at iPXE
        // and have it drop the connection.
        const int RfcDefaultMaxRecv = 8192;
        int negotiatedMaxRecv = RfcDefaultMaxRecv;
        if (textParams.TryGetValue("MaxRecvDataSegmentLength", out var maxRecvStr) &&
            int.TryParse(maxRecvStr, out var offered) && offered > 0)
        {
            negotiatedMaxRecv = Math.Min(offered, IscsiConstants.MaxDataSegmentLength);
        }
        session.MaxRecvDataSegmentLength = negotiatedMaxRecv;

        // Build response text parameters
        var responseParams = new Dictionary<string, string>
        {
            ["TargetPortalGroupTag"] = "1",
            ["HeaderDigest"] = "None",
            ["DataDigest"] = "None",
            ["DefaultTime2Wait"] = "0",
            ["DefaultTime2Retain"] = "0",
            ["MaxRecvDataSegmentLength"] = negotiatedMaxRecv.ToString(),
            ["MaxBurstLength"] = IscsiConstants.MaxDataSegmentLength.ToString(),
            ["FirstBurstLength"] = IscsiConstants.MaxDataSegmentLength.ToString(),
            ["MaxConnections"] = "1",
            ["InitialR2T"] = "Yes",
            ["ImmediateData"] = "Yes",
            ["MaxOutstandingR2T"] = "1",
            ["ErrorRecoveryLevel"] = "0",
        };

        // Echo back any other simple parameters the initiator offered.
        foreach (var kvp in textParams)
        {
            if (kvp.Key == "SessionType")
                responseParams["SessionType"] = kvp.Value;
        }

        // If transitioning to full feature phase, open the disk
        if (transit && nsg == IscsiConstants.LoginStageFullFeaturePhase)
        {
            var targetInfo = _targetRegistry.FindByIqn(session.TargetName);
            if (targetInfo == null)
            {
                _logger.LogWarning("Target '{Target}' not found in registry.", session.TargetName);

                var rejectResponse = IscsiPdu.BuildLoginResponse(request, session,
                    statusClass: 0x02, statusDetail: 0x01, // Target not found
                    currentStage: csg, nextStage: nsg, transit: false);
                return new List<IscsiPdu> { rejectResponse };
            }

            session.ClientId = targetInfo.ClientId;

            try
            {
                session.DiskStream = await _cowEngine.OpenDiskAsync(targetInfo.BaseImagePath, targetInfo.ClientId, ct);
                session.IsFullFeaturePhase = true;

                _logger.LogInformation(
                    "Session {SessionId}: Opened CoW disk for client {ClientId}, disk size: {Size} bytes.",
                    session.SessionId, targetInfo.ClientId, session.DiskStream.Length);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to open disk for client {ClientId}.", targetInfo.ClientId);

                var errorResponse = IscsiPdu.BuildLoginResponse(request, session,
                    statusClass: 0x02, statusDetail: 0x00, // Target error
                    currentStage: csg, nextStage: nsg, transit: false);
                return new List<IscsiPdu> { errorResponse };
            }
        }

        var textData = IscsiPdu.BuildTextData(responseParams);
        var loginResponse = IscsiPdu.BuildLoginResponse(request, session,
            statusClass: 0x00, statusDetail: 0x00, // Success
            currentStage: csg, nextStage: transit ? nsg : csg,
            transit: transit, textData: textData);

        return new List<IscsiPdu> { loginResponse };
    }

    // ------------------------------------------------------------------
    // CHAP MD5 handshake (RFC 3720 §11.1.4)
    // ------------------------------------------------------------------
    //
    // The exchange takes three Login PDUs:
    //   1. Initiator: AuthMethod=CHAP,None        ->  Target: AuthMethod=CHAP
    //   2. Initiator: CHAP_A=5                    ->  Target: CHAP_A=5 CHAP_I=<id> CHAP_C=0x<rand>
    //   3. Initiator: CHAP_N=<user> CHAP_R=0x<md5> ->  Target: success + transit
    //
    // Returns: a list of PDUs to send back IMMEDIATELY (and stop further
    // processing for this PDU), OR null when CHAP just succeeded so the
    // caller can continue into operational negotiation.

    private List<IscsiPdu>? HandleChapAsync(
        IscsiPdu request,
        IscsiSession session,
        Dictionary<string, string> textParams,
        byte csg)
    {
        // Server must have a username/password to verify against — otherwise
        // every login fails closed. Logging this loudly so the operator
        // doesn't mistake silent reject for a network issue.
        if (string.IsNullOrEmpty(_options.Iscsi.ChapUsername) ||
            string.IsNullOrEmpty(_options.Iscsi.ChapPassword))
        {
            _logger.LogError(
                "CHAP enabled but ChapUsername/ChapPassword not configured; rejecting login {SessionId}",
                session.SessionId);
            return new List<IscsiPdu> { BuildAuthFailure(request, session) };
        }

        // The initiator must be in security stage (CSG=0) for CHAP. If they
        // jumped straight to operational, refuse — that's a bypass attempt.
        if (csg != IscsiConstants.LoginStageSecurityNegotiation)
        {
            _logger.LogWarning(
                "CHAP enabled; rejecting login {SessionId} that skipped security stage",
                session.SessionId);
            return new List<IscsiPdu> { BuildAuthFailure(request, session) };
        }

        // Step 3: initiator returned CHAP_N + CHAP_R. Verify and transition.
        if (textParams.TryGetValue("CHAP_R", out var chapR) &&
            textParams.TryGetValue("CHAP_N", out var chapN))
        {
            if (session.ChapChallenge is null)
            {
                _logger.LogWarning("CHAP response received without prior challenge for session {SessionId}", session.SessionId);
                return new List<IscsiPdu> { BuildAuthFailure(request, session) };
            }

            if (!string.Equals(chapN, _options.Iscsi.ChapUsername, StringComparison.Ordinal))
            {
                _logger.LogWarning("CHAP username mismatch for session {SessionId}", session.SessionId);
                return new List<IscsiPdu> { BuildAuthFailure(request, session) };
            }

            byte[] expectedHash = ComputeChapResponse(
                session.ChapChallengeId,
                _options.Iscsi.ChapPassword!,
                session.ChapChallenge);

            byte[] receivedHash;
            try
            {
                var hex = chapR.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? chapR[2..] : chapR;
                receivedHash = Convert.FromHexString(hex);
            }
            catch (FormatException)
            {
                _logger.LogWarning("Malformed CHAP_R for session {SessionId}", session.SessionId);
                return new List<IscsiPdu> { BuildAuthFailure(request, session) };
            }

            // Constant-time compare so we don't leak how many bytes matched.
            if (!CryptographicOperations.FixedTimeEquals(expectedHash, receivedHash))
            {
                _logger.LogWarning("CHAP response did not match for session {SessionId}", session.SessionId);
                return new List<IscsiPdu> { BuildAuthFailure(request, session) };
            }

            session.AuthCompleted = true;
            // Wipe the secret-adjacent state so it isn't sitting around.
            session.ChapChallenge = null;

            _logger.LogInformation("CHAP authenticated session {SessionId} as '{User}'",
                session.SessionId, chapN);

            // Tell caller to fall through into operational negotiation
            // (which will pack the rest of the response).
            return null;
        }

        // Step 2: initiator sent CHAP_A; we issue the challenge.
        if (textParams.TryGetValue("CHAP_A", out var chapA))
        {
            // We only support MD5 (algorithm 5). Anything else: reject.
            if (!chapA.Split(',').Any(a => a.Trim() == "5"))
            {
                _logger.LogWarning("Initiator did not offer CHAP_A=5 (MD5) for session {SessionId}", session.SessionId);
                return new List<IscsiPdu> { BuildAuthFailure(request, session) };
            }

            // Generate a fresh challenge and remember it on the session for step 3.
            session.ChapChallenge = RandomNumberGenerator.GetBytes(16);
            session.ChapChallengeId = (byte)RandomNumberGenerator.GetInt32(0, 256);

            var chapParams = new Dictionary<string, string>
            {
                ["CHAP_A"] = "5",
                ["CHAP_I"] = session.ChapChallengeId.ToString(),
                ["CHAP_C"] = "0x" + Convert.ToHexString(session.ChapChallenge).ToLowerInvariant(),
            };

            return new List<IscsiPdu>
            {
                IscsiPdu.BuildLoginResponse(request, session,
                    statusClass: 0x00, statusDetail: 0x00,
                    currentStage: IscsiConstants.LoginStageSecurityNegotiation,
                    nextStage: IscsiConstants.LoginStageSecurityNegotiation,
                    transit: false,
                    textData: IscsiPdu.BuildTextData(chapParams)),
            };
        }

        // Step 1: initiator offered AuthMethod. Must include CHAP.
        if (textParams.TryGetValue("AuthMethod", out var authMethod))
        {
            var offered = authMethod.Split(',').Select(a => a.Trim()).ToArray();
            if (!offered.Contains("CHAP", StringComparer.OrdinalIgnoreCase))
            {
                _logger.LogWarning(
                    "Initiator on session {SessionId} did not offer CHAP (offered={Offered}); refusing anonymous login",
                    session.SessionId, authMethod);
                return new List<IscsiPdu> { BuildAuthFailure(request, session) };
            }

            var ackParams = new Dictionary<string, string>
            {
                ["AuthMethod"] = "CHAP",
            };

            return new List<IscsiPdu>
            {
                IscsiPdu.BuildLoginResponse(request, session,
                    statusClass: 0x00, statusDetail: 0x00,
                    currentStage: IscsiConstants.LoginStageSecurityNegotiation,
                    nextStage: IscsiConstants.LoginStageSecurityNegotiation,
                    transit: false,
                    textData: IscsiPdu.BuildTextData(ackParams)),
            };
        }

        // The initiator is in CSG=0 but offered no AuthMethod and no CHAP
        // exchange — that's the silent-bypass path. Refuse.
        _logger.LogWarning(
            "CHAP required but initiator sent no AuthMethod/CHAP keys on session {SessionId}",
            session.SessionId);
        return new List<IscsiPdu> { BuildAuthFailure(request, session) };
    }

    /// <summary>
    /// Compute the CHAP-MD5 response per RFC 1994: MD5(id || password || challenge).
    /// </summary>
    private static byte[] ComputeChapResponse(byte id, string password, byte[] challenge)
    {
        var pwBytes = Encoding.UTF8.GetBytes(password);
        var input = new byte[1 + pwBytes.Length + challenge.Length];
        input[0] = id;
        Array.Copy(pwBytes, 0, input, 1, pwBytes.Length);
        Array.Copy(challenge, 0, input, 1 + pwBytes.Length, challenge.Length);
        return MD5.HashData(input);
    }

    private static IscsiPdu BuildAuthFailure(IscsiPdu request, IscsiSession session)
    {
        // Status class 0x02 = Initiator Error, detail 0x01 = Authentication Failure.
        return IscsiPdu.BuildLoginResponse(request, session,
            statusClass: 0x02, statusDetail: 0x01,
            currentStage: IscsiConstants.LoginStageSecurityNegotiation,
            nextStage: IscsiConstants.LoginStageSecurityNegotiation,
            transit: false);
    }

    // ------------------------------------------------------------------
    // NOP-Out handler
    // ------------------------------------------------------------------

    private List<IscsiPdu> HandleNopOut(IscsiPdu request, IscsiSession session)
    {
        // If ITT is 0xFFFFFFFF this is an unsolicited NOP; respond only if ITT is valid
        if (request.InitiatorTaskTag == 0xFFFFFFFF)
            return new List<IscsiPdu>();

        return new List<IscsiPdu> { IscsiPdu.BuildNopIn(request, session) };
    }

    // ------------------------------------------------------------------
    // Text request handler (used for target discovery)
    // ------------------------------------------------------------------

    private List<IscsiPdu> HandleTextRequest(IscsiPdu request, IscsiSession session)
    {
        var textParams = IscsiPdu.ParseTextData(request.DataSegment);

        if (textParams.TryGetValue("SendTargets", out var sendTargets) &&
            string.Equals(sendTargets, "All", StringComparison.OrdinalIgnoreCase))
        {
            // Return list of all registered targets
            var sb = new StringBuilder();
            foreach (var target in _targetRegistry.GetAll())
            {
                sb.Append("TargetName=");
                sb.Append(target.Iqn);
                sb.Append('\0');
            }

            var responseText = sb.ToString();
            return new List<IscsiPdu> { IscsiPdu.BuildTextResponse(request, session, responseText) };
        }

        // Echo back empty response for unrecognized text requests
        return new List<IscsiPdu> { IscsiPdu.BuildTextResponse(request, session, "") };
    }

    // ------------------------------------------------------------------
    // Logout handler
    // ------------------------------------------------------------------

    private List<IscsiPdu> HandleLogout(IscsiPdu request, IscsiSession session)
    {
        _logger.LogInformation("Logout request from session {SessionId} (client {ClientId}).",
            session.SessionId, session.ClientId);

        return new List<IscsiPdu> { IscsiPdu.BuildLogoutResponse(request, session) };
    }

    // ------------------------------------------------------------------
    // Unknown opcode handler
    // ------------------------------------------------------------------

    private List<IscsiPdu> HandleUnknownOpcode(IscsiPdu request, IscsiSession session)
    {
        _logger.LogWarning("Unknown opcode 0x{Opcode:X2} from session {SessionId}.",
            request.Opcode, session.SessionId);

        // Build a Reject PDU
        var reject = new IscsiPdu
        {
            Opcode = IscsiConstants.OpcodeReject,
            Final = true,
            InitiatorTaskTag = request.InitiatorTaskTag,
            StatSN = session.StatSN++,
            ExpCmdSN = session.ExpCmdSN,
            MaxCmdSN = session.ExpCmdSN + session.MaxCmdSN,
            DataSegment = request.HeaderBytes, // Include the rejected BHS
            DataSegmentLength = (uint)request.HeaderBytes.Length,
        };

        return new List<IscsiPdu> { reject };
    }

    // ------------------------------------------------------------------
    // Network I/O helpers
    // ------------------------------------------------------------------

    /// <summary>
    /// Read exactly <paramref name="buffer"/>.Length bytes from the stream.
    /// Returns false if the connection closed before all bytes arrived.
    /// </summary>
    private static async Task<bool> ReadExactAsync(NetworkStream stream, byte[] buffer, CancellationToken ct)
    {
        int offset = 0;
        int remaining = buffer.Length;

        while (remaining > 0)
        {
            int read;
            try
            {
                read = await stream.ReadAsync(buffer.AsMemory(offset, remaining), ct);
            }
            catch (IOException)
            {
                return false;
            }

            if (read == 0)
                return false; // Connection closed

            offset += read;
            remaining -= read;
        }

        return true;
    }
}
