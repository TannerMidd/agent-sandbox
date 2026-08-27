# Implementation status

Agent Sandbox is a buildable development preview. It is **not yet a publishable public beta** because the full workflow and physical Windows matrix have not passed.

## Implemented and verified locally

- .NET 10 WinUI 3 x64 application, dark navigation shell, MVVM state, dashboard, guided setup, Files, Recovery, Diagnostics, and Settings views
- Typed setup, sandbox, operation, snapshot, diagnostic, transfer, settings, preset, and guest-file contracts
- Resumable setup coordinator, resource policy, exact `agent-dev` import policy, and fake Multipass adapter
- Verified Multipass location, non-shell process arguments, lifecycle, health checks, clean snapshot, and exact snapshot restore target
- ACL-protected named-pipe elevated helper with correlated responses, protected-install enforcement, compiled operation/installer allow-list, secured installer copy, and reparse rejection
- Versioned Python standard-library guest helper and staged/atomic host transfer service with guest/host SHA-256 comparison and a persistent crash-cleanup journal
- Exact agent preset versions and npm integrity metadata
- Self-contained x64 publish and unsigned WiX 6.0.2 MSI build
- GitHub CI/release workflow, checksums, SPDX SBOM, immutable release, and artifact-attestation configuration
- Verified Multipass 1.16.3 download/install path with pinned Microsoft WinGet SHA-256, Windows Authenticode verification, and Canonical certificate validation
- Per-VM Linux image selection (Ubuntu, Debian, Arch, Fedora, Alpine, and validated custom HTTPS cloud images), image-aware resource/preset provisioning persisted for every VM, retryable interrupted preset installation, reliable partial-VM recovery, exact reported resource discovery, Multipass-storage disk checks, per-VM Development/Balanced/Restricted/Offline/custom hardening with exact artifact and effective post-restart control verification, exact legacy import, embedded ConPTY and external terminals, typed recovery confirmations, diagnostics export, local operation history, and once-daily release checks
- Dual-pane folder navigation with complete bounded host/guest listings, bidirectional drag/drop, cross-process serialized file/folder transfers, cancellation, crash-recoverable overwrite transactions, conflict policy, UTF-8 editing, create/rename/duplicate/trash/restore/purge, permissions, archive/extract, search, hidden items, and opt-in read-only system browsing
- Strict production builds and the automated .NET/domain/application/infrastructure/UI contract suites are maintained as release gates; guest-helper safety tests also cover Unicode, path, trash, and archive boundaries
- CI installs a pinned/hash-verified WinAppDriver, launches the exact self-contained WinUI publish through WebDriver, verifies primary accessibility names, clicks Settings, and checks the resulting view before packaging; the unsigned MSI/portable archive contain the app, isolated elevated-helper runtime, guest helper, and cloud-init assets
- Tagged release automation derives app/MSI/SBOM versions from a strict semantic-version tag and fails closed until the physical beta matrix and implementation-status approval are recorded

## Required before beta approval

- Complete accessibility testing and every physical-hardware and end-to-end item in [TEST-MATRIX.md](TEST-MATRIX.md), expanding the WinAppDriver suite for interactions that require those real VMs
- Configure the canonical GitHub owner/repository and run the tagged release workflow

No release should remove this gate or describe the app as beta-ready until the evidence is recorded.
