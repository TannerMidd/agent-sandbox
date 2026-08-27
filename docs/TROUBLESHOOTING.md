# Troubleshooting

## Setup does not continue after reboot

Open Agent Sandbox again. Setup state is persisted in `%LocalAppData%\AgentSandbox\settings.json` and the host is re-inspected. Open **Diagnostics** if the wizard remains in review.

## Existing Multipass is reported incompatible

Agent Sandbox requires the Hyper-V driver. It will not switch an existing driver or migrate storage automatically. Existing instances must be handled outside Agent Sandbox before a deliberate driver change.

## Provisioning failed

Export Diagnostics and review the cloud-init and Multipass records. Partial provisioning is not overwritten automatically. Repair or remove the exact failed instance before retrying.

## File transfer was interrupted

Downloads use a `.partial` sibling and uploads use `.agent-sandbox/staging`. The next file operation reconciles stale staging data. The transfer queue records whether cleanup is pending.

## SmartScreen warns about the installer

Initial public-beta installers are unsigned. Verify the release SHA-256 and GitHub artifact attestation. Do not trust an installer obtained from another location.

## Uninstall left the VM behind

This is intentional. Reinstall the app to manage it, or use Canonical Multipass directly if you deliberately want to remove the exact VM.
