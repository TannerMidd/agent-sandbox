using System.Text.Json.Serialization;

namespace AgentSandbox.Domain;

public sealed record SandboxConfiguration(
    string InstanceName,
    ResourceProfile Resources,
    IReadOnlyList<string> SelectedPresetIds,
    bool ImportedLegacyInstance = false);

public sealed record AgentSandboxSettings
{
    public const int CurrentSchemaVersion = 1;
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public string InstanceName { get; init; } = "agent-sandbox";
    public bool ImportedLegacyInstance { get; init; }
    public IReadOnlyList<SandboxConfiguration> Sandboxes { get; init; } = [];
    public SetupState SetupState { get; init; } = SetupState.Welcome;
    public ResourceProfile Resources { get; init; } = new(4, 4, 50);
    public string? StoragePath { get; init; }
    public string Theme { get; init; } = "System";
    public bool ReducedMotion { get; init; }
    public bool AdvancedGuestBrowsing { get; init; }
    public bool CheckForUpdates { get; init; } = true;
    public DateTimeOffset? LastUpdateCheck { get; init; }
    public string? ReleaseRepository { get; init; }
    public IReadOnlyList<string> SelectedPresetIds { get; init; } = [];
    [JsonIgnore] public bool IsReady => SetupState == SetupState.Ready;
}

public sealed record PresetArtifact(
    string Platform,
    string Architecture,
    string Source,
    string Integrity,
    string InstallKind);

public sealed record AgentPresetManifest(
    int SchemaVersion,
    string Id,
    string DisplayName,
    string Version,
    string Executable,
    string MinimumRuntime,
    string AuthenticationHint,
    DateOnly LastVerified,
    IReadOnlyList<PresetArtifact> Artifacts);
