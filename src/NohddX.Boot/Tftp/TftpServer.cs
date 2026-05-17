using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NohddX.Core.Configuration;
using NohddX.Core.Interfaces;

namespace NohddX.Boot.Tftp;

/// <summary>
/// Simple TFTP server for serving iPXE binaries to PXE-booting clients.
/// Implements RFC 1350 (TFTP Protocol) with enough functionality for PXE boot.
/// Runs as a <see cref="BackgroundService"/> and implements <see cref="ITftpService"/>.
/// </summary>
public class TftpServer : BackgroundService, ITftpService
{
    private UdpClient? _listener;
    private readonly NohddxOptions _options;
    private readonly ILogger<TftpServer> _logger;

    // TFTP opcodes (RFC 1350)
    private const ushort OpcodeRrq = 1;   // Read Request
    private const ushort OpcodeData = 3;  // Data
    private const ushort OpcodeAck = 4;   // Acknowledgment
    private const ushort OpcodeError = 5; // Error

    // TFTP error codes
    private const ushort ErrorFileNotFound = 1;
    private const ushort ErrorAccessViolation = 2;

    // TFTP standard block size
    private const int BlockSize = 512;

    // Timeout for ACK responses
    private const int AckTimeoutMs = 5000;
    private const int MaxRetries = 3;

    public bool IsRunning { get; private set; }

    public TftpServer(
        IOptions<NohddxOptions> options,
        ILogger<TftpServer> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Tftp.Enabled)
        {
            _logger.LogInformation("TFTP server is disabled in configuration");
            return;
        }

        var basePath = _options.Tftp.IpxeBinaryPath;
        if (!Directory.Exists(basePath))
        {
            _logger.LogWarning("TFTP binary path does not exist: {Path}. Creating directory.", basePath);
            Directory.CreateDirectory(basePath);
        }

        try
        {
            _listener = new UdpClient();
            _listener.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _listener.Client.Bind(new IPEndPoint(IPAddress.Any, _options.Tftp.Port));
            IsRunning = true;

            _logger.LogInformation(
                "TFTP server started on port {Port}, serving from {Path}",
                _options.Tftp.Port, basePath);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var result = await _listener.ReceiveAsync(stoppingToken);

                    // Verify minimum packet size and RRQ opcode
                    if (result.Buffer.Length >= 4 &&
                        result.Buffer[0] == 0 && result.Buffer[1] == OpcodeRrq)
                    {
                        // Handle read request on a separate task (TFTP uses a new port per transfer)
                        _ = Task.Run(
                            () => HandleReadRequestAsync(result.Buffer, result.RemoteEndPoint, stoppingToken),
                            CancellationToken.None);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.OperationAborted)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "TFTP error receiving packet");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Failed to start TFTP server on port {Port}", _options.Tftp.Port);
            throw;
        }
        finally
        {
            IsRunning = false;
            _listener?.Dispose();
            _listener = null;
            _logger.LogInformation("TFTP server stopped");
        }
    }

    private async Task HandleReadRequestAsync(byte[] rrq, IPEndPoint remoteEp, CancellationToken ct)
    {
        // Parse filename from RRQ: [opcode(2)] [filename(null-terminated)] [mode(null-terminated)]
        var filename = ParseFilename(rrq);
        if (string.IsNullOrEmpty(filename))
        {
            _logger.LogWarning("Empty filename in TFTP RRQ from {Remote}", remoteEp);
            return;
        }

        _logger.LogInformation("TFTP read request for '{Filename}' from {Remote}", filename, remoteEp);

        // Use a new UdpClient on a random port for the transfer (TFTP standard)
        using var transferClient = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
        transferClient.Client.ReceiveTimeout = AckTimeoutMs;

        // Resolve and validate file path (prevent directory traversal)
        var filePath = ResolveFilePath(filename);
        if (filePath == null)
        {
            _logger.LogWarning("Directory traversal attempt or invalid path: '{Filename}' from {Remote}", filename, remoteEp);
            await SendErrorAsync(transferClient, remoteEp, ErrorAccessViolation, "Access denied");
            return;
        }

        if (!File.Exists(filePath))
        {
            _logger.LogWarning("File not found: '{FilePath}' requested by {Remote}", filePath, remoteEp);
            await SendErrorAsync(transferClient, remoteEp, ErrorFileNotFound, "File not found");
            return;
        }

        try
        {
            await SendFileAsync(transferClient, remoteEp, filePath, ct);
            _logger.LogInformation("TFTP transfer complete: '{Filename}' to {Remote}", filename, remoteEp);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("TFTP transfer cancelled: '{Filename}' to {Remote}", filename, remoteEp);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TFTP transfer failed: '{Filename}' to {Remote}", filename, remoteEp);
        }
    }

    private async Task SendFileAsync(UdpClient client, IPEndPoint remoteEp, string filePath, CancellationToken ct)
    {
        var fileData = await File.ReadAllBytesAsync(filePath, ct);
        ushort blockNumber = 1;
        int offset = 0;

        while (offset < fileData.Length || blockNumber == 1)
        {
            ct.ThrowIfCancellationRequested();

            int remaining = fileData.Length - offset;
            int chunkSize = Math.Min(remaining, BlockSize);

            var dataPacket = BuildDataPacket(blockNumber, fileData, offset, chunkSize);

            bool acked = false;
            for (int retry = 0; retry < MaxRetries && !acked; retry++)
            {
                await client.SendAsync(dataPacket, dataPacket.Length, remoteEp);

                try
                {
                    var ackResult = await ReceiveWithTimeoutAsync(client, AckTimeoutMs, ct);

                    if (ackResult.HasValue &&
                        ackResult.Value.Buffer.Length >= 4 &&
                        ackResult.Value.Buffer[0] == 0 && ackResult.Value.Buffer[1] == OpcodeAck)
                    {
                        ushort ackBlock = (ushort)((ackResult.Value.Buffer[2] << 8) | ackResult.Value.Buffer[3]);
                        if (ackBlock == blockNumber)
                        {
                            acked = true;
                            // Update remote endpoint in case NAT changes the port
                            remoteEp = ackResult.Value.RemoteEndPoint;
                        }
                    }
                }
                catch (SocketException)
                {
                    _logger.LogDebug("TFTP ACK timeout for block {Block}, retry {Retry}", blockNumber, retry + 1);
                }
            }

            if (!acked)
            {
                _logger.LogWarning("TFTP transfer aborted: no ACK for block {Block} from {Remote}", blockNumber, remoteEp);
                return;
            }

            offset += chunkSize;
            blockNumber++;

            // If this was the last block (less than 512 bytes), we're done
            if (chunkSize < BlockSize)
                break;
        }
    }

    private static byte[] BuildDataPacket(ushort blockNumber, byte[] fileData, int offset, int length)
    {
        var packet = new byte[4 + length];
        packet[0] = 0;
        packet[1] = (byte)OpcodeData;
        packet[2] = (byte)(blockNumber >> 8);
        packet[3] = (byte)(blockNumber & 0xFF);
        if (length > 0)
        {
            Array.Copy(fileData, offset, packet, 4, length);
        }
        return packet;
    }

    private static async Task<UdpReceiveResult?> ReceiveWithTimeoutAsync(
        UdpClient client, int timeoutMs, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);
        try
        {
            return await client.ReceiveAsync(cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null; // Timeout only, not external cancellation
        }
    }

    private static async Task SendErrorAsync(UdpClient client, IPEndPoint remoteEp, ushort errorCode, string message)
    {
        var msgBytes = System.Text.Encoding.ASCII.GetBytes(message);
        var packet = new byte[4 + msgBytes.Length + 1];
        packet[0] = 0;
        packet[1] = (byte)OpcodeError;
        packet[2] = (byte)(errorCode >> 8);
        packet[3] = (byte)(errorCode & 0xFF);
        Array.Copy(msgBytes, 0, packet, 4, msgBytes.Length);
        // packet[4 + msgBytes.Length] = 0; // Already zero from array initialization

        await client.SendAsync(packet, packet.Length, remoteEp);
    }

    private static string? ParseFilename(byte[] rrq)
    {
        // RRQ format: [opcode(2)] [filename\0] [mode\0]
        int start = 2;
        int end = start;
        while (end < rrq.Length && rrq[end] != 0) end++;

        if (end <= start)
            return null;

        return System.Text.Encoding.ASCII.GetString(rrq, start, end - start);
    }

    /// <summary>
    /// Resolves the requested filename to an absolute path within the TFTP root directory.
    /// Returns null if the resolved path is outside the root (directory traversal prevention).
    /// </summary>
    private string? ResolveFilePath(string filename)
    {
        // Normalize path separators
        var normalized = filename.Replace('/', Path.DirectorySeparatorChar);

        var basePath = Path.GetFullPath(_options.Tftp.IpxeBinaryPath);
        var fullPath = Path.GetFullPath(Path.Combine(basePath, normalized));

        // Security: ensure resolved path is within the base directory
        if (!fullPath.StartsWith(basePath, StringComparison.OrdinalIgnoreCase))
            return null;

        return fullPath;
    }

    // Explicit interface implementation
    Task ITftpService.StartAsync(CancellationToken ct) => StartAsync(ct);
    Task ITftpService.StopAsync(CancellationToken ct) => StopAsync(ct);
}
