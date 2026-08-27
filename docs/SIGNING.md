# Release and signing policy

Tagged releases are built by GitHub Actions from the immutable tag. The workflow publishes an unsigned x64 MSI, a portable diagnostic archive, SHA-256 files, an SPDX SBOM, and GitHub artifact attestations.

Unsigned previews are clearly labeled and may trigger SmartScreen. The project does not self-sign and does not imply public trust.

The maintainers intend to apply to [SignPath Foundation](https://signpath.org/) after the repository has public releases, a documented security policy, active maintenance, reproducible release automation, and the other program requirements. Acceptance is not guaranteed. If accepted, the policy and CI will be updated before any release is described as signed.

Preset version updates require a clean-VM verification run, exact version and integrity changes in `presets/`, test results, and maintainer review. Mutable `latest` tags are not used during first-run setup.
