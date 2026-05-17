using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NohddX.Database;

namespace NohddX.Server;

/// <summary>
/// Patches in any tables/indexes the EF model expects but the existing
/// SQLite file lacks. <see cref="DatabaseFacade.EnsureCreatedAsync"/> only
/// initialises an empty DB — when we add a new entity (e.g. AuditLog) to
/// <see cref="NohddxDbContext"/>, existing dev DBs would be missing it
/// forever, and any controller hitting it would 500 with "no such table".
/// Real production deployments should use migrations; this is the dev /
/// upgrade-in-place shim.
/// </summary>
internal static class DbSync
{
    public static async Task SyncMissingTablesAsync(NohddxDbContext db, ILogger logger)
    {
        if (!db.Database.IsSqlite())
            return; // Postgres deployments go through `dotnet ef` migrations.

        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type IN ('table','index')";
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                existing.Add(reader.GetString(0));
        }
        finally
        {
            await conn.CloseAsync();
        }

        // EF's CreateScript is the source of truth — it reflects the current
        // model exactly. We run only the statements whose object is missing.
        var script = db.Database.GenerateCreateScript();
        var statements = script.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        int created = 0;
        foreach (var stmt in statements)
        {
            var name = ExtractObjectName(stmt);
            if (name is null || existing.Contains(name)) continue;

            try
            {
                await db.Database.ExecuteSqlRawAsync(stmt);
                existing.Add(name);
                created++;
                logger.LogInformation("Created missing DB object: {Name}", name);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to create missing DB object {Name}; continuing", name);
            }
        }

        if (created > 0)
            logger.LogInformation("Schema sync added {Count} missing objects to nohddx.db", created);
    }

    /// <summary>
    /// Pull the table-or-index name out of a CREATE TABLE / CREATE INDEX
    /// statement. Returns null for anything we don't care about (PRAGMA, etc).
    /// </summary>
    private static string? ExtractObjectName(string statement)
    {
        var trimmed = statement.TrimStart();
        string[] prefixes =
        {
            "CREATE TABLE IF NOT EXISTS ",
            "CREATE TABLE ",
            "CREATE UNIQUE INDEX IF NOT EXISTS ",
            "CREATE UNIQUE INDEX ",
            "CREATE INDEX IF NOT EXISTS ",
            "CREATE INDEX ",
        };
        foreach (var p in prefixes)
        {
            if (!trimmed.StartsWith(p, StringComparison.OrdinalIgnoreCase)) continue;
            var rest = trimmed[p.Length..].TrimStart();
            if (rest.Length == 0) return null;

            if (rest[0] == '"')
            {
                int end = rest.IndexOf('"', 1);
                return end > 1 ? rest.Substring(1, end - 1) : null;
            }

            int sp = rest.IndexOfAny(new[] { ' ', '(', '\t', '\r', '\n' });
            return sp > 0 ? rest.Substring(0, sp) : rest;
        }
        return null;
    }
}
