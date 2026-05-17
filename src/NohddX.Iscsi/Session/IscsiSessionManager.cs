using System.Collections.Concurrent;

namespace NohddX.Iscsi.Session;

/// <summary>
/// Thread-safe manager for all active iSCSI sessions.
/// </summary>
public class IscsiSessionManager
{
    private readonly ConcurrentDictionary<string, IscsiSession> _sessions = new();

    /// <summary>
    /// Create and register a new session with a generated ID.
    /// </summary>
    public IscsiSession CreateSession()
    {
        var sessionId = Guid.NewGuid().ToString("N");
        var session = new IscsiSession(sessionId);
        _sessions[sessionId] = session;
        return session;
    }

    /// <summary>
    /// Add an existing session.
    /// </summary>
    public void AddSession(IscsiSession session)
    {
        _sessions[session.SessionId] = session;
    }

    /// <summary>
    /// Remove a session by ID, closing its resources.
    /// </summary>
    public async Task RemoveSessionAsync(string sessionId)
    {
        if (_sessions.TryRemove(sessionId, out var session))
        {
            await session.CloseAsync();
        }
    }

    /// <summary>
    /// Get a session by its ID.
    /// </summary>
    public IscsiSession? GetSession(string sessionId)
    {
        _sessions.TryGetValue(sessionId, out var session);
        return session;
    }

    /// <summary>
    /// Get all active sessions (snapshot).
    /// </summary>
    public IReadOnlyList<IscsiSession> GetAllSessions()
    {
        return _sessions.Values.ToList().AsReadOnly();
    }

    /// <summary>
    /// Current number of active sessions.
    /// </summary>
    public int GetSessionCount() => _sessions.Count;

    /// <summary>
    /// Find a session by its client ID.
    /// </summary>
    public IscsiSession? FindByClientId(string clientId)
    {
        foreach (var kvp in _sessions)
        {
            if (string.Equals(kvp.Value.ClientId, clientId, StringComparison.OrdinalIgnoreCase))
                return kvp.Value;
        }
        return null;
    }

    /// <summary>
    /// Close and remove all sessions.
    /// </summary>
    public async Task RemoveAllAsync()
    {
        var sessions = _sessions.Values.ToList();
        _sessions.Clear();
        foreach (var session in sessions)
        {
            await session.CloseAsync();
        }
    }
}
