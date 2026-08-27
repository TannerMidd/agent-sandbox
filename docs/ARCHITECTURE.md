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
- Machine-helper state: `%ProgramData%\AgentSandbox`
- Guest workspace: `/home/ubuntu/work`
- Guest app control data: `/home/ubuntu/work/.agent-sandbox`

Settings, helper requests, preset manifests, and file operations use schema version 1. Unknown versions fail closed. Each managed VM stores its selected hardening configuration alongside its image, resources, and agent presets; older settings normalize to the compatibility profile that matches their existing behavior.

## Trust boundaries

The WinUI process runs as the current user. Administrative actions cross a named-pipe boundary to the elevated helper. The pipe ACL includes only the current user and local Administrators, and the helper validates both the request version and operation allow-list.

Multipass is located at its canonical Program Files path or accepted from `PATH` only when Windows version metadata identifies Canonical/Multipass. All lifecycle and file arguments use `ProcessStartInfo.ArgumentList`; user paths are never interpolated into a shell command. The embedded terminal uses the Windows ConPTY API with only the verified Multipass executable and exact instance name in its generated command line.

Guest file requests use path component arrays. The guest helper rejects separators, dot segments, NUL, symlink parents, special files, root escape, unbounded pages, unsafe archives, and stale source expectations.

## Lifecycle

The setup coordinator persists a state after each meaningful step. On relaunch it re-inspects the host, then selects the next safe state. Provisioning resolves a user-selected image from the compiled catalog (Ubuntu, Debian, Arch, Fedora, or Alpine) or validates an advanced custom HTTPS cloud-image URL. The selected Development, Balanced, Restricted, Offline, or custom hardening configuration is rendered into a temporary cloud-init file from a packaged marker template; missing markers and invalid combinations fail closed, and the temporary file is removed after launch. Cloud-init applies cross-distribution packages and the chosen update, sysctl, audit, privilege, SSH, inbound-firewall, and egress policies, writes `/etc/agent-sandbox/hardening.json`, and then the host verifies that policy artifact during health checks. The VM is stopped, snapshotted as `clean`, and restarted before online agent presets are installed. The image ID and any custom URL are stored with each managed VM so rebuilds use the same source. Multipass requires native guest architecture, and the current Windows release remains x64-only.

The app stores the VMs it creates and manages only those instances. Users choose a unique Multipass name for each VM and can switch the active target; import is allowed only for the exact existing `agent-dev` name and never renames or migrates it.
