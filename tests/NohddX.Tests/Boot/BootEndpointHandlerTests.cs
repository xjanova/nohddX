using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NohddX.Boot.Http;
using NohddX.Core.Configuration;
using NohddX.Core.Interfaces;
using NohddX.Core.Models;
using Xunit;

namespace NohddX.Tests.Boot;

public class BootEndpointHandlerTests
{
    [Theory]
    [InlineData("AA:BB:CC:DD:EE:FF", "AA-BB-CC-DD-EE-FF")]
    [InlineData("aa:bb:cc:dd:ee:ff", "AA-BB-CC-DD-EE-FF")]
    [InlineData("aa-bb-cc-dd-ee-ff", "AA-BB-CC-DD-EE-FF")]
    [InlineData("AABBCCDDEEFF", "AA-BB-CC-DD-EE-FF")]
    [InlineData("aa.bb.cc.dd.ee.ff", "AA-BB-CC-DD-EE-FF")]
    public void NormalizeMac_handles_common_formats(string input, string expected)
    {
        BootEndpointHandler.NormalizeMac(input).Should().Be(expected);
    }

    [Fact]
    public async Task Unknown_client_returns_discovery_script()
    {
        var (handler, _, _, _) = MakeHandler();

        var (script, ct) = await handler.HandleBootRequestAsync("11:22:33:44:55:66");

        script.Should().Contain("not registered", because: "discovery script must inform admin");
        ct.Should().Be("text/plain");
    }

    [Fact]
    public async Task Empty_mac_returns_discovery_script_not_throw()
    {
        var (handler, _, _, _) = MakeHandler();
        var (script, _) = await handler.HandleBootRequestAsync("");
        script.Should().Contain("not registered");
    }

    [Fact]
    public async Task Known_client_with_assignment_emits_iscsi_script()
    {
        var (handler, clientRepo, asnRepo, imgRepo) = MakeHandler();

        var clientId = Guid.NewGuid();
        var imageId = Guid.NewGuid();
        var client = new ClientMachine
        {
            Id = clientId,
            MacAddress = "AA-BB-CC-DD-EE-FF",
            Hostname = "lab-01",
            AssignedNodeId = null
        };
        var assignment = new BootAssignment
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            ImageId = imageId,
            IsActive = true
        };
        var image = new BootImage
        {
            Id = imageId,
            Name = "ubuntu-22.04",
            OsType = OsType.Linux
        };

        clientRepo.Setup(r => r.GetByMacAddressAsync("AA-BB-CC-DD-EE-FF", It.IsAny<CancellationToken>()))
            .ReturnsAsync(client);
        asnRepo.Setup(r => r.GetByClientIdAsync(clientId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(assignment);
        imgRepo.Setup(r => r.GetByIdAsync(imageId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(image);

        var (script, _) = await handler.HandleBootRequestAsync("aa:bb:cc:dd:ee:ff");

        script.Should().StartWith("#!ipxe");
        script.Should().Contain("sanboot");
        script.Should().Contain("iscsi:");
        script.Should().Contain($"iqn.2024.com.nohddx:{clientId}");
    }

    private static (
        BootEndpointHandler handler,
        Mock<IClientRepository> clientRepo,
        Mock<IBootAssignmentRepository> asnRepo,
        Mock<IImageRepository> imgRepo) MakeHandler()
    {
        var options = Options.Create(new NohddxOptions());

        var clientRepo = new Mock<IClientRepository>();
        var asnRepo = new Mock<IBootAssignmentRepository>();
        var imgRepo = new Mock<IImageRepository>();
        var nodeRepo = new Mock<IClusterNodeRepository>();

        var generator = new IpxeScriptGenerator(options, NullLogger<IpxeScriptGenerator>.Instance);

        var handler = new BootEndpointHandler(
            generator,
            clientRepo.Object,
            asnRepo.Object,
            imgRepo.Object,
            nodeRepo.Object,
            options,
            NullLogger<BootEndpointHandler>.Instance);

        return (handler, clientRepo, asnRepo, imgRepo);
    }
}
