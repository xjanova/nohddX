using System.Collections.Concurrent;

namespace NohddX.Iscsi;

/// <summary>
/// Maps IQNs to client IDs and base image paths.
/// Thread-safe registry of all iSCSI targets that clients can connect to.
/// </summary>
public class TargetRegistry
{
    private readonly ConcurrentDictionary<string, TargetInfo> _byClientId = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, TargetInfo> _byIqn = new(StringComparer.OrdinalIgnoreCase);

    public record TargetInfo(string ClientId, string BaseImagePath, string Iqn);

    /// <summary>
    /// Register a new target. Overwrites any existing registration for the same client ID.
    /// </summary>
    public void Register(string clientId, string baseImagePath, string iqn)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseImagePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(iqn);

        var info = new TargetInfo(clientId, baseImagePath, iqn);

        // Remove old IQN mapping if the client was previously registered with a different IQN
        if (_byClientId.TryGetValue(clientId, out var existing))
        {
            _byIqn.TryRemove(existing.Iqn, out _);
        }

        _byClientId[clientId] = info;
        _byIqn[iqn] = info;
    }

    /// <summary>
    /// Unregister a target by client ID.
    /// </summary>
    public bool Unregister(string clientId)
    {
        if (_byClientId.TryRemove(clientId, out var info))
        {
            _byIqn.TryRemove(info.Iqn, out _);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Look up a target by its IQN (used during login when the initiator specifies TargetName).
    /// </summary>
    public TargetInfo? FindByIqn(string iqn)
    {
        _byIqn.TryGetValue(iqn, out var info);
        return info;
    }

    /// <summary>
    /// Look up a target by client ID.
    /// </summary>
    public TargetInfo? FindByClientId(string clientId)
    {
        _byClientId.TryGetValue(clientId, out var info);
        return info;
    }

    /// <summary>
    /// Get all registered targets.
    /// </summary>
    public IReadOnlyList<TargetInfo> GetAll()
    {
        return _byClientId.Values.ToList().AsReadOnly();
    }

    /// <summary>
    /// Number of registered targets.
    /// </summary>
    public int Count => _byClientId.Count;
}
