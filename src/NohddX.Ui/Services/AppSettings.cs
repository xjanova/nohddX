using System.IO;
using System.Text.Json;

namespace NohddX.Ui.Services;

/// <summary>
/// User-facing settings persisted to <c>%APPDATA%\NohddX\settings.json</c>.
/// The operator console talks to a NohddX server over HTTP + SignalR; this
/// holds the base URL and the admin API key (when auth is enabled on the
/// server).
/// </summary>
public sealed class AppSettings
{
    private static readonly string ConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "NohddX",
        "settings.json");

    public string ServerUrl { get; set; } = "http://localhost:8080";

    /// <summary>
    /// Optional X-Admin-Api-Key. Leave blank in dev where the server is
    /// configured with AllowAnonymousAdminInDev.
    /// </summary>
    public string AdminApiKey { get; set; } = "";

    /// <summary>Fired whenever Save() persists new values, so live components can re-wire.</summary>
    public event EventHandler? Changed;

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(ConfigPath))
                return new AppSettings();

            var json = File.ReadAllText(ConfigPath);
            return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(ConfigPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions
            {
                WriteIndented = true
            });
            File.WriteAllText(ConfigPath, json);
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            // Best-effort; nothing actionable for the operator on a settings write fail
        }
    }
}
