# NoHddX USB image builder

A small .NET 8 console tool that produces a bootable USB image for
PXE/iSCSI clients to chainload the NoHddX server.

## What it produces

A raw disk image (`nohddx-boot.img`) with:

- **MBR** with a single bootable FAT partition (type 0x0C).
- `/EFI/BOOT/BOOTX64.EFI` — iPXE for UEFI x86_64.
- `/EFI/BOOT/BOOTIA32.EFI` — iPXE for UEFI 32-bit (best-effort copy).
- `/ipxe.lkrn` — iPXE as a Linux-style kernel for SYSLINUX/legacy boot.
- `/undionly.kpxe`, `/snponly.efi` — chainload binaries (also mirrored
  to `tools/ipxe/` so the server's TFTP service can serve them).
- `/init.ipxe` and `/autoexec.ipxe` — chain script pre-configured with
  your server URL.
- `/nohddx-agent.json` — config consumed by the agent if you boot a
  Linux mini-OS that runs `nohddx-agent`.
- `/BOOT-INSTRUCTIONS.txt` — short reference for the operator.

## Quick start

```powershell
# From the repo root:
dotnet run --project tools/iso-builder -- `
    --server-url http://192.168.1.10:8080 `
    --output dist/nohddx-boot.img `
    --size-mb 64
```

Then flash the resulting image to a USB stick:

- Windows: [Rufus](https://rufus.ie) → "DD Image" mode, select the
  generated `.img`, write.
- macOS / Linux:
  ```bash
  sudo dd if=dist/nohddx-boot.img of=/dev/sdX bs=4M conv=fsync status=progress
  ```

## Boot flow

1. The target machine boots from USB.
2. UEFI firmware loads `EFI/BOOT/BOOTX64.EFI` (= iPXE).
3. iPXE runs its embedded default script. With DHCP option 67 set on
   your DHCP server (or by typing the chain command manually) it pulls
   `http://<server>/api/boot/${mac:hexhyp}.ipxe` from NoHddX.
4. NoHddX returns a per-client `sanboot iscsi:...` script that points
   at the iSCSI target served by NoHddX.Iscsi for the assigned image.

If your DHCP server does not set option 67, press `Ctrl+B` at the iPXE
banner and type:

```
chain http://192.168.1.10:8080/api/boot/${mac:hexhyp}.ipxe
```

(replace the IP with your NoHddX server).

## Re-building

The first run downloads iPXE binaries from `https://boot.ipxe.org` into
`./cache/`. Subsequent runs reuse them. Delete the cache folder to
force a fresh download.

## Customising the boot script

The image contains `/init.ipxe` and `/autoexec.ipxe` as plain text. You
can edit either with any text editor after writing the USB to add menu
items, fix server URLs, etc.

## Requirements

- .NET 8 SDK (only at build time)
- Internet access on first run (to fetch iPXE binaries)
