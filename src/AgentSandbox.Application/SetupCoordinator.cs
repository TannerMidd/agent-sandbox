using System.Text.RegularExpressions;
using AgentSandbox.Domain;

namespace AgentSandbox.Application;

public sealed class SetupCoordinator(
    ISettingsStore settingsStore,
    IHostPrerequisiteService prerequisites,
    IMultipassService multipass,
    IPresetService presets,
    string cloudInitPath = "cloud-init.yaml",
    Func<string, long>? freeDiskBytes = null) : ISandboxLifecycleService
{
    private static readonly Regex InstanceNamePattern = new("^[A-Za-z](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?$", RegexOptions.CultureInvariant);

    public async Task<AgentSandboxSettings> ResumeSetupAsync(CancellationToken cancellationToken = default)
    {
        var settings = await NormalizeAsync(await settingsStore.LoadAsync(cancellationToken), cancellationToken);
        if (settings.ImportedLegacyInstance && string.Equals(settings.InstanceName, "agent-dev", StringComparison.Ordinal))
        {
            var imported = await multipass.GetSandboxAsync(settings.InstanceName, cancellationToken);
            if (imported is not null && imported.State != SandboxState.Failed)
            {
                var configuration = settings.Sandboxes.Single(item => item.InstanceName == settings.InstanceName) with { Resources = imported.Resources };
                var recovered = UpdateActive(settings with
                {
                    Sandboxes = settings.Sandboxes.Select(item => item.InstanceName == configuration.InstanceName ? configuration : item).ToArray()
                }, configuration) with { SetupState = SetupState.Ready };
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

        var settings = await NormalizeAsync(await settingsStore.LoadAsync(cancellationToken), cancellationToken);
        if (settings.Sandboxes.Count > 0 && settings.SetupState == SetupState.NeedsReview)
            throw new InvalidOperationException($"Resolve or delete sandbox '{settings.InstanceName}' before importing another VM.");
        if (settings.Sandboxes.Any(item => item.InstanceName == candidate.InstanceName))
            throw new InvalidOperationException("The legacy instance is already managed.");
        var configuration = new SandboxConfiguration(candidate.InstanceName, current.Resources, [], true, Hardening: SandboxHardeningOptions.Development);
        settings = UpdateActive(settings with { Sandboxes = [.. settings.Sandboxes, configuration] }, configuration) with { SetupState = SetupState.Ready };
        await settingsStore.SaveAsync(settings, cancellationToken);
        return settings;
    }

    public async Task<AgentSandboxSettings> SelectSandboxAsync(string instanceName, CancellationToken cancellationToken = default)
    {
        ValidateInstanceName(instanceName);
        var settings = await NormalizeAsync(await settingsStore.LoadAsync(cancellationToken), cancellationToken);
        if (settings.SetupState == SetupState.NeedsReview && !string.Equals(settings.InstanceName, instanceName, StringComparison.Ordinal))
            throw new InvalidOperationException($"Resolve or delete sandbox '{settings.InstanceName}' before switching VMs.");
        var configuration = settings.Sandboxes.SingleOrDefault(item => string.Equals(item.InstanceName, instanceName, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"Sandbox '{instanceName}' is not managed by Agent Sandbox.");
        settings = UpdateActive(settings, configuration) with { SetupState = settings.SetupState == SetupState.NeedsReview ? SetupState.NeedsReview : SetupState.Ready };
        await settingsStore.SaveAsync(settings, cancellationToken);
        return settings;
    }

    public Task<OperationProgress> ProvisionAsync(
        string instanceName,
        ResourceProfile resources,
        IReadOnlyList<string> presetIds,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        ProvisionAsync(instanceName, LinuxImages.DefaultId, resources, presetIds, progress, cancellationToken);

    public Task<OperationProgress> ProvisionAsync(
        string instanceName,
        string imageId,
        ResourceProfile resources,
        IReadOnlyList<string> presetIds,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        ProvisionAsync(instanceName, imageId, null, resources, presetIds, progress, cancellationToken);

    public Task<OperationProgress> ProvisionAsync(
        string instanceName,
        string imageId,
        string? customImageUrl,
        ResourceProfile resources,
        IReadOnlyList<string> presetIds,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        ProvisionAsync(instanceName, imageId, customImageUrl, resources, presetIds, SandboxHardeningOptions.Development, progress, cancellationToken);

    public async Task<OperationProgress> ProvisionAsync(
        string instanceName,
        string imageId,
        string? customImageUrl,
        ResourceProfile resources,
        IReadOnlyList<string> presetIds,
        SandboxHardeningOptions hardening,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ValidateInstanceName(instanceName);
        ArgumentNullException.ThrowIfNull(hardening);
        hardening.Validate();
        if (hardening.NetworkAccess == NetworkAccessPolicy.Offline && presetIds.Count > 0)
            throw new InvalidOperationException("Offline hardening cannot be combined with agent presets because preset installation requires registry access.");
        var image = LinuxImages.GetRequired(imageId);
        var imageReference = LinuxImages.ResolveReference(imageId, customImageUrl);
        var settings = await NormalizeAsync(await settingsStore.LoadAsync(cancellationToken), cancellationToken);
        if (settings.Sandboxes.Count > 0 && settings.SetupState == SetupState.NeedsReview)
            throw new InvalidOperationException($"Resolve or delete sandbox '{settings.InstanceName}' before creating another VM.");
        if (settings.Sandboxes.Any(item => string.Equals(item.InstanceName, instanceName, StringComparison.Ordinal)))
            throw new InvalidOperationException($"Sandbox '{instanceName}' is already managed.");

        var host = await prerequisites.InspectAsync(cancellationToken);
        var validationErrors = resources.Validate(
            Environment.ProcessorCount,
            host.TotalMemoryBytes,
            freeDiskBytes?.Invoke(host.MultipassStoragePath ?? cloudInitPath) ?? GetFreeDiskBytes(host.MultipassStoragePath ?? cloudInitPath),
            image.MinimumResources).ToList();
        validationErrors.AddRange(ValidateAggregateResources(settings.Sandboxes, resources, Environment.ProcessorCount, host.TotalMemoryBytes));
        if (validationErrors.Count > 0)
            throw new InvalidOperationException(string.Join(Environment.NewLine, validationErrors));

        var initialProvision = settings.Sandboxes.Count == 0;
        var previousSettings = settings;
        var request = new ProvisionRequest(instanceName, imageReference, resources, cloudInitPath, "clean", image.IsUserSupplied, hardening);
        var configuration = new SandboxConfiguration(instanceName, resources, [], ImageId: image.Id, CustomImageUrl: image.IsUserSupplied ? imageReference : null, Hardening: hardening, PendingPresetIds: presetIds.ToArray(), ProvisioningComplete: false);
        settings = UpdateActive(settings with { Sandboxes = [.. settings.Sandboxes, configuration] }, configuration) with
        {
            SetupState = SetupState.Provisioning
        };
        await settingsStore.SaveAsync(settings, cancellationToken);

        var provisionResult = await multipass.ProvisionAsync(request, progress, cancellationToken);
        if (provisionResult.State != OperationState.Succeeded)
        {
            if (provisionResult.State == OperationState.CleanupPending)
            {
                settings = settings with { SetupState = SetupState.NeedsReview };
            }
            else if (initialProvision)
            {
                settings = settings with { Sandboxes = [], SetupState = SetupState.NeedsReview };
            }
            else
            {
                settings = previousSettings with { SetupState = SetupState.Ready };
            }
            await settingsStore.SaveAsync(settings, cancellationToken);
            return provisionResult;
        }

        configuration = configuration with { ProvisioningComplete = true };
        settings = UpdateActive(settings with
        {
            Sandboxes = settings.Sandboxes.Select(item => item.InstanceName == instanceName ? configuration : item).ToArray()
        }, configuration) with
        {
            SetupState = presetIds.Count > 0 ? SetupState.InstallingPresets : SetupState.Ready
        };
        await settingsStore.SaveAsync(settings, cancellationToken);
        if (presetIds.Count > 0)
        {
            OperationProgress presetResult;
            try
            {
                presetResult = await presets.InstallAsync(instanceName, presetIds, progress, cancellationToken);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                presetResult = new OperationProgress(
                    Guid.NewGuid(), "Install agent presets",
                    exception is OperationCanceledException ? OperationState.Canceled : OperationState.Failed,
                    "Preset installation failed", null, null, null, "PRESET_INSTALL_FAILED", exception.Message, DateTimeOffset.UtcNow);
            }
            if (presetResult.State != OperationState.Succeeded)
            {
                await settingsStore.SaveAsync(settings with { SetupState = SetupState.NeedsReview }, cancellationToken);
                return presetResult;
            }
            configuration = configuration with { SelectedPresetIds = presetIds.ToArray(), PendingPresetIds = [] };
            settings = UpdateActive(settings with
            {
                Sandboxes = settings.Sandboxes.Select(item => item.InstanceName == instanceName ? configuration : item).ToArray()
            }, configuration) with { SetupState = SetupState.Ready };
            await settingsStore.SaveAsync(settings, cancellationToken);
        }

        return provisionResult with { Title = $"{instanceName} is ready", UpdatedAt = DateTimeOffset.UtcNow };
    }

    public async Task<OperationProgress> RetryPendingPresetsAsync(
        string instanceName,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ValidateInstanceName(instanceName);
        var settings = await NormalizeAsync(await settingsStore.LoadAsync(cancellationToken), cancellationToken);
        var configuration = settings.Sandboxes.SingleOrDefault(item => string.Equals(item.InstanceName, instanceName, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"Sandbox '{instanceName}' is not managed by Agent Sandbox.");
        var pending = configuration.PendingPresetIds ?? [];
        if (!configuration.ProvisioningComplete)
            throw new InvalidOperationException("Provisioning did not complete; rebuild the preserved partial VM after reviewing diagnostics.");
        if (pending.Count == 0) throw new InvalidOperationException("This sandbox has no pending preset installation to retry.");
        var sandbox = await multipass.GetSandboxAsync(instanceName, cancellationToken)
            ?? throw new InvalidOperationException($"Exact sandbox '{instanceName}' no longer exists.");
        if (sandbox.State != SandboxState.Running)
        {
            var started = await multipass.StartAsync(instanceName, progress, cancellationToken);
            if (started.State != OperationState.Succeeded) return started;
        }
        settings = UpdateActive(settings, configuration) with { SetupState = SetupState.InstallingPresets };
        await settingsStore.SaveAsync(settings, cancellationToken);
        OperationProgress result;
        try
        {
            result = await presets.InstallAsync(instanceName, pending, progress, cancellationToken);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            result = new OperationProgress(
                Guid.NewGuid(), "Install agent presets",
                exception is OperationCanceledException ? OperationState.Canceled : OperationState.Failed,
                "Preset installation failed", null, null, null, "PRESET_INSTALL_FAILED", exception.Message, DateTimeOffset.UtcNow);
        }
        if (result.State == OperationState.Succeeded)
        {
            configuration = configuration with
            {
                SelectedPresetIds = configuration.SelectedPresetIds.Concat(pending).Distinct(StringComparer.Ordinal).ToArray(),
                PendingPresetIds = []
            };
            settings = UpdateActive(settings with
            {
                Sandboxes = settings.Sandboxes.Select(item => item.InstanceName == instanceName ? configuration : item).ToArray()
            }, configuration) with { SetupState = SetupState.Ready };
        }
        else
        {
            settings = settings with { SetupState = SetupState.NeedsReview };
        }
        await settingsStore.SaveAsync(settings, cancellationToken);
        return result;
    }

    public async Task<OperationProgress> DeleteSandboxAsync(string instanceName, CancellationToken cancellationToken = default)
    {
        ValidateInstanceName(instanceName);
        var settings = await NormalizeAsync(await settingsStore.LoadAsync(cancellationToken), cancellationToken);
        if (!settings.Sandboxes.Any(item => string.Equals(item.InstanceName, instanceName, StringComparison.Ordinal)))
            throw new InvalidOperationException($"Sandbox '{instanceName}' is not managed by Agent Sandbox.");
        if (settings.SetupState == SetupState.NeedsReview && !string.Equals(settings.InstanceName, instanceName, StringComparison.Ordinal))
            throw new InvalidOperationException($"Resolve or delete sandbox '{settings.InstanceName}' before deleting another VM.");

        var result = await multipass.GetSandboxAsync(instanceName, cancellationToken) is null
            ? new OperationProgress(Guid.NewGuid(), "Remove sandbox", OperationState.Succeeded, "Missing VM registration removed", 100, null, null, null, null, DateTimeOffset.UtcNow)
            : await multipass.DeleteAsync(instanceName, purge: true, cancellationToken: cancellationToken);
        var remaining = settings.Sandboxes.Where(item => !string.Equals(item.InstanceName, instanceName, StringComparison.Ordinal)).ToArray();
        settings = settings with { Sandboxes = remaining };
        settings = remaining.Length == 0
            ? settings with
            {
                InstanceName = "agent-sandbox",
                ImportedLegacyInstance = false,
                Resources = new ResourceProfile(4, 4, 50),
                ImageId = LinuxImages.DefaultId,
                CustomImageUrl = null,
                Hardening = SandboxHardeningOptions.Development,
                SelectedPresetIds = [],
                SetupState = SetupState.ResourceConfiguration
            }
            : UpdateActive(settings, remaining.FirstOrDefault(item => item.InstanceName == settings.InstanceName) ?? remaining[0]) with { SetupState = SetupState.Ready };
        await settingsStore.SaveAsync(settings, cancellationToken);
        return result;
    }

    public static SetupState DetermineNextState(HostReadiness host, AgentSandboxSettings settings)
    {
        if (!host.IsWindows11 || !host.IsSupportedEdition || !host.IsX64 || !host.HasVirtualization)
            return SetupState.NeedsReview;
        if (host.IsRebootPending) return SetupState.RebootRequired;
        if (!host.IsHyperVEnabled) return SetupState.HyperVRequired;
        if (!host.IsMultipassInstalled) return SetupState.MultipassRequired;
        if (!host.IsMultipassCompatible) return SetupState.NeedsReview;
        if (settings.Sandboxes.Count > 0 && settings.SetupState == SetupState.NeedsReview) return SetupState.NeedsReview;
        if (settings.IsReady) return SetupState.Ready;
        return settings.SetupState is SetupState.Welcome or SetupState.CheckingHost or SetupState.HyperVRequired or
            SetupState.RebootRequired or SetupState.MultipassRequired or SetupState.StorageRequired or SetupState.NeedsReview
            ? SetupState.ResourceConfiguration
            : settings.SetupState;
    }

    public static IReadOnlyList<string> ValidateAggregateResources(
        IReadOnlyList<SandboxConfiguration> existing,
        ResourceProfile requested,
        int logicalProcessors,
        long totalMemoryBytes)
    {
        var errors = new List<string>();
        if (existing.Sum(item => item.Resources.CpuCount) + requested.CpuCount > Math.Max(2, logicalProcessors - 2))
            errors.Add("Combined sandbox CPUs must leave at least two logical processors for Windows.");
        var totalMemoryGiB = (int)(totalMemoryBytes / 1_073_741_824L);
        if (existing.Sum(item => item.Resources.MemoryGiB) + requested.MemoryGiB > Math.Max(4, totalMemoryGiB - 6))
            errors.Add("Combined sandbox memory must leave at least 6 GiB for Windows.");
        return errors;
    }

    private async Task<AgentSandboxSettings> NormalizeAsync(AgentSandboxSettings settings, CancellationToken cancellationToken)
    {
        _ = LinuxImages.ResolveReference(settings.ImageId, settings.CustomImageUrl);
        ArgumentNullException.ThrowIfNull(settings.Hardening);
        settings.Hardening.Validate();
        if (settings.Sandboxes.Any(configuration => configuration.Hardening is null))
        {
            settings = settings with
            {
                Sandboxes = settings.Sandboxes.Select(configuration => configuration.Hardening is null
                    ? configuration with { Hardening = SandboxHardeningOptions.Development }
                    : configuration).ToArray()
            };
            await settingsStore.SaveAsync(settings, cancellationToken);
        }
        foreach (var configuration in settings.Sandboxes)
        {
            _ = LinuxImages.ResolveReference(configuration.ImageId, configuration.CustomImageUrl);
            configuration.Hardening!.Validate();
        }
        var interrupted = settings.SetupState is SetupState.Provisioning or SetupState.InstallingPresets;
        if (settings.Sandboxes.Count > 0 && interrupted)
        {
            settings = settings with { SetupState = SetupState.NeedsReview };
            await settingsStore.SaveAsync(settings, cancellationToken);
        }
        else if (settings.Sandboxes.Count == 0)
        {
            var existing = interrupted ? await multipass.GetSandboxAsync(settings.InstanceName, cancellationToken) : null;
            if (settings.IsReady || settings.ImportedLegacyInstance || existing is not null)
            {
                var configuration = new SandboxConfiguration(
                    settings.InstanceName,
                    existing?.Resources ?? settings.Resources,
                    interrupted ? [] : settings.SelectedPresetIds,
                    settings.ImportedLegacyInstance,
                    settings.ImageId,
                    settings.CustomImageUrl,
                    settings.Hardening);
                settings = UpdateActive(settings with { Sandboxes = [configuration] }, configuration) with
                {
                    SetupState = interrupted ? SetupState.NeedsReview : settings.SetupState
                };
                await settingsStore.SaveAsync(settings, cancellationToken);
            }
            else if (interrupted)
            {
                settings = settings with { SetupState = SetupState.ResourceConfiguration };
                await settingsStore.SaveAsync(settings, cancellationToken);
            }
        }
        else if (!settings.Sandboxes.Any(item => item.InstanceName == settings.InstanceName))
        {
            settings = UpdateActive(settings, settings.Sandboxes[0]);
            await settingsStore.SaveAsync(settings, cancellationToken);
        }
        return settings;
    }

    private static AgentSandboxSettings UpdateActive(AgentSandboxSettings settings, SandboxConfiguration configuration) => settings with
    {
        InstanceName = configuration.InstanceName,
        ImportedLegacyInstance = configuration.ImportedLegacyInstance,
        Resources = configuration.Resources,
        ImageId = configuration.ImageId,
        CustomImageUrl = configuration.CustomImageUrl,
        Hardening = configuration.Hardening ?? SandboxHardeningOptions.Development,
        SelectedPresetIds = configuration.SelectedPresetIds
    };

    private static void ValidateInstanceName(string value)
    {
        if (!InstanceNamePattern.IsMatch(value))
            throw new ArgumentException("VM names must start with a letter and contain only letters, numbers, and hyphens (maximum 63 characters).", nameof(value));
        if (string.Equals(value, "primary", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The Multipass name 'primary' is reserved.", nameof(value));
    }

    private static long GetFreeDiskBytes(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath) ?? throw new InvalidOperationException("The cloud-init path has no volume root.");
        return new DriveInfo(root).AvailableFreeSpace;
    }
}
