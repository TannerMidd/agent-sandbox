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

    private static HostReadiness ReadyHost() => new(true, true, true, true, true, false, true, true, "multipass.exe", "1", "hyperv", null, 32L << 30, 20L << 30, []);

    private sealed class MemorySettings : ISettingsStore
    {
        private AgentSandboxSettings value = new();
        public Task<AgentSandboxSettings> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(value);
        public Task SaveAsync(AgentSandboxSettings settings, CancellationToken cancellationToken = default) { value = settings; return Task.CompletedTask; }
    }
    private sealed class FakePrerequisites : IHostPrerequisiteService
    {
        public Task<HostReadiness> InspectAsync(CancellationToken cancellationToken = default) => Task.FromResult(ReadyHost());
        public Task<SetupHelperResponse> ExecuteElevatedAsync(SetupHelperRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
    private sealed class FakePresets : IPresetService
    {
        public Task<IReadOnlyList<AgentPresetManifest>> GetAvailableAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<AgentPresetManifest>>([]);
        public Task<OperationProgress> InstallAsync(string instanceName, IReadOnlyList<string> presetIds, IProgress<OperationProgress>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
    private sealed class FakeMultipass : IMultipassService
    {
        public Task<SandboxInfo?> GetSandboxAsync(string instanceName, CancellationToken cancellationToken = default) => Task.FromResult<SandboxInfo?>(null);
        public Task<IReadOnlyList<SandboxInfo>> ListSandboxesAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<SandboxInfo>>([]);
        public Task<OperationProgress> StartAsync(string instanceName, IProgress<OperationProgress>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<OperationProgress> StopAsync(string instanceName, IProgress<OperationProgress>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<OperationProgress> ProvisionAsync(ProvisionRequest request, IProgress<OperationProgress>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<SnapshotInfo>> ListSnapshotsAsync(string instanceName, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<SnapshotInfo>>([]);
        public Task<OperationProgress> CreateSnapshotAsync(string instanceName, string snapshotName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<OperationProgress> RestoreSnapshotAsync(string instanceName, string snapshotName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<OperationProgress> DeleteAsync(string instanceName, bool purge, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
