using System.Net;
using System.Text;
using System.Text.Json;
using NohddX.Agent.Hardware;

namespace NohddX.Agent.Communication;

/// <summary>
/// Lightweight HTTP server (HttpListener) the agent exposes so the
/// NoHddX server can poll its status, query hardware, push commands
/// and request a graceful shutdown.
/// </summary>
public class AgentHttpServer : IDisposable
{
    private readonly int _port;
    private readonly HardwareInfo? _hardware;
    private readonly string? _agentId;
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public event EventHandler<CommandReceivedEventArgs>? CommandReceived;

    public AgentHttpServer(int port, HardwareInfo? hardware, string? agentId)
    {
        _port = port;
        _hardware = hardware;
        _agentId = agentId;
    }

    public Task StartAsync(CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _listener = new HttpListener();
        // "+" is privileged on Windows; use localhost on dev for safety.
        var prefix = OperatingSystem.IsWindows()
            ? $"http://localhost:{_port}/"
            : $"http://+:{_port}/";
        _listener.Prefixes.Add(prefix);

        try
        {
            _listener.Start();
        }
        catch
        {
            return Task.CompletedTask;
        }

        return Task.Run(async () => await AcceptLoopAsync(_cts.Token));
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        if (_listener == null) return;

        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync();
            }
            catch
            {
                break;
            }

            _ = Task.Run(() => HandleRequestAsync(ctx, ct));
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        try
        {
            var req = ctx.Request;
            var path = (req.Url?.AbsolutePath ?? "/").TrimEnd('/').ToLowerInvariant();

            switch (path)
            {
                case "":
                case "/ping":
                    await WriteText(ctx, 200, "pong");
                    return;

                case "/info":
                {
                    var info = new
                    {
                        agentId = _agentId,
                        hostname = _hardware?.Hostname,
                        cpu = _hardware?.Cpu.Model,
                        memory = _hardware?.Memory.TotalBytes,
                        disks = _hardware?.Disks.Count ?? 0,
                        networks = _hardware?.Networks.Count ?? 0,
                        boot = _hardware?.Boot.Mode,
                        detectedAt = _hardware?.DetectedAt
                    };
                    await WriteJson(ctx, 200, info);
                    return;
                }

                case "/command":
                {
                    if (!string.Equals(req.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
                    {
                        await WriteText(ctx, 405, "Method Not Allowed");
                        return;
                    }
                    using var reader = new StreamReader(req.InputStream, req.ContentEncoding ?? Encoding.UTF8);
                    var body = await reader.ReadToEndAsync();
                    var cmd = ParseCommand(body);
                    if (cmd != null)
                    {
                        CommandReceived?.Invoke(this, cmd);
                    }
                    await WriteJson(ctx, 200, new { ok = true });
                    return;
                }

                case "/shutdown":
                {
                    if (!string.Equals(req.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
                    {
                        await WriteText(ctx, 405, "Method Not Allowed");
                        return;
                    }
                    await WriteJson(ctx, 200, new { ok = true });
                    _cts?.Cancel();
                    return;
                }

                default:
                    await WriteText(ctx, 404, "Not Found");
                    return;
            }
        }
        catch
        {
            try { ctx.Response.StatusCode = 500; ctx.Response.Close(); } catch { }
        }
    }

    private static CommandReceivedEventArgs? ParseCommand(string body)
    {
        try
        {
            var doc = JsonSerializer.Deserialize<CommandPayload>(body, JsonOpts);
            if (doc == null || string.IsNullOrEmpty(doc.Command)) return null;
            return new CommandReceivedEventArgs
            {
                Command = doc.Command,
                Payload = doc.Payload
            };
        }
        catch
        {
            return null;
        }
    }

    private static async Task WriteText(HttpListenerContext ctx, int status, string body)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "text/plain; charset=utf-8";
        var bytes = Encoding.UTF8.GetBytes(body);
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes);
        ctx.Response.Close();
    }

    private static async Task WriteJson(HttpListenerContext ctx, int status, object body)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(body, JsonOpts);
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes);
        ctx.Response.Close();
    }

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        try { _listener?.Close(); } catch { }
        _cts?.Dispose();
        GC.SuppressFinalize(this);
    }

    private class CommandPayload
    {
        public string Command { get; set; } = "";
        public string? Payload { get; set; }
    }
}

public class CommandReceivedEventArgs : EventArgs
{
    public string Command { get; set; } = "";
    public string? Payload { get; set; }
}
