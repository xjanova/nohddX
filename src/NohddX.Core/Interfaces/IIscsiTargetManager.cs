namespace NohddX.Core.Interfaces;

public interface IIscsiTargetManager
{
    Task StartAsync(CancellationToken ct = default);
    Task StopAsync(CancellationToken ct = default);
    Task<string> RegisterTargetAsync(string clientId, string baseImagePath, CancellationToken ct = default);
    Task UnregisterTargetAsync(string clientId, CancellationToken ct = default);
    int GetActiveSessionCount();
}
