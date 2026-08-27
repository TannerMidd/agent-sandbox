using AgentSandbox.Application;
using AgentSandbox.Domain;

namespace AgentSandbox.Infrastructure;

public sealed class FakeMultipassService : IMultipassService
{
    private readonly Dictionary<string, SandboxInfo> sandboxes = new(StringComparer.Ordinal);
    private readonly List<SnapshotInfo> snapshots = [];

    public Task<SandboxInfo?> GetSandboxAsync(string instanceName, CancellationToken cancellationToken = default) =>
        Task.FromResult(sandboxes.GetValueOrDefault(instanceName));
    public Task<IReadOnlyList<SandboxInfo>> ListSandboxesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<SandboxInfo>>(sandboxes.Values.ToArray());
    public Task<IReadOnlyList<SnapshotInfo>> ListSnapshotsAsync(string instanceName, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<SnapshotInfo>>(snapshots.Where(item => item.InstanceName == instanceName).ToArray());

    public Task<OperationProgress> StartAsync(string instanceName, IProgress<OperationProgress>? progress = null, CancellationToken cancellationToken = default) =>
        SetState(instanceName, SandboxState.Running, "Started", progress);
    public Task<OperationProgress> StopAsync(string instanceName, IProgress<OperationProgress>? progress = null, CancellationToken cancellationToken = default) =>
        SetState(instanceName, SandboxState.Stopped, "Stopped", progress);

    public Task<OperationProgress> ProvisionAsync(ProvisionRequest request, IProgress<OperationProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (sandboxes.ContainsKey(request.InstanceName)) throw new InvalidOperationException("Fake sandbox already exists.");
        sandboxes.Add(request.InstanceName, new SandboxInfo(request.InstanceName, SandboxState.Running, request.Resources, "10.10.10.10", "24.04", DateTimeOffset.UtcNow));
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
        Require(instanceName);
        sandboxes.Remove(instanceName);
        snapshots.RemoveAll(item => item.InstanceName == instanceName);
        return Task.FromResult(Result("Delete sandbox", purge ? "Purged" : "Deleted", null));
    }

    private Task<OperationProgress> SetState(string instanceName, SandboxState state, string phase, IProgress<OperationProgress>? progress)
    {
        sandboxes[instanceName] = Require(instanceName) with { State = state, LastUpdatedAt = DateTimeOffset.UtcNow };
        return Task.FromResult(Result("Lifecycle", phase, progress));
    }
    private SandboxInfo Require(string instanceName) => sandboxes.GetValueOrDefault(instanceName) ?? throw new InvalidOperationException("Exact fake instance not found.");
    private static OperationProgress Result(string title, string phase, IProgress<OperationProgress>? progress)
    {
        var result = new OperationProgress(Guid.NewGuid(), title, OperationState.Succeeded, phase, 100, null, null, null, null, DateTimeOffset.UtcNow);
        progress?.Report(result); return result;
    }
}
