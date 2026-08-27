# Pinned dependencies

First-run setup never resolves a mutable `latest` installer or runtime tag.

| Component | Version | Immutable source | Integrity |
| --- | --- | --- | --- |
| Canonical Multipass for Windows x64 | 1.16.3 | `https://github.com/canonical/multipass/releases/download/v1.16.3/multipass-1.16.3+win-win64.msi` | SHA-256 `F5BFF63D13FB1377A72B8DD6D277BBDD3369B1F278F4C85D2C8427A2E7D38D39`; valid Canonical Authenticode signature and certificate chain required |
| Microsoft WinAppDriver (CI only) | 1.2.1 | `https://github.com/microsoft/WinAppDriver/releases/download/v1.2.1/WindowsApplicationDriver_1.2.1.msi` | SHA-256 `A76A8F4E44B29BAD331ACF6B6C248FCC65324F502F28826AD2ACD5F3C80857FE` |
| Node.js Linux x64 | 22.23.2 LTS | `https://nodejs.org/download/release/v22.23.2/node-v22.23.2-linux-x64.tar.xz` | SHA-256 `d60acfe00a2932254bb0ad20e01b0d74397a0875595de719654b214f4b03f307` |
| Debian generic cloud image x64 | 13, build 20260826-2582 | `https://cloud.debian.org/images/cloud/trixie/20260826-2582/debian-13-generic-amd64-20260826-2582.qcow2` | Official Debian HTTPS source; downloaded and cached by Multipass |
| Arch Linux cloud image x64 | 2026.08.15 build 573966 | `https://geo.mirror.pkgbuild.com/images/v20260815.573966/Arch-Linux-x86_64-cloudimg-20260815.573966.qcow2` | Official Arch Linux HTTPS source; downloaded and cached by Multipass |
| Fedora Cloud Base x64 | 44-1.7 | `https://download.fedoraproject.org/pub/fedora/linux/releases/44/Cloud/x86_64/images/Fedora-Cloud-Base-Generic-44-1.7.x86_64.qcow2` | Official Fedora HTTPS source; downloaded and cached by Multipass |
| Alpine generic UEFI cloud image x64 | 3.22.5 r0 | `https://dl-cdn.alpinelinux.org/alpine/v3.22/releases/cloud/generic_alpine-3.22.5-x86_64-uefi-cloudinit-r0.qcow2` | Official Alpine HTTPS source; downloaded and cached by Multipass |

Ubuntu selections use Canonical's Multipass `24.04` and `22.04` aliases so they receive the current point release for that LTS. Catalog custom-distro URLs are immutable and allow-listed; user-supplied images require an explicit HTTPS URL without embedded credentials, fragments, or a loopback host. Multipass performs HTTPS download and cache management. Agent Sandbox does not currently duplicate Multipass's image cache to perform a second digest verification.

The Multipass hash is the value published in Microsoft’s WinGet manifest for `Canonical.Multipass` 1.16.3. The Node.js hash is published in the Node.js 22.23.2 `SHASUMS256.txt` file.

Agent CLI versions and npm SHA-512 integrity values are recorded in `presets/*.json`. Installation first compares the exact package version’s registry integrity with the pinned manifest, then lets npm verify the downloaded package.

Maintainers update these values only after a clean-VM provisioning run, preset version check, authentication smoke test, and file/snapshot recovery pass.
