# Contributing

Thank you for helping make Agent Sandbox safer and easier to use.

1. Discuss substantial behavior or trust-boundary changes in an issue first.
2. Keep domain policy independent from WinUI and process details.
3. Add tests for every state transition, path rule, target-selection rule, and recovery behavior you change.
4. Run all .NET and guest-helper tests before submitting a pull request.
5. Never commit VM data, logs, credentials, local settings, downloaded installers, or generated signing material.

Security changes should fail closed, use typed contracts, preserve existing Multipass data, and avoid shell command construction. Preset updates must pin an exact version and integrity value and include a clean Ubuntu 24.04 verification note.

By participating, you agree to the [Code of Conduct](CODE_OF_CONDUCT.md).
