# Changelog

All notable changes are documented here. The project follows Semantic Versioning while the public API is unstable during 0.x previews.

## [Unreleased]

- Replaced the legacy WinForms control surface with a .NET 10 WinUI 3 application foundation.
- Added typed setup, lifecycle, snapshot, diagnostics, transfer, settings, and guest-file contracts.
- Added a narrow ACL-pipe elevated helper and resumable setup coordinator.
- Added safe Multipass discovery and lifecycle operations.
- Added user-named multi-VM creation, selection, lifecycle, files, snapshots, rebuild, and exact-target deletion.
- Added per-VM Linux image selection for Ubuntu 24.04/22.04, Debian 13, Arch Linux, Fedora Cloud 44, Alpine 3.22, and advanced custom HTTPS cloud images with image-aware resources and cross-distro provisioning.
- Added a daemonless Python guest helper with paged listings, text editing, trash/restore, permissions, archives, and strict path controls.
- Added a professional dark dashboard, dual-pane file workspace, recovery, diagnostics, and settings views.
- Replaced the placeholder Windows artwork with a branded multi-resolution app, taskbar, tile, and splash icon set.
- Added exact-version Codex, Claude Code, Gemini CLI, and Pi manifests with npm integrity metadata.
- Added tests, unsigned WiX release scaffolding, CI, documentation, SBOM, checksum, and attestation workflows.
- Fixed existing `agent-dev` discovery so import remains available during host-review states and never renames, migrates, or rebuilds the VM.
- Replaced the elevated-only Hyper-V readiness query with read-only CIM checks that work for the normal desktop user.
- Fixed Multipass discovery for Canonical's Windows package by validating the exact Program Files path against its installed-product publisher record.
- Added a visible guest connection check and fixed Windows Terminal launch through the per-user execution alias.
- Replaced redirected-stdin guest requests with bounded, one-shot request files because Multipass on Windows could leave the helper waiting indefinitely.
- Made the dashboard summary responsive so connection and terminal controls remain usable at high display scaling and minimum window width.
- Aligned the active-VM picker with its adjacent dashboard actions.
- Added live CPU, memory, and disk usage visuals for the active running VM.
