# Implementation status

Agent Sandbox is a buildable development preview. It is **not yet a publishable public beta** because the full workflow and physical Windows matrix have not passed.

## Implemented and verified locally

- .NET 10 WinUI 3 x64 application, dark navigation shell, MVVM state, dashboard, guided setup, Files, Recovery, Diagnostics, and Settings views
- Typed setup, sandbox, operation, snapshot, diagnostic, transfer, settings, preset, and guest-file contracts
- Resumable setup coordinator, resource policy, exact `agent-dev` import policy, and fake Multipass adapter
- Verified Multipass location, non-shell process arguments, lifecycle, health checks, clean snapshot, and exact snapshot restore target
- ACL-protected named-pipe elevated helper with compiled operation allow-list
- Versioned Python standard-library guest helper and staged/atomic host transfer service
- Exact agent preset versions and npm integrity metadata
- Self-contained x64 publish and unsigned WiX 6.0.2 MSI build
- GitHub CI/release workflow, checksums, SPDX SBOM, immutable release, and artifact-attestation configuration
- Verified Multipass 1.16.3 download/install path with pinned Microsoft WinGet SHA-256, Windows Authenticode verification, and Canonical certificate validation
- Resource/preset provisioning, exact legacy import, embedded ConPTY and external terminals, typed recovery confirmations, diagnostics export, local operation history, and once-daily release checks
- Dual-pane folder navigation, bidirectional drag/drop, file/folder transfers, cancellation, conflict policy, UTF-8 editing, create/rename/duplicate/trash/restore/purge, permissions, archive/extract, search, hidden items, and opt-in read-only system browsing
- Strict production builds pass with zero warnings and zero errors; 35 .NET unit/contract/UI-contract tests and 10 guest-helper safety tests pass locally (2 platform-specific guest tests skip on Windows)
- The exact self-contained publish passes a real WinUI launch smoke test, and the unsigned MSI/portable archive contain the app, isolated elevated-helper runtime, guest helper, and cloud-init assets

## Required before beta approval

- Add full Appium/WinAppDriver interaction automation beyond the current XAML/UI contract suite
- Complete accessibility testing and every physical-hardware and end-to-end item in [TEST-MATRIX.md](TEST-MATRIX.md)
- Configure the canonical GitHub owner/repository and run the tagged release workflow

No release should remove this gate or describe the app as beta-ready until the evidence is recorded.
