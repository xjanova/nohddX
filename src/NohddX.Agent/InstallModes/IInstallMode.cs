using NohddX.Agent.Communication;
using NohddX.Agent.Hardware;

namespace NohddX.Agent.InstallModes;

/// <summary>
/// Strategy interface implemented by each supported boot/install mode.
/// </summary>
public interface IInstallMode
{
    string Name { get; }

    Task ExecuteAsync(
        HardwareInfo hardware,
        AgentApiClient client,
        string agentId,
        CancellationToken ct);
}
