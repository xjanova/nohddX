using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NohddX.ClientMgmt.Services;
using NohddX.Core.Interfaces;
using NohddX.Core.Models;
using Xunit;

namespace NohddX.Tests.ClientMgmt;

public class IscsiTargetBootstrapTests
{
    [Fact]
    public async Task Bootstrap_registers_iscsi_target_per_active_assignment()
    {
        // The whole point: when the server restarts, the in-memory iSCSI
        // registry is empty, so previously-assigned clients can't boot until
        // an operator reassigns. Bootstrap rehydrates from the DB.
        var clientA = Guid.NewGuid();
        var clientB = Guid.NewGuid();
        var imageA = Guid.NewGuid();
        var imageB = Guid.NewGuid();

        var iscsi = new Mock<IIscsiTargetManager>();

        var (provider, _) = BuildProvider(
            assignments: new[]
            {
                new BootAssignment { Id = Guid.NewGuid(), ClientId = clientA, ImageId = imageA, IsActive = true  },
                new BootAssignment { Id = Guid.NewGuid(), ClientId = clientB, ImageId = imageB, IsActive = false }, // inactive, skip
            },
            images: new[]
            {
                new BootImage { Id = imageA, FilePath = "/img/a.vhd" },
                new BootImage { Id = imageB, FilePath = "/img/b.vhd" },
            },
            iscsi: iscsi);

        var bootstrap = new IscsiTargetBootstrap(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<IscsiTargetBootstrap>.Instance);

        await bootstrap.StartAsync(default);

        iscsi.Verify(m => m.RegisterTargetAsync(clientA.ToString(), "/img/a.vhd", It.IsAny<CancellationToken>()), Times.Once);
        iscsi.Verify(m => m.RegisterTargetAsync(clientB.ToString(), "/img/b.vhd", It.IsAny<CancellationToken>()), Times.Never,
            "inactive assignments must not register iSCSI targets");
    }

    [Fact]
    public async Task Bootstrap_skips_assignments_with_missing_image()
    {
        // Defensive path: a corrupted DB shouldn't crash startup. We log
        // and skip — the rest of the fleet should still come up.
        var clientId = Guid.NewGuid();
        var imageId = Guid.NewGuid();

        var iscsi = new Mock<IIscsiTargetManager>();

        var (provider, _) = BuildProvider(
            assignments: new[]
            {
                new BootAssignment { Id = Guid.NewGuid(), ClientId = clientId, ImageId = imageId, IsActive = true }
            },
            images: Array.Empty<BootImage>(),
            iscsi: iscsi);

        var bootstrap = new IscsiTargetBootstrap(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<IscsiTargetBootstrap>.Instance);

        Func<Task> act = async () => await bootstrap.StartAsync(default);
        await act.Should().NotThrowAsync();

        iscsi.Verify(m => m.RegisterTargetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Bootstrap_swallows_exceptions_so_server_can_still_start()
    {
        // If the DB itself blows up we still don't want StartAsync to throw —
        // that would tank the WebApplication. Just log and move on.
        var asnRepo = new Mock<IBootAssignmentRepository>();
        asnRepo.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("db unavailable"));

        var services = new ServiceCollection();
        services.AddSingleton(asnRepo.Object);
        services.AddSingleton(new Mock<IImageRepository>().Object);
        services.AddSingleton(new Mock<IIscsiTargetManager>().Object);
        var provider = services.BuildServiceProvider();

        var bootstrap = new IscsiTargetBootstrap(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<IscsiTargetBootstrap>.Instance);

        Func<Task> act = async () => await bootstrap.StartAsync(default);
        await act.Should().NotThrowAsync();
    }

    // ── Helpers ────────────────────────────────────────────────────────

    private static (ServiceProvider provider, Mock<IBootAssignmentRepository> asnRepo)
        BuildProvider(
            IEnumerable<BootAssignment> assignments,
            IEnumerable<BootImage> images,
            Mock<IIscsiTargetManager> iscsi)
    {
        var asnRepo = new Mock<IBootAssignmentRepository>();
        asnRepo.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(assignments.ToList());

        var imageRepo = new Mock<IImageRepository>();
        var byId = images.ToDictionary(i => i.Id);
        imageRepo.Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, CancellationToken _) => byId.TryGetValue(id, out var img) ? img : null);

        var services = new ServiceCollection();
        services.AddSingleton(asnRepo.Object);
        services.AddSingleton(imageRepo.Object);
        services.AddSingleton(iscsi.Object);

        return (services.BuildServiceProvider(), asnRepo);
    }
}
