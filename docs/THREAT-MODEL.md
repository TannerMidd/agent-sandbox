# Threat model

## Security objective

Agent Sandbox reduces the impact of mistakes made by development agents by running their code in a Hyper-V Linux VM and by minimizing privileged and host-filesystem access.

It is not intended to contain kernel exploits, malicious hypervisor escape research, hostile insiders with Windows administrator access, or secrets deliberately copied into the VM.

## Protected assets

- Windows files and credential stores
- Existing Multipass instances, driver selection, and storage location
- The configured sandbox workspace and snapshots
- Installer and preset supply-chain integrity
- User intent around overwrite, restore, purge, and rebuild operations

## Controls

- No host credential forwarding or persistent mounts.
- Every provisioned VM denies unsolicited inbound traffic and disables SSH password authentication, root login, forwarding, and X11 forwarding.
- Per-VM hardening ranges from a compatibility profile through automatic updates/kernel/audit safeguards to web-only or offline egress and removal of general passwordless sudo/Docker-socket access. Restricted profiles retain only an exact no-argument root-owned runtime verifier command so post-restart health checks can read root-only firewall and update state. Custom combinations are explicit and persisted.
- Offline egress cannot be combined with remote agent presets; contradictory automatic-update/offline configurations fail before launch.
- No arbitrary commands accepted by the elevated helper; portable builds cannot elevate, and installed helper paths must remain under protected Program Files without reparse points.
- Helper-compiled installer identity, reparse rejection plus enforced Administrators ownership/protected ACLs across the full staging chain, secured MSI copy, pinned hash, and publisher certificate validation before Windows Installer execution.
- Catalog image allow-list; advanced custom images require credential-free HTTPS and reject loopback hosts.
- Exact instance/snapshot targeting and explicit destructive confirmations.
- Read/write guest file management only under `/home/ubuntu/work` by default.
- Optional system browsing is read-only and never uses sudo.
- Path components instead of shell strings; symlink/reparse parents are rejected.
- Cross-process transfer serialization; upload staging and journaled guest replacement commit; request-scoped download staging, host/guest SHA-256 comparison, retryable `.partial` cleanup, journaled host directory replacement, and atomic host file rename.
- Archive entry count and expanded-size limits, traversal rejection, and link/special-file rejection.
- Default conflict policy is `fail`; overwrite is never inferred.

## Residual risks

- Hyper-V and Multipass defects can cross the intended boundary.
- Downloaded files can harm Windows when opened by another application.
- Development and Balanced profiles permit unrestricted guest egress. Web-only filtering is port-based rather than destination allow-listing, so HTTPS endpoints remain reachable; guest administrators can change guest policy when administrative tools are enabled.
- Restricted kernel settings can break debuggers, unprivileged containers, and tools that need user namespaces or BPF. Offline mode prevents agent sign-in, APIs, package downloads, and security updates.
- Agent CLIs communicate with their providers and are governed by those providers' policies.
- npm, Node.js, and the selected distribution's image/package infrastructure remain external supply-chain dependencies; a user-supplied custom image is trusted guest input and is not app-side digest verified.
- Unsigned preview installers can be replaced unless users verify hashes and attestations from the canonical release.

Report vulnerabilities privately as described in [SECURITY.md](../SECURITY.md).
