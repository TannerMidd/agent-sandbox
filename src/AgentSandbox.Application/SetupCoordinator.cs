using AgentSandbox.Domain;

namespace AgentSandbox.Application;

public sealed class SetupCoordinator(
    ISettingsStore settingsStore,
    IHostPrerequisiteService prerequisites,
    IMultipassService multipass,
    IPresetService presets,
    string cloudInitPath = "cloud-init.yaml") : ISandboxLifecycleService
{
    public async Task<AgentSandboxSettings> ResumeSetupAsync(CancellationToken cancellationToken = default)
    {
        var settings = await settingsStore.LoadAsync(cancellationToken);
        if (settings.ImportedLegacyInstance && string.Equals(settings.InstanceName, "agent-dev", StringComparison.Ordinal))
        {
            var imported = await multipass.GetSandboxAsync(settings.InstanceName, cancellationToken);
            if (imported is not null && imported.State != SandboxState.Failed)
            {
                var recovered = settings with { SetupState = SetupState.Ready, Resources = imported.Resources };
                if (settings != recovered) await settingsStore.SaveAsync(recovered, cancellationToken);
                return recovered;
            }
        }
        var host = await prerequisites.InspectAsync(cancellationToken);
        var nextState = DetermineNextState(host, settings);
        if (settings.SetupState != nextState)
        {
            settings = settings with { SetupState = nextState };
            await settingsStore.SaveAsync(settings, cancellationToken);
        }
        return settings;
    }

    public async Task<LegacyImportCandidate?> InspectLegacyImportAsync(CancellationToken cancellationToken = default)
    {
        var legacy = await multipass.GetSandboxAsync("agent-dev", cancellationToken);
        if (legacy is null) return null;
        var snapshots = await multipass.ListSnapshotsAsync(legacy.InstanceName, cancellationToken);
        return new LegacyImportCandidate(
            legacy.InstanceName,
            legacy.State,
            null,
            snapshots.Select(snapshot => snapshot.Name).ToArray(),
            []);
    }

    public async Task<AgentSandboxSettings> ImportLegacyAsync(LegacyImportCandidate candidate, CancellationToken cancellationToken = default)
    {
        if (!string.Equals(candidate.InstanceName, "agent-dev", StringComparison.Ordinal))
            throw new InvalidOperationException("Only the exact legacy instance 'agent-dev' can be imported.");

        var current = await multipass.GetSandboxAsync(candidate.InstanceName, cancellationToken)
            ?? throw new InvalidOperationException("The legacy instance no longer exists.");
        if (current.State == SandboxState.Failed)
            throw new InvalidOperationException("The legacy instance must be repaired before import.");

        var settings = (await settingsStore.LoadAsync(cancellationToken)) with
        {
            InstanceName = candidate.InstanceName,
            ImportedLegacyInstance = true,
            Resources = current.Resources,
            SetupState = SetupState.Ready
        };
        await settingsStore.SaveAsync(settings, cancellationToken);
        return settings;
    }

    public async Task<OperationProgress> ProvisionAsync(
        ResourceProfile resources,
        IReadOnlyList<string> presetIds,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var settings = await settingsStore.LoadAsync(cancellationToken);
        var host = await prerequisites.InspectAsync(cancellationToken);
        var validationErrors = resources.Validate(
            Environment.ProcessorCount,
            host.TotalMemoryBytes,
            GetFreeDiskBytes(cloudInitPath));
        if (validationErrors.Count > 0)
            throw new InvalidOperationException(string.Join(Environment.NewLine, validationErrors));

        var request = new ProvisionRequest(settings.InstanceName, "24.04", resources, cloudInitPath, "clean");
        settings = settings with { Resources = resources, SelectedPresetIds = presetIds, SetupState = SetupState.Provisioning };
        await settingsStore.SaveAsync(settings, cancellationToken);

        var provisionResult = await multipass.ProvisionAsync(request, progress, cancellationToken);
        if (provisionResult.State != OperationState.Succeeded)
        {
            await settingsStore.SaveAsync(settings with { SetupState = SetupState.NeedsReview }, cancellationToken);
            return provisionResult;
        }

        if (presetIds.Count > 0)
        {
            settings = settings with { SetupState = SetupState.InstallingPresets };
            await settingsStore.SaveAsync(settings, cancellationToken);
            var presetResult = await presets.InstallAsync(settings.InstanceName, presetIds, progress, cancellationToken);
            if (presetResult.State != OperationState.Succeeded)
            {
                await settingsStore.SaveAsync(settings with { SetupState = SetupState.NeedsReview }, cancellationToken);
                return presetResult;
            }
        }

        settings = settings with { SetupState = SetupState.Ready };
        await settingsStore.SaveAsync(settings, cancellationToken);
        return provisionResult with { Title = "Agent Sandbox is ready", UpdatedAt = DateTimeOffset.UtcNow };
    }

    public static SetupState DetermineNextState(HostReadiness host, AgentSandboxSettings settings)
    {
        if (!host.IsWindows11 || !host.IsSupportedEdition || !host.IsX64 || !host.HasVirtualization)
            return SetupState.NeedsReview;
        if (host.IsRebootPending) return SetupState.RebootRequired;
        if (!host.IsHyperVEnabled) return SetupState.HyperVRequired;
        if (!host.IsMultipassInstalled) return SetupState.MultipassRequired;
        if (!host.IsMultipassCompatible) return SetupState.NeedsReview;
        if (settings.IsReady) return SetupState.Ready;
        return settings.SetupState is SetupState.Welcome or SetupState.CheckingHost or SetupState.HyperVRequired or
            SetupState.RebootRequired or SetupState.MultipassRequired or SetupState.StorageRequired or SetupState.NeedsReview
            ? SetupState.ResourceConfiguration
            : settings.SetupState;
    }

    private static long GetFreeDiskBytes(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath) ?? throw new InvalidOperationException("The cloud-init path has no volume root.");
        return new DriveInfo(root).AvailableFreeSpace;
    }
}
