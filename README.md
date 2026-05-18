# NoHddX — Diskless Boot Platform

> บูตเครื่องลูกข่ายทั้งฟลีตจาก OS image กลางผ่าน **PXE / iPXE + iSCSI**
> โดยไม่ต้องใช้ฮาร์ดดิสก์ในเครื่อง — เขียนด้วย .NET 8

**👉 อ่านรายละเอียดเต็ม หลักการทำงาน สถาปัตยกรรม และราคาที่
[xjanova.github.io/nohddx](https://xjanova.github.io/nohddx/)**

[![Site](https://img.shields.io/badge/website-xjanova.github.io%2Fnohddx-7c5cff)](https://xjanova.github.io/nohddx/)
[![.NET](https://img.shields.io/badge/.NET-8-512BD4)](https://dotnet.microsoft.com/)
[![iSCSI](https://img.shields.io/badge/iSCSI-RFC%203720-22d3ee)](https://datatracker.ietf.org/doc/html/rfc3720)

## TL;DR

- เครื่องลูกข่ายไม่ต้องมี HDD/SSD — บูตจาก iSCSI ผ่าน PXE/iPXE
- 1 base image ใช้ร่วมกันได้ N เครื่อง ผ่าน **Copy-on-Write overlay**
- รีเซ็ตเครื่องกลับสภาพต้นได้ในคลิกเดียว (drop overlay)
- iSCSI target พูด RFC 3720 จริง — CHAP, CRC32C digests, R2T multi-PDU
- REST API + SignalR live dashboard, Raft cluster (optional), Prometheus metrics

## Quickstart

```powershell
# 1. รัน server
.\scripts\start-server.ps1

# 2. สร้าง USB boot stick
.\scripts\build-usb.ps1 -ServerUrl http://192.168.1.10:8080

# 3. เสียบ USB / เปิด PXE แล้วบูตเครื่องเป้าหมาย
```

Server ฟังที่ `http://localhost:8080` (Swagger ที่ `/swagger`)

## โครงสร้าง

```
src/
  NohddX.Core / Database / Storage         shared, EF Core, VHD + CoW
  NohddX.Iscsi / Boot / Cluster            iSCSI target, PXE/iPXE/TFTP, Raft
  NohddX.ClientMgmt / Monitoring           clients, groups, WoL, Prometheus
  NohddX.Api / Server / Ui / Agent         REST + SignalR, host, WPF, Linux agent
tools/iso-builder, scripts/                USB image builder, PowerShell wrappers
tests/NohddX.Tests                          77 unit tests
```

## พอร์ตที่ใช้

| Service     | Port | Proto | Purpose                              |
|-------------|------|-------|--------------------------------------|
| HTTP API    | 8080 | TCP   | REST + SignalR + iPXE script         |
| iSCSI       | 3260 | TCP   | VHD-backed boot LUNs                 |
| TFTP        | 69   | UDP   | iPXE chainload                       |
| DHCP-Proxy  | 4011 | UDP   | PXE next-server hint                 |
| Discovery   | 4012 | UDP   | Agent broadcast probe                |

## เอกสารเพิ่มเติม

- **เว็บไซต์โปรเจค (อธิบายเต็ม + ราคา):** <https://xjanova.github.io/nohddx/>
- **Security guide:** [docs/SECURITY.md](docs/SECURITY.md)
- **REST API + iSCSI feature matrix:** [website → How it works](https://xjanova.github.io/nohddx/#how)
- **Use cases (โรงเรียน / เน็ตคาเฟ่ / คีออสก์ / ออฟฟิศ):** [website → Use cases](https://xjanova.github.io/nohddx/#usecases)

## Development

```bash
dotnet build NohddX.sln
dotnet test
```

## License

Open source — ดูรายละเอียดบน [GitHub](https://github.com/xjanova/nohddx)
