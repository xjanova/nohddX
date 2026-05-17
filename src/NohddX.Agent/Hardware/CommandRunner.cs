using System.Diagnostics;

namespace NohddX.Agent.Hardware;

/// <summary>
/// Tiny helper for running external commands and reading files
/// safely. Failures are swallowed and surfaced as null so callers
/// can fall through to defaults; this is a critical boot path that
/// must never crash on a missing tool or unreadable sysfs node.
/// </summary>
public static class CommandRunner
{
    public static async Task<string?> RunAsync(string command, string args, int timeoutMs = 5000)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = command,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null) return null;

            var outputTask = process.StandardOutput.ReadToEndAsync();

            var completed = await Task.Run(() => process.WaitForExit(timeoutMs));
            if (!completed)
            {
                try { process.Kill(true); } catch { }
                return null;
            }

            return await outputTask;
        }
        catch
        {
            return null;
        }
    }

    public static async Task<string?> ReadFileAsync(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return await File.ReadAllTextAsync(path);
        }
        catch
        {
            return null;
        }
    }

    public static string? ReadFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return File.ReadAllText(path);
        }
        catch
        {
            return null;
        }
    }

    public static bool DirectoryExists(string path)
    {
        try { return Directory.Exists(path); }
        catch { return false; }
    }
}
