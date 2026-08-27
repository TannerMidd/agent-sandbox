using System.Text.Json.Serialization;

namespace AgentSandbox.Domain;

[JsonConverter(typeof(JsonStringEnumConverter<NetworkAccessPolicy>))]
public enum NetworkAccessPolicy
{
    Unrestricted,
    WebOnly,
    Offline
}

public sealed record SandboxHardeningOptions(
    string PresetId,
    bool AutomaticSecurityUpdates,
    bool KernelHardening,
    bool RestrictUnprivilegedFeatures,
    bool AuditSecurityEvents,
    NetworkAccessPolicy NetworkAccess,
    bool AllowAdministrativeTools)
{
    public static SandboxHardeningOptions Development => HardeningPresets.GetRequired(HardeningPresets.DevelopmentId).Options;

    public SandboxHardeningOptions Validate()
    {
        var namedPreset = HardeningPresets.All.SingleOrDefault(item => string.Equals(item.Id, PresetId, StringComparison.Ordinal));
        if (PresetId != HardeningPresets.CustomId && namedPreset is null)
            throw new ArgumentException($"Unknown hardening preset '{PresetId}'.", nameof(PresetId));
        if (namedPreset is not null && this != namedPreset.Options)
            throw new ArgumentException($"Hardening options labeled '{PresetId}' do not match that preset. Use the custom preset ID for modified options.", nameof(PresetId));
        if (!Enum.IsDefined(NetworkAccess))
            throw new ArgumentOutOfRangeException(nameof(NetworkAccess), "Unknown network access policy.");
        if (NetworkAccess == NetworkAccessPolicy.Offline && AutomaticSecurityUpdates)
            throw new ArgumentException("Automatic security updates require network access. Disable them for an offline sandbox.", nameof(AutomaticSecurityUpdates));
        return this;
    }

    public SandboxHardeningOptions AsCustom() => this with { PresetId = HardeningPresets.CustomId };
}

public sealed record HardeningPreset(
    string Id,
    string DisplayName,
    string Description,
    string CompatibilityNote,
    SandboxHardeningOptions Options,
    bool IsRecommended = false);

public static class HardeningPresets
{
    public const string DevelopmentId = "development";
    public const string BalancedId = "balanced";
    public const string RestrictedId = "restricted";
    public const string OfflineId = "offline";
    public const string CustomId = "custom";

    public static IReadOnlyList<HardeningPreset> All { get; } =
    [
        new(
            DevelopmentId,
            "Development compatibility",
            "Keeps unrestricted outbound access, passwordless administration, and non-root Docker access. Inbound traffic and SSH are still hardened.",
            "Best compatibility; an agent can obtain root-equivalent access inside the VM.",
            new SandboxHardeningOptions(DevelopmentId, false, false, false, false, NetworkAccessPolicy.Unrestricted, true)),
        new(
            BalancedId,
            "Balanced (recommended)",
            "Adds automatic updates, kernel safeguards, and security auditing while preserving normal development networking and administrative tools.",
            "Recommended for most agentic development workflows.",
            new SandboxHardeningOptions(BalancedId, true, true, false, true, NetworkAccessPolicy.Unrestricted, true),
            true),
        new(
            RestrictedId,
            "Restricted agent",
            "Limits outbound traffic to DNS, time synchronization, HTTP, and HTTPS; reduces kernel attack surface; and removes passwordless sudo and Docker-socket access.",
            "Some package registries, Git-over-SSH, containers, debuggers, and build tools may not work.",
            new SandboxHardeningOptions(RestrictedId, true, true, true, true, NetworkAccessPolicy.WebOnly, false)),
        new(
            OfflineId,
            "Offline / maximum isolation",
            "Blocks new outbound connections after provisioning, reduces kernel attack surface, audits security changes, and removes passwordless sudo and Docker-socket access.",
            "Agent sign-in, remote APIs, updates, package downloads, and selected agent presets are unavailable.",
            new SandboxHardeningOptions(OfflineId, false, true, true, true, NetworkAccessPolicy.Offline, false))
    ];

    public static HardeningPreset GetRequired(string id) =>
        All.SingleOrDefault(item => string.Equals(item.Id, id, StringComparison.Ordinal))
        ?? throw new ArgumentException($"Unknown hardening preset '{id}'.", nameof(id));

    public static string Describe(SandboxHardeningOptions options)
    {
        options.Validate();
        var preset = All.SingleOrDefault(item => string.Equals(item.Id, options.PresetId, StringComparison.Ordinal));
        return preset?.DisplayName ?? "Custom hardening";
    }
}
