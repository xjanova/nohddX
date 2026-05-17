using NohddX.Core.Models;

namespace NohddX.Core.Interfaces;

public interface IIpxeScriptGenerator
{
    string GenerateBootScript(ClientMachine client, BootImage image, ClusterNode node);
    string GenerateDiscoveryScript(string serverIp);
}
