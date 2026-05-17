using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NohddX.ClientMgmt.Services;
using NohddX.Core.Interfaces;
using NohddX.Core.Models;
using Xunit;

namespace NohddX.Tests.ClientMgmt;

public class ClientManagerTests
{
    [Theory]
    [InlineData("AA:BB:CC:DD:EE:FF", "AA-BB-CC-DD-EE-FF")]
    [InlineData("aa-bb-cc-dd-ee-ff", "AA-BB-CC-DD-EE-FF")]
    [InlineData("AABBCCDDEEFF",       "AA-BB-CC-DD-EE-FF")]
    [InlineData("aa.bb.cc.dd.ee.ff", "AA-BB-CC-DD-EE-FF")]
    public async Task RegisterClient_normalises_mac_to_hyphen_form(string input, string expected)
    {
        // Why hyphens: iPXE substitutes ${mac:hexhyp} (hyphens) when chainloading
        // /api/boot/{mac}.ipxe, and BootEndpointHandler.NormalizeMac returns the
        // same form. If ClientManager stored colons, every /api/boot lookup
        // would miss and clients would silently get the discovery script.
        var clientRepo = new Mock<IClientRepository>();
        clientRepo.Setup(r => r.GetByMacAddressAsync(It.IsAny<string>(), default))
            .ReturnsAsync((ClientMachine?)null);
        clientRepo.Setup(r => r.AddAsync(It.IsAny<ClientMachine>(), default))
            .ReturnsAsync((ClientMachine c, CancellationToken _) => c);

        var mgr = MakeManager(clientRepo);

        var created = await mgr.RegisterClientAsync(input);

        created.MacAddress.Should().Be(expected);
        clientRepo.Verify(r => r.GetByMacAddressAsync(expected, default), Times.Once,
            "the canonical MAC must also be used when probing the DB");
    }

    [Fact]
    public async Task AssignImage_registers_iscsi_target_with_image_path()
    {
        // The audit's #1 blocker: without this hook, every iSCSI login fails
        // "target not found" because TargetRegistry stays empty.
        var clientId = Guid.NewGuid();
        var imageId = Guid.NewGuid();
        var imagePath = @"C:\NohddX\bases\ubuntu-22.04.vhd";

        var clientRepo = new Mock<IClientRepository>();
        clientRepo.Setup(r => r.GetByIdAsync(clientId, default))
            .ReturnsAsync(new ClientMachine { Id = clientId, MacAddress = "AA-BB-CC-DD-EE-FF" });

        var asnRepo = new Mock<IBootAssignmentRepository>();
        asnRepo.Setup(r => r.GetByClientIdAsync(clientId, default)).ReturnsAsync((BootAssignment?)null);
        asnRepo.Setup(r => r.AddAsync(It.IsAny<BootAssignment>(), default))
            .ReturnsAsync((BootAssignment a, CancellationToken _) => a);

        var imageRepo = new Mock<IImageRepository>();
        imageRepo.Setup(r => r.GetByIdAsync(imageId, default))
            .ReturnsAsync(new BootImage { Id = imageId, FilePath = imagePath });

        var iscsi = new Mock<IIscsiTargetManager>();

        var mgr = MakeManager(clientRepo, asnRepo, imageRepo, iscsi);

        await mgr.AssignImageAsync(clientId, imageId);

        iscsi.Verify(
            m => m.RegisterTargetAsync(clientId.ToString(), imagePath, default),
            Times.Once,
            "after a new assignment the iSCSI registry must know which VHD backs this client");
    }

    [Fact]
    public async Task AssignImage_reassign_path_also_registers_iscsi_target()
    {
        // The update branch (existing != null) was easy to forget; verify it too.
        var clientId = Guid.NewGuid();
        var oldImageId = Guid.NewGuid();
        var newImageId = Guid.NewGuid();

        var clientRepo = new Mock<IClientRepository>();
        clientRepo.Setup(r => r.GetByIdAsync(clientId, default))
            .ReturnsAsync(new ClientMachine { Id = clientId, MacAddress = "AA-BB-CC-DD-EE-FF" });

        var existing = new BootAssignment
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            ImageId = oldImageId,
            IsActive = true,
        };
        var asnRepo = new Mock<IBootAssignmentRepository>();
        asnRepo.Setup(r => r.GetByClientIdAsync(clientId, default)).ReturnsAsync(existing);

        var imageRepo = new Mock<IImageRepository>();
        imageRepo.Setup(r => r.GetByIdAsync(newImageId, default))
            .ReturnsAsync(new BootImage { Id = newImageId, FilePath = "/img/new.vhd" });

        var iscsi = new Mock<IIscsiTargetManager>();
        var mgr = MakeManager(clientRepo, asnRepo, imageRepo, iscsi);

        await mgr.AssignImageAsync(clientId, newImageId);

        iscsi.Verify(m => m.RegisterTargetAsync(clientId.ToString(), "/img/new.vhd", default), Times.Once);
    }

    [Fact]
    public async Task UnregisterClient_drops_iscsi_target()
    {
        var clientId = Guid.NewGuid();
        var clientRepo = new Mock<IClientRepository>();
        clientRepo.Setup(r => r.GetByIdAsync(clientId, default))
            .ReturnsAsync(new ClientMachine { Id = clientId, MacAddress = "AA-BB-CC-DD-EE-FF" });

        var asnRepo = new Mock<IBootAssignmentRepository>();
        asnRepo.Setup(r => r.GetByClientIdAsync(clientId, default)).ReturnsAsync((BootAssignment?)null);

        var iscsi = new Mock<IIscsiTargetManager>();
        var mgr = MakeManager(clientRepo, asnRepo, iscsi: iscsi);

        await mgr.UnregisterClientAsync(clientId);

        iscsi.Verify(m => m.UnregisterTargetAsync(clientId.ToString(), default), Times.Once,
            "deleting a client must close its iSCSI target so future logins are rejected");
    }

    // ── Helper ─────────────────────────────────────────────────────────

    private static ClientManager MakeManager(
        Mock<IClientRepository>? clientRepo = null,
        Mock<IBootAssignmentRepository>? asnRepo = null,
        Mock<IImageRepository>? imageRepo = null,
        Mock<IIscsiTargetManager>? iscsi = null)
    {
        return new ClientManager(
            clientRepo?.Object ?? new Mock<IClientRepository>().Object,
            asnRepo?.Object ?? new Mock<IBootAssignmentRepository>().Object,
            new Mock<IClientGroupRepository>().Object,
            imageRepo?.Object ?? new Mock<IImageRepository>().Object,
            new Mock<ICowStorageEngine>().Object,
            iscsi?.Object ?? new Mock<IIscsiTargetManager>().Object,
            NullLogger<ClientManager>.Instance);
    }
}
