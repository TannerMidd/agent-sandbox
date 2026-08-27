# Beta verification matrix

The beta is publishable only after every selected workflow below has a recorded passing result on physical Windows 11 hardware.

## Automated gates

- Domain setup transitions, resource limits, target names, path components, and read-only roots
- Application resume and exact legacy import policy
- Infrastructure atomic settings, separated process arguments, and diagnostic redaction
- Guest helper Unicode, spaces, newline preservation, cursor revisions, symlink parents, and trash/restore
- WinUI build, setup-helper build, WiX build, checksums, SBOM, and attestations

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
- Provision Ubuntu 24.04 and each exact-version preset.
- Authenticate each agent only inside the guest terminal.
- Round-trip a Unicode project, edit text, rename/copy/move, trash, restore, archive, and extract.
- Create and restore an exact snapshot.
- Interrupt upload and download operations, relaunch, and reconcile cleanup.
- Verify 100%, 125%, 150%, and 200% scale; minimum window; keyboard-only navigation; screen-reader names; focus order; reduced motion; dark/light/high contrast.

Release maintainers replace each `Pending` cell with the tested app version, Windows build, Multipass version, hardware summary, date, and result. A checklist is not evidence of a pass.
