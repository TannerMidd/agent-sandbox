using AgentSandbox.Application;
using AgentSandbox.Domain;

namespace AgentSandbox.Application.Tests;

public sealed class SetupCoordinatorTests
{
    [Fact]
    public void SetupRequiresHyperVBeforeMultipass()
    {
        var host = ReadyHost() with { IsHyperVEnabled = false, IsMultipassInstalled = false };
        Assert.Equal(SetupState.HyperVRequired, SetupCoordinator.DetermineNextState(host, new AgentSandboxSettings()));
    }

    [Fact]
    public void PendingRestartTakesPrecedence()
    {
        var host = ReadyHost() with { IsRebootPending = true, IsHyperVEnabled = false };
        Assert.Equal(SetupState.RebootRequired, SetupCoordinator.DetermineNextState(host, new AgentSandboxSettings()));
    }

    [Fact]
    public void ReadyStateIsResumed()
    {
        Assert.Equal(SetupState.Ready, SetupCoordinator.DetermineNextState(ReadyHost(), new AgentSandboxSettings { SetupState = SetupState.Ready }));
    }

    [Theory]
    [InlineData(SetupState.HyperVRequired)]
    [InlineData(SetupState.RebootRequired)]
    [InlineData(SetupState.MultipassRequired)]
    [InlineData(SetupState.StorageRequired)]
    [InlineData(SetupState.NeedsReview)]
    public void CompletedHostChecksAdvanceToResourceConfiguration(SetupState prior)
    {
        Assert.Equal(SetupState.ResourceConfiguration, SetupCoordinator.DetermineNextState(ReadyHost(), new AgentSandboxSettings { SetupState = prior }));
    }

    [Fact]
    public async Task LegacyImportRejectsAnyNonExactInstance()
    {
        var coordinator = new SetupCoordinator(new MemorySettings(), new FakePrerequisites(), new FakeMultipass(), new FakePresets());
        var candidate = new LegacyImportCandidate("Agent-Dev", SandboxState.Stopped, null, [], []);
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.ImportLegacyAsync(candidate));
    }

    [Fact]
    public async Task LegacyImportAssociatesExactInstanceWithoutProvisioning()
    {
        var store = new MemorySettings();
        var sandbox = new SandboxInfo("agent-dev", SandboxState.Running, new ResourceProfile(4, 4, 50), "10.0.0.2", "24.04", DateTimeOffset.UtcNow);
        var coordinator = new SetupCoordinator(store, new FakePrerequisites(), new FakeMultipass(sandbox), new FakePresets());

        var imported = await coordinator.ImportLegacyAsync(new LegacyImportCandidate("agent-dev", SandboxState.Running, null, ["clean"], []));

        Assert.Equal("agent-dev", imported.InstanceName);
        Assert.True(imported.ImportedLegacyInstance);
        Assert.Equal(SetupState.Ready, imported.SetupState);
        Assert.Equal(sandbox.Resources, imported.Resources);
    }

    [Fact]
    public async Task ImportedLegacyInstanceRecoversReadyStateFromStaleHostReview()
    {
        var sandbox = new SandboxInfo("agent-dev", SandboxState.Running, new ResourceProfile(6, 8, 60), "10.0.0.2", "24.04", DateTimeOffset.UtcNow);
        var initial = new AgentSandboxSettings { InstanceName = "agent-dev", ImportedLegacyInstance = true, SetupState = SetupState.MultipassRequired };
        var store = new MemorySettings(initial);
        var incompatibleHost = ReadyHost() with { IsMultipassInstalled = false, IsMultipassCompatible = false };
        var coordinator = new SetupCoordinator(store, new FakePrerequisites(incompatibleHost), new FakeMultipass(sandbox), new FakePresets());

        var recovered = await coordinator.ResumeSetupAsync();

        Assert.Equal(SetupState.Ready, recovered.SetupState);
        Assert.Equal(sandbox.Resources, recovered.Resources);
        Assert.Equal(sandbox.Resources, Assert.Single(recovered.Sandboxes).Resources);
    }

    [Fact]
    public async Task ReadyV1SettingsAreMigratedToManagedSandboxes()
    {
        var initial = new AgentSandboxSettings { InstanceName = "agent-sandbox-old", SetupState = SetupState.Ready };
        var coordinator = new SetupCoordinator(new MemorySettings(initial), new FakePrerequisites(), new FakeMultipass(), new FakePresets());

        var migrated = await coordinator.ResumeSetupAsync();

        Assert.Equal("agent-sandbox-old", Assert.Single(migrated.Sandboxes).InstanceName);
    }

    [Theory]
    [InlineData(SetupState.Provisioning)]
    [InlineData(SetupState.InstallingPresets)]
    public async Task InterruptedV1SettingsWithAnExistingVmRequireReview(SetupState interruptedState)
    {
        var resources = new ResourceProfile(2, 4, 30);
        var sandbox = new SandboxInfo("agent-sandbox-old", SandboxState.Running, resources, "10.0.0.2", "24.04", DateTimeOffset.UtcNow);
        var initial = new AgentSandboxSettings { InstanceName = sandbox.InstanceName, SetupState = interruptedState, Resources = resources, SelectedPresetIds = ["codex"] };
        var coordinator = new SetupCoordinator(new MemorySettings(initial), new FakePrerequisites(), new FakeMultipass(sandbox), new FakePresets());

        var migrated = await coordinator.ResumeSetupAsync();

        Assert.Equal(SetupState.NeedsReview, migrated.SetupState);
        Assert.Empty(Assert.Single(migrated.Sandboxes).SelectedPresetIds);
    }

    [Theory]
    [InlineData(SetupState.Provisioning)]
    [InlineData(SetupState.InstallingPresets)]
    public async Task InterruptedV1SettingsWithoutAVmReturnToConfiguration(SetupState interruptedState)
    {
        var initial = new AgentSandboxSettings { InstanceName = "agent-sandbox-old", SetupState = interruptedState };
        var coordinator = new SetupCoordinator(new MemorySettings(initial), new FakePrerequisites(), new FakeMultipass(), new FakePresets());

        var migrated = await coordinator.ResumeSetupAsync();

        Assert.Equal(SetupState.ResourceConfiguration, migrated.SetupState);
        Assert.Empty(migrated.Sandboxes);
    }

    [Fact]
    public async Task InterruptedPresetInstallWithARegistrationRequiresReview()
    {
        var resources = new ResourceProfile(2, 4, 30);
        var initial = new AgentSandboxSettings
        {
            InstanceName = "agent-sandbox-preset",
            SetupState = SetupState.InstallingPresets,
            Resources = resources,
            Sandboxes = [new SandboxConfiguration("agent-sandbox-preset", resources, [])]
        };
        var coordinator = new SetupCoordinator(new MemorySettings(initial), new FakePrerequisites(), new FakeMultipass(), new FakePresets());

        var recovered = await coordinator.ResumeSetupAsync();

        Assert.Equal(SetupState.NeedsReview, recovered.SetupState);
    }

    [Fact]
    public void AggregateResourcesPreserveWindowsCapacity()
    {
        var existing = new[] { new SandboxConfiguration("agent-sandbox-one", new ResourceProfile(4, 8, 30), []) };

        var errors = SetupCoordinator.ValidateAggregateResources(existing, new ResourceProfile(4, 8, 30), 8, 20L << 30);

        Assert.Equal(2, errors.Count);
    }

    [Fact]
    public async Task SelectedLinuxImageIsProvisionedAndPersisted()
    {
        var store = new MemorySettings();
        var multipass = new FakeMultipass();
        var coordinator = new SetupCoordinator(store, new FakePrerequisites(), multipass, new FakePresets(), freeDiskBytes: _ => 100L << 30);

        var result = await coordinator.ProvisionAsync("agent-sandbox-alpine", "alpine-3.22", new ResourceProfile(1, 1, 10), []);
        var settings = await store.LoadAsync();

        Assert.Equal(OperationState.Succeeded, result.State);
        Assert.Equal(LinuxImages.GetRequired("alpine-3.22").ImageReference, multipass.LastProvisionRequest?.Image);
        Assert.Equal("alpine-3.22", settings.ImageId);
        Assert.Equal("alpine-3.22", Assert.Single(settings.Sandboxes).ImageId);
    }

    [Fact]
    public async Task CustomCloudImageIsValidatedAndPersisted()
    {
        const string imageUrl = "https://images.example.org/team/cloud-image.qcow2";
        var store = new MemorySettings();
        var multipass = new FakeMultipass();
        var coordinator = new SetupCoordinator(store, new FakePrerequisites(), multipass, new FakePresets(), freeDiskBytes: _ => 100L << 30);

        await coordinator.ProvisionAsync("agent-sandbox-custom", LinuxImages.CustomId, imageUrl, new ResourceProfile(2, 2, 20), []);
        var settings = await store.LoadAsync();

        Assert.True(multipass.LastProvisionRequest?.IsUserSuppliedImage);
        Assert.Equal(imageUrl, settings.CustomImageUrl);
        Assert.Equal(imageUrl, Assert.Single(settings.Sandboxes).CustomImageUrl);
    }

    [Fact]
    public async Task UnknownLinuxImageIsRejectedBeforeProvisioning()
    {
        var coordinator = new SetupCoordinator(new MemorySettings(), new FakePrerequisites(), new FakeMultipass(), new FakePresets(), freeDiskBytes: _ => 100L << 30);
        await Assert.ThrowsAsync<ArgumentException>(() => coordinator.ProvisionAsync("agent-sandbox-unknown", "unknown", new ResourceProfile(2, 4, 30), []));
    }

    [Fact]
    public async Task PartialProvisioningIsManagedButRequiresReview()
    {
        var store = new MemorySettings();
        var multipass = new FakeMultipass { ProvisionResult = Result(OperationState.CleanupPending, "Partial") };
        var coordinator = new SetupCoordinator(store, new FakePrerequisites(), multipass, new FakePresets(), freeDiskBytes: _ => 100L << 30);

        var result = await coordinator.ProvisionAsync("agent-sandbox-partial", new ResourceProfile(2, 4, 30), []);
        var settings = await store.LoadAsync();

        Assert.Equal(OperationState.CleanupPending, result.State);
        Assert.Equal(SetupState.NeedsReview, settings.SetupState);
        Assert.Equal("agent-sandbox-partial", Assert.Single(settings.Sandboxes).InstanceName);
    }

    [Fact]
    public async Task FailedPresetIsNotMarkedInstalled()
    {
        var store = new MemorySettings();
        var presets = new FakePresets(Result(OperationState.Failed, "Preset failed"));
        var coordinator = new SetupCoordinator(store, new FakePrerequisites(), new FakeMultipass(), presets, freeDiskBytes: _ => 100L << 30);

        var result = await coordinator.ProvisionAsync("agent-sandbox-preset", new ResourceProfile(2, 4, 30), ["codex"]);
        var settings = await store.LoadAsync();

        Assert.Equal(OperationState.Failed, result.State);
        Assert.Equal(SetupState.NeedsReview, settings.SetupState);
        Assert.Empty(Assert.Single(settings.Sandboxes).SelectedPresetIds);
    }

    [Fact]
    public async Task ManagedSandboxesCanBeSelectedAndDeletedIndependently()
    {
        var resources = new ResourceProfile(2, 4, 30);
        var first = new SandboxInfo("agent-sandbox-one", SandboxState.Running, resources, "10.0.0.2", "24.04", DateTimeOffset.UtcNow);
        var second = first with { InstanceName = "agent-sandbox-two", IPv4Address = "10.0.0.3" };
        var profiles = new[]
        {
            new SandboxConfiguration(first.InstanceName, resources, []),
            new SandboxConfiguration(second.InstanceName, resources, ["codex"])
        };
        var store = new MemorySettings(new AgentSandboxSettings
        {
            InstanceName = first.InstanceName,
            SetupState = SetupState.Ready,
            Resources = resources,
            Sandboxes = profiles
        });
        var coordinator = new SetupCoordinator(store, new FakePrerequisites(), new FakeMultipass(first, second), new FakePresets());

        var selected = await coordinator.SelectSandboxAsync(second.InstanceName);
        await coordinator.DeleteSandboxAsync(second.InstanceName);
        var remaining = await store.LoadAsync();

        Assert.Equal(second.InstanceName, selected.InstanceName);
        Assert.Equal(new[] { "codex" }, selected.SelectedPresetIds);
        Assert.Equal(first.InstanceName, remaining.InstanceName);
        Assert.Equal(first.InstanceName, Assert.Single(remaining.Sandboxes).InstanceName);
    }

    [Fact]
    public async Task ReviewTargetMustBeResolvedBeforeManagingAnotherVm()
    {
        var resources = new ResourceProfile(2, 4, 30);
        var first = new SandboxInfo("agent-sandbox-one", SandboxState.Running, resources, "10.0.0.2", "24.04", DateTimeOffset.UtcNow);
        var second = first with { InstanceName = "agent-sandbox-two" };
        var store = new MemorySettings(new AgentSandboxSettings
        {
            InstanceName = second.InstanceName,
            SetupState = SetupState.NeedsReview,
            Resources = resources,
            Sandboxes =
            [
                new SandboxConfiguration(first.InstanceName, resources, []),
                new SandboxConfiguration(second.InstanceName, resources, [])
            ]
        });
        var coordinator = new SetupCoordinator(store, new FakePrerequisites(), new FakeMultipass(first, second), new FakePresets());

        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.SelectSandboxAsync(first.InstanceName));
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.DeleteSandboxAsync(first.InstanceName));

        var unchanged = await store.LoadAsync();
        Assert.Equal(SetupState.NeedsReview, unchanged.SetupState);
        Assert.Equal(second.InstanceName, unchanged.InstanceName);
    }

    [Fact]
    public async Task MissingManagedSandboxCanBeSelectedAndRemoved()
    {
        var resources = new ResourceProfile(2, 4, 30);
        var existing = new SandboxInfo("agent-sandbox-one", SandboxState.Running, resources, "10.0.0.2", "24.04", DateTimeOffset.UtcNow);
        var store = new MemorySettings(new AgentSandboxSettings
        {
            InstanceName = existing.InstanceName,
            SetupState = SetupState.Ready,
            Resources = resources,
            Sandboxes =
            [
                new SandboxConfiguration(existing.InstanceName, resources, []),
                new SandboxConfiguration("agent-sandbox-missing", resources, [])
            ]
        });
        var coordinator = new SetupCoordinator(store, new FakePrerequisites(), new FakeMultipass(existing), new FakePresets());

        var selected = await coordinator.SelectSandboxAsync("agent-sandbox-missing");
        await coordinator.DeleteSandboxAsync("agent-sandbox-missing");
        var remaining = await store.LoadAsync();

        Assert.Equal("agent-sandbox-missing", selected.InstanceName);
        Assert.Equal(existing.InstanceName, remaining.InstanceName);
        Assert.Equal(existing.InstanceName, Assert.Single(remaining.Sandboxes).InstanceName);
    }

    private static OperationProgress Result(OperationState state, string phase) =>
        new(Guid.NewGuid(), "Test", state, phase, 100, null, null, state == OperationState.Succeeded ? null : "TEST", null, DateTimeOffset.UtcNow);

    private static HostReadiness ReadyHost() => new(true, true, true, true, true, false, true, true, "multipass.exe", "1", "hyperv", null, 32L << 30, 20L << 30, []);

    private sealed class MemorySettings(AgentSandboxSettings? initial = null) : ISettingsStore
    {
        private AgentSandboxSettings value = initial ?? new();
        public Task<AgentSandboxSettings> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(value);
        public Task SaveAsync(AgentSandboxSettings settings, CancellationToken cancellationToken = default) { value = settings; return Task.CompletedTask; }
    }
    private sealed class FakePrerequisites(HostReadiness? readiness = null) : IHostPrerequisiteService
    {
        public Task<HostReadiness> InspectAsync(CancellationToken cancellationToken = default) => Task.FromResult(readiness ?? ReadyHost());
        public Task<SetupHelperResponse> ExecuteElevatedAsync(SetupHelperRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
    private sealed class FakePresets(OperationProgress? installResult = null) : IPresetService
    {
        public Task<IReadOnlyList<AgentPresetManifest>> GetAvailableAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<AgentPresetManifest>>([]);
        public Task<OperationProgress> InstallAsync(string instanceName, IReadOnlyList<string> presetIds, IProgress<OperationProgress>? progress = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(installResult ?? Result(OperationState.Succeeded, "Installed"));
    }
    private sealed class FakeMultipass(params SandboxInfo[] initial) : IMultipassService
    {
        private readonly Dictionary<string, SandboxInfo> sandboxes = initial.ToDictionary(item => item.InstanceName, StringComparer.Ordinal);
        public OperationProgress ProvisionResult { get; init; } = Result(OperationState.Succeeded, "Ready");
        public ProvisionRequest? LastProvisionRequest { get; private set; }
        public Task<SandboxInfo?> GetSandboxAsync(string instanceName, CancellationToken cancellationToken = default) =>
            Task.FromResult(sandboxes.GetValueOrDefault(instanceName));
        public Task<IReadOnlyList<SandboxInfo>> ListSandboxesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SandboxInfo>>(sandboxes.Values.ToArray());
        public Task<SandboxResourceUsage> GetResourceUsageAsync(string instanceName, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<OperationProgress> StartAsync(string instanceName, IProgress<OperationProgress>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<OperationProgress> StopAsync(string instanceName, IProgress<OperationProgress>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<OperationProgress> ProvisionAsync(ProvisionRequest request, IProgress<OperationProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            LastProvisionRequest = request;
            if (ProvisionResult.State is OperationState.Succeeded or OperationState.CleanupPending)
            {
                var imageName = request.IsUserSuppliedImage
                    ? "Custom Linux image"
                    : LinuxImages.All.Single(image => image.ImageReference == request.Image).DisplayName;
                sandboxes.Add(request.InstanceName, new SandboxInfo(request.InstanceName, SandboxState.Running, request.Resources, "10.0.0.4", imageName, DateTimeOffset.UtcNow));
            }
            return Task.FromResult(ProvisionResult);
        }
        public Task<IReadOnlyList<SnapshotInfo>> ListSnapshotsAsync(string instanceName, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<SnapshotInfo>>([]);
        public Task<OperationProgress> CreateSnapshotAsync(string instanceName, string snapshotName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<OperationProgress> RestoreSnapshotAsync(string instanceName, string snapshotName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<OperationProgress> DeleteAsync(string instanceName, bool purge, CancellationToken cancellationToken = default)
        {
            if (!sandboxes.Remove(instanceName)) throw new InvalidOperationException("Exact fake sandbox not found.");
            return Task.FromResult(new OperationProgress(Guid.NewGuid(), "Delete", OperationState.Succeeded, "Deleted", 100, null, null, null, null, DateTimeOffset.UtcNow));
        }
    }
}
