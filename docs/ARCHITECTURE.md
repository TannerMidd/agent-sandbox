# Architecture

Agent Sandbox is split into five production assemblies and one daemonless guest helper.

## Layers

1. **AgentSandbox.App** — WinUI 3 navigation, accessible XAML views, MVVM commands, and the composition root.
2. **AgentSandbox.Domain** — immutable records, setup/sandbox/operation states, resource rules, guest-path policy, and versioned contracts.
3. **AgentSandbox.Application** — setup coordinator and narrow service interfaces. It owns workflow order without knowing process details.
4. **AgentSandbox.Infrastructure** — safe process arguments, verified Multipass discovery, lifecycle/snapshots, local settings, terminal launch, preset manifests, and staged transfers.
5. **AgentSandbox.SetupHelper** — a separately compiled elevated executable. It allows only host inspection, Hyper-V enablement, fresh-storage configuration, and verified Multipass MSI installation.
6. **guest_helper.py** — copied into the VM and invoked through `multipass exec` per request. It uses only the Python standard library and has no listener or persistent daemon.

## State and storage

- Non-secret settings: `%LocalAppData%\AgentSandbox\settings.json`
- Rolling redacted operation history: `%LocalAppData%\AgentSandbox\operations.jsonl`
- Crash-recovery journal for generated host partials: `%LocalAppData%\AgentSandbox\pending-transfers.json`
- Machine-helper state: `%ProgramData%\AgentSandbox`
- Guest workspace: `/home/ubuntu/work`
- Guest app control data: `/home/ubuntu/work/.agent-sandbox`

Settings, helper requests, preset manifests, and file operations use schema version 1. Unknown versions fail closed. Each managed VM stores its selected hardening configuration alongside its image, resources, and agent presets; older settings normalize to the compatibility profile that matches their existing behavior.

## Trust boundaries

The WinUI process runs as the current user. Administrative actions cross a named-pipe boundary to the elevated helper. The desktop enables elevation only from the ACL-protected Program Files installation and rejects reparse-point helper paths; portable diagnostics builds cannot elevate. The pipe ACL includes only the current user and local Administrators, and both sides correlate protocol version and request ID. The helper validates the operation allow-list and its own compiled installer identity, resets the owner and protected ACL on the full ProgramData staging chain, copies the approved MSI into that admin/SYSTEM-only directory, then hashes and authenticates that immutable copy before invoking Windows Installer.

Multipass is accepted only from Canonical's protected Program Files location after installed-product, version-resource, and Authenticode signer verification. All lifecycle and file arguments use `ProcessStartInfo.ArgumentList`; user paths are never interpolated into a shell command. The embedded terminal uses the Windows ConPTY API with only the verified Multipass executable and exact instance name in its generated command line.

Guest file requests use path component arrays. The guest helper rejects separators, dot segments, NUL, symlink parents, special files, root escape, unbounded pages, unsafe archives, and stale source expectations. Downloads are copied to a request-scoped generated guest staging path, hashed there, transferred, and committed on Windows only after the host reproduces the SHA-256 digest. A cross-process transfer lease prevents reconciliation from racing active jobs. Local journals remove crash-left host partials and recover interrupted directory-overwrite transactions on reconnection; failed cleanup remains journaled for another attempt. Guest overwrites use a serialized replacement journal that either completes the staged commit or restores the prior destination after interruption.

## Lifecycle

The setup coordinator persists a state after each meaningful step. On relaunch it re-inspects the host, then selects the next safe state. Provisioning resolves a user-selected image from the compiled catalog (Ubuntu, Debian, Arch, Fedora, or Alpine) or validates an advanced custom HTTPS cloud-image URL. The selected Development, Balanced, Restricted, Offline, or custom hardening configuration is rendered into a temporary cloud-init file from a packaged marker template; missing markers and invalid combinations fail closed, and the temporary file is removed after launch. Cloud-init applies cross-distribution packages and the chosen update, sysctl, audit, privilege, SSH, inbound-firewall, and egress policies, writes `/etc/agent-sandbox/hardening.json`, and then the host parses that artifact and compares every policy field with the exact request during health checks. Effective SSH, firewall, privilege, Docker, kernel, audit, update-scheduling, and unprivileged-feature controls are checked before the baseline snapshot and again after restart. Restricted guests receive one root-owned, no-argument runtime verifier through an exact sudoers command allow-list so host health checks can inspect root-only firewall state without restoring general passwordless administration. The VM is stopped, snapshotted as `clean`, and restarted before online tool presets are installed. Interrupted preset installation is retained as pending state and can be retried without rebuilding the VM; incomplete VM provisioning remains explicitly preserved for diagnostics and exact-target rebuild. The image ID and any custom URL are stored with each managed VM so rebuilds use the same source. Multipass requires native guest architecture, and the current Windows release remains x64-only.

Provisioning intent is persisted before every VM launch, including additional VMs. Completed provisioning, pending optional-tool installation, and preserved partial provisioning are distinct states so recovery never depends on process memory. The app stores the VMs it creates and manages only those instances. Users choose a unique Multipass name for each VM and can switch the active target; import is allowed only for the exact existing `agent-dev` name and never renames or migrates it.
