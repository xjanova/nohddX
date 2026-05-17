namespace NohddX.Core.Interfaces;

public interface IDhcpProxyService
{
    Task StartAsync(CancellationToken ct = default);
    Task StopAsync(CancellationToken ct = default);
    bool IsRunning { get; }
}
