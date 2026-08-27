using AgentSandbox.Application;
using AgentSandbox.Domain;

namespace AgentSandbox.Infrastructure;

public sealed class FakeMultipassService : IMultipassService
{
    private SandboxInfo? sandbox;
    private readonly List<SnapshotInfo> snapshots = [];

    public Task<SandboxInfo?> GetSandboxAsync(string instanceName, CancellationToken cancellationToken = default) =>
        Task.FromResult(sandbox is not null && sandbox.InstanceName == instanceName ? sandbox : null);
    public Task<IReadOnlyList<SandboxInfo>> ListSandboxesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<SandboxInfo>>(sandbox is null ? [] : [sandbox]);
    public Task<IReadOnlyList<SnapshotInfo>> ListSnapshotsAsync(string instanceName, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<SnapshotInfo>>(snapshots.Where(item => item.InstanceName == instanceName).ToArray());

    public Task<OperationProgress> StartAsync(string instanceName, IProgress<OperationProgress>? progress = null, CancellationToken cancellationToken = default) =>
        SetState(instanceName, SandboxState.Running, "Started", progress);
    public Task<OperationProgress> StopAsync(string instanceName, IProgress<OperationProgress>? progress = null, CancellationToken cancellationToken = default) =>
        SetState(instanceName, SandboxState.Stopped, "Stopped", progress);

    public Task<OperationProgress> ProvisionAsync(ProvisionRequest request, IProgress<OperationProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (sandbox is not null) throw new InvalidOperationException("Fake sandbox already exists.");
        sandbox = new SandboxInfo(request.InstanceName, SandboxState.Running, request.Resources, "10.10.10.10", "24.04", DateTimeOffset.UtcNow);
        snapshots.Add(new SnapshotInfo(request.BaselineSnapshot, request.InstanceName, DateTimeOffset.UtcNow, "Clean baseline", true));
        return Task.FromResult(Result("Provision", "Ready", progress));
    }

    public Task<OperationProgress> CreateSnapshotAsync(string instanceName, string snapshotName, CancellationToken cancellationToken = default)
    {
        Require(instanceName);
        snapshots.Add(new SnapshotInfo(snapshotName, instanceName, DateTimeOffset.UtcNow, null, snapshotName == "clean"));
        return Task.FromResult(Result("Snapshot", "Created", null));
    }

    public Task<OperationProgress> RestoreSnapshotAsync(string instanceName, string snapshotName, CancellationToken cancellationToken = default)
    {
        Require(instanceName);
        if (!snapshots.Any(item => item.InstanceName == instanceName && item.Name == snapshotName)) throw new InvalidOperationException("Exact fake snapshot not found.");
        return Task.FromResult(Result("Restore", "Restored", null));
    }

    public Task<OperationProgress> DeleteAsync(string instanceName, bool purge, CancellationToken cancellationToken = default)
    {
        if (!string.Equals(instanceName, sandbox?.InstanceName, StringComparison.Ordinal))
            throw new InvalidOperationException("The exact sandbox does not exist.");
        sandbox = null;
        snapshots.Clear();
        return Task.FromResult(Result("Delete sandbox", purge ? "Purged" : "Deleted", null));
    }

    private Task<OperationProgress> SetState(string instanceName, SandboxState state, string phase, IProgress<OperationProgress>? progress)
    {
        Require(instanceName);
        sandbox = sandbox! with { State = state, LastUpdatedAt = DateTimeOffset.UtcNow };
        return Task.FromResult(Result("Lifecycle", phase, progress));
    }
    private void Require(string instanceName) { if (sandbox?.InstanceName != instanceName) throw new InvalidOperationException("Exact fake instance not found."); }
    private static OperationProgress Result(string title, string phase, IProgress<OperationProgress>? progress)
    {
        var result = new OperationProgress(Guid.NewGuid(), title, OperationState.Succeeded, phase, 100, null, null, null, null, DateTimeOffset.UtcNow);
        progress?.Report(result); return result;
    }
}
