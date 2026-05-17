# NoHddX — Diskless Boot System

NoHddX is an enterprise diskless boot platform written in .NET 8. It
lets a fleet of client machines boot a centrally-managed OS image from
the network — no local hard drive required — using PXE / iPXE chain
boot to an iSCSI target served by NoHddX itself.

## What's in the box

```
src/
  NohddX.Core/         shared models, options, interfaces
  NohddX.Database/     EF Core context + repositories (SQLite/Postgres)
  NohddX.Storage/      VHD image catalog + CoW overlay engine
  NohddX.Iscsi/        iSCSI target service that serves boot images
  NohddX.Boot/         DHCP-Proxy, TFTP, iPXE script generator,
                       UDP discovery responder
  NohddX.Cluster/      Raft-based cluster coordination (optional)
  NohddX.ClientMgmt/   client / group / hardware-profile services + WoL
  NohddX.Monitoring/   Prometheus metrics + alerts
  NohddX.Api/          ASP.NET Core controllers + SignalR hub
  NohddX.Server/       hostable web server that wires everything together
  NohddX.Ui/           WPF operator console (Windows)
  NohddX.Agent/        Linux bootstrap agent that runs on the client

tools/
  iso-builder/         .NET CLI tool that builds the bootable USB image
  ipxe/                cache of iPXE binaries (populated on demand)

scripts/               quickstart wrappers for Windows
```

## Quickstart

### 1. Run the server

```powershell
.\scripts\start-server.ps1
```

This restores, builds, and runs `NohddX.Server`. The HTTP API listens
on **<http://localhost:8080>** by default. Swagger UI is at
<http://localhost:8080/swagger> in development.

The same process binds:

| Service        | Port      | Protocol | Purpose                                  |
|----------------|-----------|----------|------------------------------------------|
| HTTP API       | 8080      | TCP      | REST + SignalR + iPXE script endpoint    |
| iSCSI target   | 3260      | TCP      | Serves VHD-backed boot LUNs              |
| TFTP           | 69        | UDP      | iPXE chainload binaries                  |
| DHCP-Proxy     | 4011      | UDP      | PXE next-server hint (no DHCP conflict)  |
| Discovery      | 4012      | UDP      | Replies to agent broadcast probes        |

> **Privileged ports.** TFTP (69) and DHCP-Proxy (4011) are below 1024
> on Linux and require Administrator rights on Windows. Run the
> elevated PowerShell to bind them; otherwise edit
> `src/NohddX.Server/appsettings.json` to disable those services.

### 2. Build the USB boot stick

```powershell
.\scripts\build-usb.ps1 -ServerUrl http://192.168.1.10:8080
```

This downloads pre-built iPXE binaries from `boot.ipxe.org`, caches
them under `tools/iso-builder/cache/`, mirrors them to `tools/ipxe/`
(so the running TFTP service can serve them), and writes
`dist/nohddx-boot.img` — a UEFI-bootable raw disk image.

Flash it to a USB stick:

- **Windows:** open [Rufus](https://rufus.ie), pick the `.img` in DD
  Image mode, write.
- **Linux / macOS:**
  ```bash
  sudo dd if=dist/nohddx-boot.img of=/dev/sdX bs=4M conv=fsync status=progress
  ```

### 3. Boot a target machine

1. Plug the USB stick into the target.
2. Boot the target in **UEFI** mode (most modern firmware does this
   automatically when no internal disk is found).
3. Firmware loads `EFI/BOOT/BOOTX64.EFI` (= iPXE).
4. iPXE acquires DHCP and chainloads `http://<server>/api/boot/<mac>.ipxe`.
5. The server responds with a `sanboot iscsi:...` script and the
   client boots from the assigned image.

If your DHCP server does not set option 67, press `Ctrl+B` at the iPXE
banner and type:

```
chain http://192.168.1.10:8080/api/boot/${mac:hexhyp}.ipxe
```

### 4. Operator workflow

In the WPF console (`NohddX.Ui` — runs on Windows):

1. **Settings** → **Connection** tab: enter the server URL and (in
   production) the admin API key. Hit *Test connection* to confirm
   reachability, then *Save*.
2. **Images** tab → register your VHD-backed OS image.
3. **Clients** tab → either wait for an unknown client to PXE-boot
   (NoHddX will register it automatically as "unassigned"), or click
   *Add Client* and enter the MAC. MAC accepts any common format
   (`AA:BB:CC:DD:EE:FF`, `AA-BB-CC-DD-EE-FF`, `AABBCCDDEEFF`); it is
   normalised to `AA-BB-CC-DD-EE-FF` internally.
4. Select the client → *Assign Image* → pick from the image list.
   This also registers an iSCSI target in the running session so the
   client can boot immediately (no server restart needed).
5. *Wake* or hardware-reboot the target → it iSCSI-boots into the
   assigned image. The Dashboard tab updates live over SignalR.

Per-client overlay (CoW) data is reset by selecting the client and
choosing *Reset overlay* — useful for kiosks that should boot fresh
each session.

The `NohddX.Agent` Linux mini-OS binary is an alternative path: build
it with `dotnet publish -r linux-x64 --self-contained` and embed it in
a Linux ISO that you boot instead of straight iPXE. The agent will
auto-discover the server via UDP broadcast (port 4012), register, and
let the operator pick **persistent install** (downloads + writes the
image to a local disk; fully implemented), **diskless USB+net**, or
**network boot** (these two delegate to server-provided scripts and
expect a Linux runtime with `iscsiadm`/`kexec`).

## REST endpoints (cheat sheet)

MAC address path parameters are matched case-insensitively in the canonical
`AA-BB-CC-DD-EE-FF` form (uppercase hyphen-separated) — this matches what
iPXE substitutes for `${mac:hexhyp}`. Colon-separated input is accepted in
JSON bodies but stored in hyphen form server-side.

| Method | Path                              | Used by  | Description                              |
|--------|-----------------------------------|----------|------------------------------------------|
| GET    | `/api/agents/ping`                | Agent    | Discovery / liveness probe (open)        |
| POST   | `/api/agents/register`            | Agent    | Register, send hardware snapshot         |
| POST   | `/api/agents/{id}/status`         | Agent    | Progress / state update                  |
| POST   | `/api/agents/{id}/install`        | Agent    | Get install instructions (URL + size)    |
| GET    | `/api/boot/{mac}.ipxe`            | iPXE     | Per-client iPXE script (sanboot iSCSI)   |
| GET    | `/api/clients`                    | UI       | List registered clients                  |
| POST   | `/api/clients`                    | UI       | Manually register a client               |
| POST   | `/api/clients/{id}/assign`        | UI       | Assign an image to a client              |
| POST   | `/api/clients/{id}/wake`          | UI       | Wake-on-LAN to the client                |
| POST   | `/api/clients/{id}/reset`         | UI       | Drop the per-client CoW overlay          |
| GET    | `/api/images`                     | UI       | List boot images                         |
| GET    | `/api/images/{id}/download`       | Agent    | Stream the raw image bytes               |
| GET    | `/api/cluster/status`             | UI       | Cluster nodes + leader + load            |
| GET    | `/api/storage/health`             | UI       | Pool totals + RAID status                |
| GET    | `/api/storage/disks`              | UI       | Per-disk health (portable, no SMART yet) |
| GET    | `/api/monitoring/health`          | UI       | Per-component health (iSCSI/TFTP/DHCP)   |
| GET    | `/api/monitoring/audit`           | UI       | Recent audit log entries (filterable)    |

A live SignalR hub at `/hubs/dashboard` pushes `ClientStatusChanged` /
`BootEventOccurred` / `ClusterStateChanged` notifications to the operator UI.

## Development

```powershell
# Build everything
dotnet build NohddX.sln

# Run the server in debug mode
.\scripts\start-server.ps1

# Run the operator UI (Windows only — WPF target)
dotnet run --project src/NohddX.Ui

# Run the agent on Linux (after publishing)
sudo ./nohddx-agent
```

## Configuration

Server settings live in `src/NohddX.Server/appsettings.json`. The most
important keys for first-time setup:

```jsonc
"NohddX": {
  "StorageBasePath": "./storage",                 // base for VHDs
  "BaseImagesPath":  "./storage/bases",
  "DhcpProxy":  { "Enabled": true,  "Port": 4011 },
  "Iscsi":      { "Port": 3260 },
  "Tftp":       { "Enabled": true,  "Port": 69, "IpxeBinaryPath": "../../tools/ipxe" },
  "Discovery":  { "Enabled": true,  "Port": 4012, "AnnouncedPort": 8080 },
  "Security":   { "AuthEnabled": true, "AdminApiKey": "...", "AgentTokenSecret": "..." }
}
```

## Security & enterprise hardening

NoHddX ships with authenticated management APIs, agent bearer tokens,
per-IP rate limiting, request-size caps, audit logging, and a real
Raft RPC control plane. Read the full guide before deploying:

- [docs/SECURITY.md](docs/SECURITY.md) — keys to set, what each endpoint
  protects, TLS, bootstrap tokens, and the cluster control plane.
- `appsettings.Production.json` — template that wires HTTPS on 8443 and
  pulls every secret from environment variables.

## iSCSI protocol features

The target speaks enough RFC 3720 to boot Windows / Linux clients from
real OS images. Negotiable features in current builds:

| Feature | Status | Notes |
|---|---|---|
| Login auth: `None` | ✓ | Default when `NohddX:Iscsi:ChapEnabled=false` |
| Login auth: `CHAP` (MD5) | ✓ | Enforced when `ChapEnabled=true`; rejects `AuthMethod=None` and security-stage bypass |
| `HeaderDigest=CRC32C` | ✓ | Negotiated when initiator offers; CRC over the 48-byte BHS |
| `DataDigest=CRC32C` | ✓ | Negotiated when initiator offers; CRC over padded data segment |
| `MaxRecvDataSegmentLength` | ✓ | Negotiated `min(offered, 262144)`; defaults to RFC 8192 |
| `MaxBurstLength` | ✓ | Negotiated; Data-In F-bit + DataSN reset at each burst boundary |
| `InitialR2T` / Data-Out | ✓ | Multi-PDU writes work; Microsoft / Linux iSCSI initiators tested |
| SCSI: Inquiry / TestUnitReady / ReadCapacity 10+16 / Read 10+16 / Write 10+16 / ModeSense / ReportLuns | ✓ | Sufficient for boot + steady-state I/O |

## Tests

```powershell
dotnet test
```

The `tests/NohddX.Tests` project covers (77 tests as of last commit):

- BlockMap persistence
- CoW overlay engine (writes don't touch the base, per-client isolation, reset revert)
- iSCSI PDU parse/build, CRC32C wire format
- CHAP MD5 handshake (full path + every rejection mode)
- R2T multi-PDU write reassembly
- Per-burst Data-In framing (DataSN reset, BufferOffset monotonicity)
- Digest negotiation policy (CRC32C preferred, unknown algos → None)
- DHCP packet round-trip
- BootEndpointHandler MAC normalization
- ClientManager iSCSI hook + MAC normalization
- IscsiTargetBootstrap re-hydration
- Agent token signing/expiry
- Raft envelope wire format
