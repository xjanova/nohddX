# NoHddX Security Guide

NoHddX is a fleet boot platform — the server can write disk images to every
machine in your network. Treat the operator API the same way you treat your
DHCP/AD/PXE infrastructure: locked down, behind a firewall, on its own VLAN
where possible.

## Authentication

There are three caller classes:

| Caller        | Identity                              | How to authenticate                              |
|---------------|---------------------------------------|--------------------------------------------------|
| Operator UI   | A human via `NohddX.Ui` / curl / etc. | `X-Admin-Api-Key: <pre-shared key>` header       |
| Booted agent  | A `NohddX.Agent` instance on a client | `Authorization: Bearer <agent-token>` header     |
| iPXE / PXE    | The firmware on the target machine    | Anonymous — only `/api/boot/{mac}.ipxe` is open  |

The agent token is HMAC-SHA256 signed by `NohddX:Security:AgentTokenSecret`
and issued at registration time. It expires after `AgentTokenLifetimeHours`
(default 24).

## Required configuration in production

Set these via environment variables, **never** commit them:

```
NohddX__Security__AdminApiKey         = <random 32-byte hex>
NohddX__Security__AgentTokenSecret    = <random 32-byte base64>
NohddX__Security__BootstrapToken      = <random hex, baked into agent ISO>
ASPNETCORE_ENVIRONMENT                = Production
```

Generate keys with:

```powershell
[Convert]::ToBase64String([System.Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
```

```bash
openssl rand -base64 32
```

## What the API gates protect

* `/api/clients/*` — admin only. Adding a client lets you boot it from any
  registered image; deleting one disconnects it from its iSCSI target.
* `/api/images/*` — admin only for create/update/delete; agents and admins
  can `GET /{id}/download`.
* `/api/boot/{mac}.ipxe` — open. PXE has no header support; the protection
  here is that only registered MACs get a real boot script (others get a
  "please register" message).
* `/api/agents/ping` — open (liveness probe).
* `/api/agents/register` — open if no `BootstrapToken` is set, otherwise
  requires `X-Bootstrap-Token`. Rate-limited per IP.
* `/api/agents/{id}/status` and `/api/agents/{id}/install` — agent token
  required, and the token's agent ID must match the route ID.
* `/api/cluster/*`, `/api/storage/*`, `/api/groups/*`, `/api/monitoring/*` —
  admin only.

## Rate limiting

Configured per-policy under `NohddX:Security`:

* `AgentRegisterRatePerMinute` (default 30 / IP)
* `AdminRatePerMinute` (default 600 / identity)

The agent endpoints (status, install) are limited to 300/min per agent.

## Audit log

Every admin and security-sensitive action is appended to the `AuditLog`
table. Disable with `AuditLogEnabled: false`. Read recent entries via the
DB or — once you wire it — a future `/api/monitoring/audit` endpoint.

## Bootstrap token

Without `BootstrapToken`, any machine that can reach port 8080 can register
itself. With it, only an agent that knows the token can register. Bake
the token into the ISO/USB you ship to clients (`tools/iso-builder`).

## TLS

The default config exposes plain HTTP for local install convenience.
`appsettings.Production.json` adds an HTTPS endpoint on 8443. Provide a
certificate path + password through environment variables. For lab use a
self-signed cert; for production buy a real one or use ACME.

## Cluster control plane

Cluster control messages (Heartbeat, RequestVote, VoteResponse) ride a UDP
socket on `Cluster:ClusterPort` (default 5000). They are **not signed** — the
control plane assumes the cluster network is private. Run cluster traffic
on a dedicated VLAN; do not bind the cluster port on the public NIC.
