using System.Text.Json;
using AgentSandbox.Application;
using AgentSandbox.Domain;

namespace AgentSandbox.Infrastructure;

public sealed class MultipassService(IProcessRunner runner, IMultipassLocator locator) : IMultipassService
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(2);

    public async Task<SandboxInfo?> GetSandboxAsync(string instanceName, CancellationToken cancellationToken = default)
    {
        ValidateIdentifier(instanceName);
        var all = await ListSandboxesAsync(cancellationToken);
        return all.SingleOrDefault(item => string.Equals(item.InstanceName, instanceName, StringComparison.Ordinal));
    }

    public async Task<IReadOnlyList<SandboxInfo>> ListSandboxesAsync(CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(["list", "--format", "json"], DefaultTimeout, cancellationToken);
        using var document = JsonDocument.Parse(result.StandardOutput);
        if (!document.RootElement.TryGetProperty("list", out var list)) return [];
        var sandboxes = new List<SandboxInfo>();
        foreach (var item in list.EnumerateArray())
        {
            var name = ReadString(item, "name");
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }
            var state = ParseState(ReadString(item, "state"));
            var ipv4 = ReadFirstString(item, "ipv4");
            sandboxes.Add(new SandboxInfo(name, state, new ResourceProfile(4, 4, 50), ipv4, null, DateTimeOffset.UtcNow));
        }
        return sandboxes;
    }

    public Task<OperationProgress> StartAsync(string instanceName, IProgress<OperationProgress>? progress = null, CancellationToken cancellationToken = default) =>
        RunOperationAsync("Start sandbox", ["start", ValidateIdentifier(instanceName)], progress, TimeSpan.FromMinutes(10), cancellationToken);

    public Task<OperationProgress> StopAsync(string instanceName, IProgress<OperationProgress>? progress = null, CancellationToken cancellationToken = default) =>
        RunOperationAsync("Stop sandbox", ["stop", ValidateIdentifier(instanceName)], progress, TimeSpan.FromMinutes(10), cancellationToken);

    public async Task<OperationProgress> ProvisionAsync(ProvisionRequest request, IProgress<OperationProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ValidateIdentifier(request.InstanceName);
        if (await GetSandboxAsync(request.InstanceName, cancellationToken) is not null)
            throw new InvalidOperationException($"Sandbox '{request.InstanceName}' already exists; provisioning will not overwrite it.");
        if (!File.Exists(request.CloudInitPath)) throw new FileNotFoundException("Cloud-init configuration was not found.", request.CloudInitPath);

        var id = Guid.NewGuid();
        Report(progress, id, "Provision sandbox", OperationState.Running, "Launching Ubuntu");
        var arguments = new[]
        {
            "launch", request.Image, "--name", request.InstanceName,
            "--cpus", request.Resources.CpuCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--memory", $"{request.Resources.MemoryGiB}G",
            "--disk", $"{request.Resources.DiskGiB}G",
            "--cloud-init", Path.GetFullPath(request.CloudInitPath), "--timeout", "1800"
        };
        try
        {
            await RunAsync(arguments, TimeSpan.FromMinutes(35), cancellationToken);
            Report(progress, id, "Provision sandbox", OperationState.Running, "Waiting for cloud-init");
            await RunAsync(["exec", request.InstanceName, "--", "cloud-init", "status", "--wait", "--long"], TimeSpan.FromMinutes(30), cancellationToken);
            await RunAsync(["exec", request.InstanceName, "--", "bash", "-lc", "set -eu; test -d /home/ubuntu/work; command -v git; command -v python3; command -v docker; test \"$(node --version)\" = v22.23.2; sudo -n docker info >/dev/null"], TimeSpan.FromMinutes(5), cancellationToken);
            Report(progress, id, "Provision sandbox", OperationState.Running, "Creating clean baseline");
            await RunAsync(["stop", request.InstanceName], TimeSpan.FromMinutes(10), cancellationToken);
            await RunAsync(["snapshot", request.InstanceName, "--name", ValidateIdentifier(request.BaselineSnapshot)], TimeSpan.FromMinutes(10), cancellationToken);
            await RunAsync(["start", request.InstanceName], TimeSpan.FromMinutes(10), cancellationToken);
            return Report(progress, id, "Provision sandbox", OperationState.Succeeded, "Ready");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var partialExists = false;
            try { partialExists = await GetSandboxAsync(request.InstanceName, CancellationToken.None) is not null; } catch { }
            var state = partialExists ? OperationState.CleanupPending : exception is OperationCanceledException ? OperationState.Canceled : OperationState.Failed;
            var phase = partialExists ? "Provisioning stopped; the partial VM was preserved for diagnostics" : "Provisioning failed before the VM was created";
            return Report(progress, id, "Provision sandbox", state, phase, "PROVISION_FAILED", exception.Message);
        }
    }

    public async Task<IReadOnlyList<SnapshotInfo>> ListSnapshotsAsync(string instanceName, CancellationToken cancellationToken = default)
    {
        ValidateIdentifier(instanceName);
        var result = await RunAsync(["list", "--snapshots", "--format", "json"], DefaultTimeout, cancellationToken);
        using var document = JsonDocument.Parse(result.StandardOutput);
        var snapshots = new List<SnapshotInfo>();
        CollectSnapshots(document.RootElement, instanceName, snapshots);
        return snapshots.DistinctBy(snapshot => snapshot.Name).ToArray();
    }

    public Task<OperationProgress> CreateSnapshotAsync(string instanceName, string snapshotName, CancellationToken cancellationToken = default) =>
        RunOperationAsync("Create snapshot", ["snapshot", ValidateIdentifier(instanceName), "--name", ValidateIdentifier(snapshotName)], null, TimeSpan.FromMinutes(10), cancellationToken);

    public async Task<OperationProgress> RestoreSnapshotAsync(string instanceName, string snapshotName, CancellationToken cancellationToken = default)
    {
        ValidateIdentifier(instanceName); ValidateIdentifier(snapshotName);
        var exactTarget = $"{instanceName}.{snapshotName}";
        var existing = await ListSnapshotsAsync(instanceName, cancellationToken);
        if (!existing.Any(item => string.Equals(item.Name, snapshotName, StringComparison.Ordinal)))
            throw new InvalidOperationException($"Exact snapshot '{exactTarget}' does not exist.");
        await RunAsync(["stop", instanceName], TimeSpan.FromMinutes(10), cancellationToken);
        return await RunOperationAsync("Restore snapshot", ["restore", "--destructive", exactTarget], null, TimeSpan.FromMinutes(20), cancellationToken);
    }

    public async Task<OperationProgress> DeleteAsync(string instanceName, bool purge, CancellationToken cancellationToken = default)
    {
        ValidateIdentifier(instanceName);
        var exact = await GetSandboxAsync(instanceName, cancellationToken);
        if (exact is null) throw new InvalidOperationException($"Exact sandbox '{instanceName}' does not exist.");
        await RunAsync(purge ? ["delete", "--purge", instanceName] : ["delete", instanceName], TimeSpan.FromMinutes(20), cancellationToken);
        return new OperationProgress(Guid.NewGuid(), "Delete sandbox", OperationState.Succeeded,
            purge ? "Sandbox deleted and Multipass trash purged" : "Sandbox moved to Multipass trash",
            100, null, null, null, null, DateTimeOffset.UtcNow);
    }

    private async Task<OperationProgress> RunOperationAsync(string title, IReadOnlyList<string> arguments, IProgress<OperationProgress>? progress, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();
        Report(progress, id, title, OperationState.Running, title);
        try
        {
            await RunAsync(arguments, timeout, cancellationToken);
            return Report(progress, id, title, OperationState.Succeeded, "Complete");
        }
        catch (OperationCanceledException)
        {
            return Report(progress, id, title, OperationState.Canceled, "Canceled");
        }
        catch (Exception exception)
        {
            return Report(progress, id, title, OperationState.Failed, "Failed", "MULTIPASS_FAILED", exception.Message);
        }
    }

    private async Task<ProcessResult> RunAsync(IEnumerable<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var executable = locator.Locate() ?? throw new FileNotFoundException("A verified Canonical Multipass executable was not found.");
        var result = await runner.RunAsync(executable, arguments, timeout: timeout, cancellationToken: cancellationToken);
        if (!result.IsSuccess)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput.Trim() : result.StandardError.Trim());
        return result;
    }

    private static OperationProgress Report(IProgress<OperationProgress>? progress, Guid id, string title, OperationState state, string phase, string? errorCode = null, string? detail = null)
    {
        var value = new OperationProgress(id, title, state, phase, null, null, null, errorCode, detail, DateTimeOffset.UtcNow);
        progress?.Report(value);
        return value;
    }

    private static string ValidateIdentifier(string value)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(value, "^[A-Za-z](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?$"))
            throw new ArgumentException("Multipass identifiers must contain only letters, numbers, and hyphens.", nameof(value));
        if (string.Equals(value, "primary", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("The Multipass name 'primary' is reserved.", nameof(value));
        return value;
    }

    private static SandboxState ParseState(string? state) => state?.ToUpperInvariant() switch
    {
        "RUNNING" => SandboxState.Running,
        "STOPPED" => SandboxState.Stopped,
        "STARTING" => SandboxState.Starting,
        "SUSPENDED" => SandboxState.Suspended,
        _ => SandboxState.Unknown
    };

    private static string? ReadString(JsonElement element, string property) => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static string? ReadFirstString(JsonElement element, string property) => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().FirstOrDefault().GetString() : ReadString(element, property);

    private static void CollectSnapshots(JsonElement element, string instanceName, ICollection<SnapshotInfo> output)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var name = ReadString(element, "name") ?? ReadString(element, "snapshot");
            var instance = ReadString(element, "instance") ?? instanceName;
            if (name?.StartsWith(instanceName + ".", StringComparison.Ordinal) == true) name = name[(instanceName.Length + 1)..];
            if (!string.IsNullOrWhiteSpace(name) && string.Equals(instance, instanceName, StringComparison.Ordinal))
                output.Add(new SnapshotInfo(name, instanceName, null, ReadString(element, "comment"), string.Equals(name, "clean", StringComparison.Ordinal)));
            foreach (var property in element.EnumerateObject()) CollectSnapshots(property.Value, instanceName, output);
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray()) CollectSnapshots(child, instanceName, output);
        }
    }
}
