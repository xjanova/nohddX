namespace NohddX.Core.Interfaces;

public interface ICowStorageEngine
{
    Task<Stream> OpenDiskAsync(string baseImagePath, string clientId, CancellationToken ct = default);
    Task ResetOverlayAsync(string clientId, CancellationToken ct = default);
    Task<long> GetOverlaySizeAsync(string clientId, CancellationToken ct = default);
    Task CreateSnapshotAsync(string clientId, string snapshotName, CancellationToken ct = default);
}
