using Prometheus;

namespace NohddX.Monitoring.Metrics;

/// <summary>
/// Central registry of all Prometheus metrics for the NohddX system.
/// </summary>
public static class NohddxMetrics
{
    public static readonly Gauge ActiveClients = Prometheus.Metrics.CreateGauge(
        "nohddx_active_clients",
        "Number of currently connected clients");

    public static readonly Gauge IscsiSessions = Prometheus.Metrics.CreateGauge(
        "nohddx_iscsi_sessions",
        "Active iSCSI sessions");

    public static readonly Counter BootAttempts = Prometheus.Metrics.CreateCounter(
        "nohddx_boot_attempts_total",
        "Total boot attempts",
        new CounterConfiguration { LabelNames = new[] { "status" } });

    public static readonly Counter BytesRead = Prometheus.Metrics.CreateCounter(
        "nohddx_bytes_read_total",
        "Total bytes read from storage");

    public static readonly Counter BytesWritten = Prometheus.Metrics.CreateCounter(
        "nohddx_bytes_written_total",
        "Total bytes written to storage");

    public static readonly Gauge CpuUsage = Prometheus.Metrics.CreateGauge(
        "nohddx_cpu_usage_percent",
        "Server CPU usage");

    public static readonly Gauge MemoryUsage = Prometheus.Metrics.CreateGauge(
        "nohddx_memory_usage_percent",
        "Server memory usage");

    public static readonly Gauge DiskIops = Prometheus.Metrics.CreateGauge(
        "nohddx_disk_iops",
        "Disk IOPS");

    public static readonly Gauge NetworkBandwidth = Prometheus.Metrics.CreateGauge(
        "nohddx_network_bandwidth_mbps",
        "Network bandwidth in Mbps");

    public static readonly Gauge ClusterNodes = Prometheus.Metrics.CreateGauge(
        "nohddx_cluster_nodes",
        "Number of cluster nodes",
        new GaugeConfiguration { LabelNames = new[] { "status" } });

    public static readonly Gauge StorageUsedBytes = Prometheus.Metrics.CreateGauge(
        "nohddx_storage_used_bytes",
        "Storage used in bytes");

    public static readonly Gauge StorageTotalBytes = Prometheus.Metrics.CreateGauge(
        "nohddx_storage_total_bytes",
        "Total storage in bytes");

    public static readonly Histogram BootDuration = Prometheus.Metrics.CreateHistogram(
        "nohddx_boot_duration_seconds",
        "Boot duration in seconds",
        new HistogramConfiguration
        {
            Buckets = new[] { 5.0, 10.0, 20.0, 30.0, 60.0, 120.0, 300.0 }
        });
}
