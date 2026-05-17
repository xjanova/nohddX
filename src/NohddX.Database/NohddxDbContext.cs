using Microsoft.EntityFrameworkCore;
using NohddX.Core.Models;

namespace NohddX.Database;

public class NohddxDbContext : DbContext
{
    public NohddxDbContext(DbContextOptions<NohddxDbContext> options) : base(options) { }

    public DbSet<ClientMachine> Clients => Set<ClientMachine>();
    public DbSet<ClientGroup> Groups => Set<ClientGroup>();
    public DbSet<BootImage> Images => Set<BootImage>();
    public DbSet<BootAssignment> Assignments => Set<BootAssignment>();
    public DbSet<HardwareProfile> HardwareProfiles => Set<HardwareProfile>();
    public DbSet<ClusterNode> ClusterNodes => Set<ClusterNode>();
    public DbSet<ImageSnapshot> Snapshots => Set<ImageSnapshot>();
    public DbSet<BootEvent> BootEvents => Set<BootEvent>();
    public DbSet<StoragePool> StoragePools => Set<StoragePool>();
    public DbSet<AuditLogEntry> AuditLog => Set<AuditLogEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // ClientMachine
        modelBuilder.Entity<ClientMachine>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.MacAddress).IsUnique();
            e.HasOne(x => x.Group).WithMany(g => g.Clients).HasForeignKey(x => x.GroupId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne(x => x.HardwareProfile).WithMany(h => h.Clients).HasForeignKey(x => x.HardwareProfileId).OnDelete(DeleteBehavior.SetNull);
            e.Property(x => x.MacAddress).HasMaxLength(20).IsRequired();
            e.Property(x => x.Hostname).HasMaxLength(255);
        });

        // BootImage
        modelBuilder.Entity<BootImage>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(255).IsRequired();
            e.Property(x => x.FilePath).HasMaxLength(500).IsRequired();
        });

        // BootAssignment
        modelBuilder.Entity<BootAssignment>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasOne(x => x.Client).WithOne(c => c.BootAssignment).HasForeignKey<BootAssignment>(x => x.ClientId);
            e.HasOne(x => x.Image).WithMany(i => i.Assignments).HasForeignKey(x => x.ImageId);
        });

        // ClientGroup
        modelBuilder.Entity<ClientGroup>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(255).IsRequired();
        });

        // HardwareProfile
        modelBuilder.Entity<HardwareProfile>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(255).IsRequired();
        });

        // ClusterNode
        modelBuilder.Entity<ClusterNode>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Hostname).HasMaxLength(255).IsRequired();
            e.Property(x => x.IpAddress).HasMaxLength(50).IsRequired();
        });

        // ImageSnapshot
        modelBuilder.Entity<ImageSnapshot>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasOne(x => x.Image).WithMany(i => i.Snapshots).HasForeignKey(x => x.ImageId);
        });

        // BootEvent
        modelBuilder.Entity<BootEvent>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.ClientId);
            e.HasIndex(x => x.StartedAt);
        });

        // StoragePool
        modelBuilder.Entity<StoragePool>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(255).IsRequired();
        });

        // AuditLogEntry
        modelBuilder.Entity<AuditLogEntry>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Timestamp);
            e.HasIndex(x => new { x.Actor, x.Action });
            e.Property(x => x.Actor).HasMaxLength(32).IsRequired();
            e.Property(x => x.ActorId).HasMaxLength(64);
            e.Property(x => x.RemoteIp).HasMaxLength(64);
            e.Property(x => x.Action).HasMaxLength(128).IsRequired();
            e.Property(x => x.TargetType).HasMaxLength(64);
            e.Property(x => x.TargetId).HasMaxLength(64);
            e.Property(x => x.Detail).HasMaxLength(2048);
        });
    }
}
