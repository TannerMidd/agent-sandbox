# Beta verification matrix

The beta is publishable only after every selected workflow below has a recorded passing result on physical Windows 11 hardware.

## Automated gates

- Domain setup transitions, resource limits, hardening preset/option invariants, target names, path components, and read-only roots
- Application resume and exact legacy import policy
- Infrastructure atomic settings, exact Multipass resource parsing, storage-volume selection, list-only partial-VM detection, protected signed executable discovery, protected helper location, helper-compiled/secured installer identity, exact hardening-artifact and effective-control verification, separated process arguments, and diagnostic redaction
- Guest helper Unicode, spaces, newline preservation, cursor revisions, request-scoped SHA-256 download staging, serialized crash-recoverable replacement transactions, active-staging preservation, symlink/control paths, and trash/restore
- Host cross-process transfer lease, retryable crash-journal cleanup, directory-overwrite recovery, and digest reproduction tests
- WinUI build and pinned/hash-verified WinAppDriver interaction test (published-app launch, accessibility names, navigation click, resulting view), setup-helper build, WiX build, checksums, SBOM, and attestations
- Release workflow fails closed while this matrix contains pending physical results

## Manual hardware matrix

| Scenario | Pro x64 | Enterprise x64 |
|---|---:|---:|
| Hyper-V disabled → UAC → reboot → resume | Pending | Pending |
| Hyper-V already enabled | Pending | Pending |
| Fresh Multipass install | Pending | Pending |
| Compatible existing Multipass preserved | Pending | Pending |
| Existing instances prevent driver/storage migration | Pending | Pending |
| Low memory and low disk recommendations | Pending | Pending |
| Failed cloud-init and interrupted provisioning recovery | Pending | Pending |
| Uninstall preserves VM, snapshots, and workspace | Pending | Pending |

## End-to-end workflow

- Install from a GitHub Release without using a terminal.
- Provision every catalog image (Ubuntu 24.04/22.04, Debian 13, Arch Linux, Fedora Cloud 44, and Alpine 3.22), one representative custom HTTPS cloud image, and each exact-version agent preset.
- Provision Development, Balanced, Restricted, and Offline hardening plus one custom combination on every package-manager family; verify `/etc/agent-sandbox/hardening.json`, SSH policy, firewall state, update scheduling, sysctl/audit state, sudo/Docker access, egress behavior, and `clean` snapshot persistence.
- Authenticate each agent only inside the guest terminal.
- Round-trip a Unicode project, edit text, rename/copy/move, trash, restore, archive, and extract.
- Create and restore an exact snapshot.
- Interrupt upload and download operations, relaunch, and reconcile cleanup.
- Verify 100%, 125%, 150%, and 200% scale; minimum window; keyboard-only navigation; screen-reader names; focus order; reduced motion; dark/light/high contrast.

Release maintainers replace each `Pending` cell with the tested app version, Windows build, Multipass version, hardware summary, date, and result. A checklist is not evidence of a pass.
