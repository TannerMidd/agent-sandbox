# Privacy and system changes

Agent Sandbox has no application telemetry. It stores non-secret settings, operation history, and redacted rolling logs locally. Diagnostic export is initiated by the user and produces a local archive; the app does not upload it.

## System changes

With explicit consent, the app may:

- Enable the Windows Hyper-V optional feature and request a reboot.
- Install a pinned Canonical Multipass MSI.
- Create a fresh local NTFS Multipass storage directory and machine environment setting.
- Create and operate one Ubuntu 24.04 Multipass VM.

The app preserves a compatible existing Multipass installation, storage path, driver, instances, and snapshots. It never changes driver or migrates storage when instances exist.

## Network access

Windows may access Canonical to obtain the pinned Multipass installer and GitHub Releases at most once daily when update checks are enabled. The VM accesses Ubuntu package mirrors and npm while provisioning selected presets. Agent CLIs access their own providers only when the user runs and authenticates them inside the guest.

## Credentials

Host browser, Codex, Claude, Google, SSH, Git, and other credential stores are never copied or mounted into the VM. Authentication is performed in the guest terminal and remains guest data.

## Uninstall

Uninstall removes the application files. It deliberately does not delete the Multipass VM, snapshots, storage, or guest workspace. Removing those requires a separate exact-target action.
