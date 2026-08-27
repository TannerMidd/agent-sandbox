# Threat model

## Security objective

Agent Sandbox reduces the impact of mistakes made by development agents by running their code in a Hyper-V Ubuntu VM and by minimizing privileged and host-filesystem access.

It is not intended to contain kernel exploits, malicious hypervisor escape research, hostile insiders with Windows administrator access, or secrets deliberately copied into the VM.

## Protected assets

- Windows files and credential stores
- Existing Multipass instances, driver selection, and storage location
- The configured sandbox workspace and snapshots
- Installer and preset supply-chain integrity
- User intent around overwrite, restore, purge, and rebuild operations

## Controls

- No host credential forwarding or persistent mounts.
- No arbitrary commands accepted by the elevated helper.
- Pinned installer hash plus publisher certificate validation.
- Exact instance/snapshot targeting and explicit destructive confirmations.
- Read/write guest file management only under `/home/ubuntu/work` by default.
- Optional system browsing is read-only and never uses sudo.
- Path components instead of shell strings; symlink/reparse parents are rejected.
- Upload staging and atomic guest commit; download `.partial`, metadata/size verification, and atomic host rename.
- Archive entry count and expanded-size limits, traversal rejection, and link/special-file rejection.
- Default conflict policy is `fail`; overwrite is never inferred.

## Residual risks

- Hyper-V and Multipass defects can cross the intended boundary.
- Downloaded files can harm Windows when opened by another application.
- Agent CLIs communicate with their providers and are governed by those providers' policies.
- npm and Ubuntu package infrastructure remain external supply-chain dependencies.
- Unsigned preview installers can be replaced unless users verify hashes and attestations from the canonical release.

Report vulnerabilities privately as described in [SECURITY.md](../SECURITY.md).
