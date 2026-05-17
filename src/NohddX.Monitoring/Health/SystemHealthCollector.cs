using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NohddX.Monitoring.Metrics;

namespace NohddX.Monitoring.Health;

/// <summary>
/// Background service that periodically collects system-level metrics
/// (CPU, memory, etc.) and publishes them as Prometheus gauges.
/// </summary>
public class SystemHealthCollector : BackgroundService
{
    private readonly ILogger<SystemHealthCollector> _logger;
    private readonly TimeSpan _collectionInterval = TimeSpan.FromSeconds(5);

    private TimeSpan _previousCpuTime = TimeSpan.Zero;
    private DateTime _previousTimestamp = DateTime.UtcNow;

    public SystemHealthCollector(ILogger<SystemHealthCollector> logger)
    {
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("System health collector started (interval: {Interval}s)",
            _collectionInterval.TotalSeconds);

        // Initialize CPU tracking baseline
        var process = Process.GetCurrentProcess();
        _previousCpuTime = process.TotalProcessorTime;
        _previousTimestamp = DateTime.UtcNow;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                CollectMetrics();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error collecting system metrics");
            }

            await Task.Delay(_collectionInterval, stoppingToken);
        }

        _logger.LogInformation("System health collector stopped");
    }

    private void CollectMetrics()
    {
        var process = Process.GetCurrentProcess();

        // Memory: working set in MB
        var memoryMb = process.WorkingSet64 / (1024.0 * 1024.0);
        NohddxMetrics.MemoryUsage.Set(memoryMb);

        // CPU: estimate usage based on elapsed processor time since last sample
        var now = DateTime.UtcNow;
        var currentCpuTime = process.TotalProcessorTime;
        var elapsedCpu = currentCpuTime - _previousCpuTime;
        var elapsedWall = now - _previousTimestamp;

        if (elapsedWall.TotalMilliseconds > 0)
        {
            var cpuPercent = (elapsedCpu.TotalMilliseconds /
                (elapsedWall.TotalMilliseconds * Environment.ProcessorCount)) * 100.0;
            NohddxMetrics.CpuUsage.Set(Math.Round(cpuPercent, 2));
        }

        _previousCpuTime = currentCpuTime;
        _previousTimestamp = now;

        // Storage: report usage for all fixed drives
        try
        {
            long totalUsed = 0;
            long totalSize = 0;
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (drive.IsReady && drive.DriveType == DriveType.Fixed)
                {
                    totalUsed += drive.TotalSize - drive.AvailableFreeSpace;
                    totalSize += drive.TotalSize;
                }
            }

            NohddxMetrics.StorageUsedBytes.Set(totalUsed);
            NohddxMetrics.StorageTotalBytes.Set(totalSize);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read drive information");
        }
    }
}
