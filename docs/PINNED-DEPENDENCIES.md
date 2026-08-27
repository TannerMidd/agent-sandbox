# Pinned dependencies

First-run setup never resolves a mutable `latest` installer or runtime tag.

| Component | Version | Immutable source | Integrity |
| --- | --- | --- | --- |
| Canonical Multipass for Windows x64 | 1.16.3 | `https://github.com/canonical/multipass/releases/download/v1.16.3/multipass-1.16.3+win-win64.msi` | SHA-256 `F5BFF63D13FB1377A72B8DD6D277BBDD3369B1F278F4C85D2C8427A2E7D38D39`; valid Canonical Authenticode signature and certificate chain required |
| Node.js Linux x64 | 22.23.2 LTS | `https://nodejs.org/download/release/v22.23.2/node-v22.23.2-linux-x64.tar.xz` | SHA-256 `d60acfe00a2932254bb0ad20e01b0d74397a0875595de719654b214f4b03f307` |

The Multipass hash is the value published in Microsoft’s WinGet manifest for `Canonical.Multipass` 1.16.3. The Node.js hash is published in the Node.js 22.23.2 `SHASUMS256.txt` file.

Agent CLI versions and npm SHA-512 integrity values are recorded in `presets/*.json`. Installation first compares the exact package version’s registry integrity with the pinned manifest, then lets npm verify the downloaded package.

Maintainers update these values only after a clean-VM provisioning run, preset version check, authentication smoke test, and file/snapshot recovery pass.
