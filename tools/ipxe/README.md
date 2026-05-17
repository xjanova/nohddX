# tools/ipxe

The TFTP server in `NohddX.Boot` serves PXE chainload binaries from
this directory at boot time.

The directory is populated automatically the first time you run the
USB image builder:

```powershell
dotnet run --project tools/iso-builder -- --server-url http://<your-ip>:8080
```

The builder downloads from `https://boot.ipxe.org` and mirrors the
following files here:

- `undionly.kpxe`  — BIOS PXE chainload
- `snponly.efi`    — UEFI x86_64 PXE chainload
- `ipxe.efi`       — UEFI x86_64 standalone iPXE
- `ipxe.lkrn`      — Linux-format iPXE for SYSLINUX

If you cannot reach `boot.ipxe.org`, you can also build iPXE yourself
from <https://github.com/ipxe/ipxe> and drop the resulting binaries
into this directory using the same filenames.

These files are intentionally not checked in; they are downloaded on
demand.
