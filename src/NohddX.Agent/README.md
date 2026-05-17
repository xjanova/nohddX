# NohddX.Agent

Linux mini-OS bootstrap agent for the NoHddX diskless boot system.

This is the small TUI application that runs on a client computer when it
boots from the NoHddX USB ISO. It detects the local hardware, discovers a
NoHddX server on the LAN, registers itself, and lets the operator pick an
install mode (persistent install, diskless USB+network, or full network
boot).

## Architecture

```
Program.cs              -> Spectre.Console banner + AgentApp.RunAsync
AgentApp.cs             -> Orchestrator (detect -> discover -> register -> menu loop)
Hardware/                -> /proc, /sys and dmidecode/lspci/smartctl probes
Discovery/               -> JSON config + UDP broadcast server discovery
Communication/           -> HttpClient API client + HttpListener server (port 7000)
Tui/                     -> Spectre.Console menu and hardware table
InstallModes/            -> Persistent / Diskless USB / Network boot strategies
```

The whole binary is self-contained, single-file, and trimmed off
for size. It deliberately avoids ASP.NET Core, EF Core, DI containers
and any other heavyweight dependency to keep the on-disk footprint
small enough to embed in a minimal Linux ISO.

## Building

The project targets `net8.0` (set in the repo-level
`Directory.Build.props`). All package versions come from
`Directory.Packages.props`.

```bash
# From the repo root:
dotnet build src/NohddX.Agent/NohddX.Agent.csproj -c Release
```

## Publishing for Linux

```bash
dotnet publish src/NohddX.Agent/NohddX.Agent.csproj \
    -c Release \
    -r linux-x64 \
    --self-contained \
    -p:PublishSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    -o publish/agent-linux-x64
```

The output binary is published as `publish/agent-linux-x64/nohddx-agent`
(see `<AssemblyName>` in the csproj). Drop it into the ISO at e.g.
`/usr/local/bin/nohddx-agent` and call it from the autologin shell or
a systemd service.

For ARM:

```bash
dotnet publish src/NohddX.Agent/NohddX.Agent.csproj \
    -c Release -r linux-arm64 --self-contained -p:PublishSingleFile=true
```

## Configuration

The agent looks for a JSON config at, in order:

1. `/boot/nohddx-agent.json`  (preferred for USB customisation)
2. `/etc/nohddx-agent.json`
3. `./nohddx-agent.json`

Example:

```json
{
  "ServerUrl": "http://10.10.0.1:8080",
  "UseMdnsDiscovery": true,
  "PreferredMode": "Persistent",
  "AgentPort": 7000,
  "DiscoveryPort": 4012
}
```

If no config is found the agent falls back to UDP broadcast discovery
on port 4012 (payload `NOHDDX_DISCOVER`, expected reply
`NOHDDX_SERVER:<ip>:<port>`).

## Server endpoints used

| Method | Path                                 | Purpose                          |
|--------|--------------------------------------|----------------------------------|
| GET    | `/api/agents/ping`                   | discovery probe                  |
| POST   | `/api/agents/register`               | register agent + send hardware   |
| POST   | `/api/agents/{id}/status`            | progress / state updates         |
| POST   | `/api/agents/{id}/install`           | request install instructions     |
| GET    | (server-supplied)                    | stream OS image bytes            |

## Server-to-agent control plane

The agent exposes a tiny HttpListener on `:7000`:

| Method | Path        | Description                          |
|--------|-------------|--------------------------------------|
| GET    | `/ping`     | liveness probe -> "pong"             |
| GET    | `/info`     | JSON snapshot of detected hardware   |
| POST   | `/command`  | server pushes a command (JSON body)  |
| POST   | `/shutdown` | graceful agent shutdown              |

## Cross-platform notes

The agent is designed to run on Linux but compiles cleanly on Windows.
Anywhere a Linux-specific syscall, sysfs path, or external command is
required, the code is gated on `OperatingSystem.IsLinux()` and falls
back to a benign default on other platforms so you can develop and
debug end-to-end on Windows.
