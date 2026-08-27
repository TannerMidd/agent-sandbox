using System.Text.Json;
using AgentSandbox.Application;
using AgentSandbox.Domain;

namespace AgentSandbox.Infrastructure;

public sealed class MultipassService(IProcessRunner runner, IMultipassLocator locator) : IMultipassService
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(2);
    private const string ResourceUsageScript = """
        import json
        import shutil
        import time

        def cpu_sample():
            with open("/proc/stat", encoding="ascii") as source:
                values = [int(value) for value in source.readline().split()[1:]]
            return sum(values), values[3] + values[4]

        total1, idle1 = cpu_sample()
        time.sleep(0.2)
        total2, idle2 = cpu_sample()
        elapsed = max(total2 - total1, 1)
        cpu_percent = max(0.0, min(100.0, (1.0 - ((idle2 - idle1) / elapsed)) * 100.0))

        memory = {}
        with open("/proc/meminfo", encoding="ascii") as source:
            for line in source:
                key, value = line.split(":", 1)
                memory[key] = int(value.split()[0]) * 1024

        disk = shutil.disk_usage("/")
        print(json.dumps({
            "cpuPercent": round(cpu_percent, 1),
            "usedMemoryBytes": memory["MemTotal"] - memory["MemAvailable"],
            "totalMemoryBytes": memory["MemTotal"],
            "usedDiskBytes": disk.used,
            "totalDiskBytes": disk.total
        }))
        """;

    public async Task<SandboxInfo?> GetSandboxAsync(string instanceName, CancellationToken cancellationToken = default)
    {
        ValidateIdentifier(instanceName);
        var all = await ListSandboxesAsync(cancellationToken);
        var summary = all.SingleOrDefault(item => string.Equals(item.InstanceName, instanceName, StringComparison.Ordinal));
        if (summary is null) return null;
        var result = await RunAsync(["info", instanceName, "--format", "json"], DefaultTimeout, cancellationToken);
        using var document = JsonDocument.Parse(result.StandardOutput);
        return EnrichFromInfo(document.RootElement, summary);
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
            var release = ReadString(item, "release");
            sandboxes.Add(new SandboxInfo(name, state, new ResourceProfile(4, 4, 50), ipv4, release, DateTimeOffset.UtcNow));
        }
        return sandboxes;
    }

    public async Task<SandboxResourceUsage> GetResourceUsageAsync(string instanceName, CancellationToken cancellationToken = default)
    {
        ValidateIdentifier(instanceName);
        var result = await RunAsync(["exec", instanceName, "--", "python3", "-c", ResourceUsageScript], TimeSpan.FromSeconds(20), cancellationToken);
        using var document = JsonDocument.Parse(result.StandardOutput);
        var root = document.RootElement;
        var cpuPercent = ReadRequiredDouble(root, "cpuPercent");
        var usedMemoryBytes = ReadRequiredInt64(root, "usedMemoryBytes");
        var totalMemoryBytes = ReadRequiredInt64(root, "totalMemoryBytes");
        var usedDiskBytes = ReadRequiredInt64(root, "usedDiskBytes");
        var totalDiskBytes = ReadRequiredInt64(root, "totalDiskBytes");
        if (totalMemoryBytes == 0 || totalDiskBytes == 0)
            throw new JsonException("The guest returned an invalid zero resource capacity.");

        return new SandboxResourceUsage(
            Math.Clamp(cpuPercent, 0, 100),
            Math.Min(usedMemoryBytes, totalMemoryBytes),
            totalMemoryBytes,
            Math.Min(usedDiskBytes, totalDiskBytes),
            totalDiskBytes,
            DateTimeOffset.UtcNow);
    }

    public Task<OperationProgress> StartAsync(string instanceName, IProgress<OperationProgress>? progress = null, CancellationToken cancellationToken = default) =>
        RunOperationAsync("Start sandbox", ["start", ValidateIdentifier(instanceName)], progress, TimeSpan.FromMinutes(10), cancellationToken);

    public Task<OperationProgress> StopAsync(string instanceName, IProgress<OperationProgress>? progress = null, CancellationToken cancellationToken = default) =>
        RunOperationAsync("Stop sandbox", ["stop", ValidateIdentifier(instanceName)], progress, TimeSpan.FromMinutes(10), cancellationToken);

    public async Task<OperationProgress> ProvisionAsync(ProvisionRequest request, IProgress<OperationProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ValidateIdentifier(request.InstanceName);
        if (request.IsUserSuppliedImage)
            _ = LinuxImages.ValidateCustomImageUrl(request.Image);
        else if (!LinuxImages.IsKnownReference(request.Image))
            throw new ArgumentException("The requested Linux image is not in the approved image catalog.", nameof(request));
        if (await GetSandboxAsync(request.InstanceName, cancellationToken) is not null)
            throw new InvalidOperationException($"Sandbox '{request.InstanceName}' already exists; provisioning will not overwrite it.");
        if (!File.Exists(request.CloudInitPath)) throw new FileNotFoundException("Cloud-init configuration was not found.", request.CloudInitPath);

        var id = Guid.NewGuid();
        var imageName = request.IsUserSuppliedImage
            ? "custom Linux image"
            : LinuxImages.All.Single(item => string.Equals(item.ImageReference, request.Image, StringComparison.Ordinal)).DisplayName;
        Report(progress, id, "Provision sandbox", OperationState.Running, $"Launching {imageName}");
        string? renderedCloudInitPath = null;
        try
        {
            var hardening = request.Hardening ?? SandboxHardeningOptions.Development;
            renderedCloudInitPath = await CloudInitRenderer.CreateTemporaryAsync(request.CloudInitPath, hardening, cancellationToken);
            var arguments = new[]
            {
                "launch", request.Image, "--name", request.InstanceName,
                "--cpus", request.Resources.CpuCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "--memory", $"{request.Resources.MemoryGiB}G",
                "--disk", $"{request.Resources.DiskGiB}G",
                "--cloud-init", renderedCloudInitPath, "--timeout", "1800"
            };
            await RunAsync(arguments, TimeSpan.FromMinutes(35), cancellationToken);
            Report(progress, id, "Provision sandbox", OperationState.Running, "Waiting for cloud-init");
            await RunAsync(["exec", request.InstanceName, "--", "cloud-init", "status", "--wait"], TimeSpan.FromMinutes(30), cancellationToken);
            var hardeningVerification = BuildHardeningVerificationScript(hardening);
            await RunAsync(["exec", request.InstanceName, "--", "bash", "-lc", $"set -eu; test -d /home/ubuntu/work; test -s /etc/agent-sandbox/hardening.json; command -v git; command -v python3; command -v docker; command -v npm; node -e 'if (+process.versions.node.split(\".\")[0] < 20) process.exit(1)'; {hardeningVerification}"], TimeSpan.FromMinutes(5), cancellationToken);
            var policyArtifact = await RunAsync(["exec", request.InstanceName, "--", "cat", "/etc/agent-sandbox/hardening.json"], TimeSpan.FromMinutes(2), cancellationToken);
            ValidateHardeningArtifact(policyArtifact.StandardOutput, hardening);
            Report(progress, id, "Provision sandbox", OperationState.Running, "Creating clean baseline");
            await RunAsync(["stop", request.InstanceName], TimeSpan.FromMinutes(10), cancellationToken);
            await RunAsync(["snapshot", request.InstanceName, "--name", ValidateIdentifier(request.BaselineSnapshot)], TimeSpan.FromMinutes(10), cancellationToken);
            await RunAsync(["start", request.InstanceName], TimeSpan.FromMinutes(10), cancellationToken);
            await RunAsync(["exec", request.InstanceName, "--", "bash", "-lc", $"set -eu; {hardeningVerification}"], TimeSpan.FromMinutes(5), cancellationToken);
            return Report(progress, id, "Provision sandbox", OperationState.Succeeded, "Ready");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var partialExists = false;
            try
            {
                partialExists = (await ListSandboxesAsync(CancellationToken.None))
                    .Any(item => string.Equals(item.InstanceName, request.InstanceName, StringComparison.Ordinal));
            }
            catch { }
            var state = partialExists ? OperationState.CleanupPending : exception is OperationCanceledException ? OperationState.Canceled : OperationState.Failed;
            var phase = partialExists ? "Provisioning stopped; the partial VM was preserved for diagnostics" : "Provisioning failed before the VM was created";
            return Report(progress, id, "Provision sandbox", state, phase, "PROVISION_FAILED", exception.Message);
        }
        finally
        {
            if (renderedCloudInitPath is not null)
            {
                try { File.Delete(renderedCloudInitPath); } catch { }
            }
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

    public async Task<OperationProgress> CreateSnapshotAsync(string instanceName, string snapshotName, CancellationToken cancellationToken = default)
    {
        ValidateIdentifier(instanceName); ValidateIdentifier(snapshotName);
        var sandbox = await GetSandboxAsync(instanceName, cancellationToken)
            ?? throw new InvalidOperationException($"Exact sandbox '{instanceName}' does not exist.");
        var restart = sandbox.State == SandboxState.Running;
        if (sandbox.State != SandboxState.Stopped)
            await RunAsync(["stop", instanceName], TimeSpan.FromMinutes(10), cancellationToken);
        var result = await RunOperationAsync("Create snapshot", ["snapshot", instanceName, "--name", snapshotName], null, TimeSpan.FromMinutes(10), cancellationToken);
        if (restart)
            await RunAsync(["start", instanceName], TimeSpan.FromMinutes(10), CancellationToken.None);
        return result;
    }

    public async Task<OperationProgress> RestoreSnapshotAsync(string instanceName, string snapshotName, CancellationToken cancellationToken = default)
    {
        ValidateIdentifier(instanceName); ValidateIdentifier(snapshotName);
        var exactTarget = $"{instanceName}.{snapshotName}";
        var sandbox = await GetSandboxAsync(instanceName, cancellationToken)
            ?? throw new InvalidOperationException($"Exact sandbox '{instanceName}' does not exist.");
        var existing = await ListSnapshotsAsync(instanceName, cancellationToken);
        if (!existing.Any(item => string.Equals(item.Name, snapshotName, StringComparison.Ordinal)))
            throw new InvalidOperationException($"Exact snapshot '{exactTarget}' does not exist.");
        var restart = sandbox.State == SandboxState.Running;
        if (sandbox.State != SandboxState.Stopped)
            await RunAsync(["stop", instanceName], TimeSpan.FromMinutes(10), cancellationToken);
        var result = await RunOperationAsync("Restore snapshot", ["restore", "--destructive", exactTarget], null, TimeSpan.FromMinutes(20), cancellationToken);
        if (restart && result.State == OperationState.Succeeded)
            await RunAsync(["start", instanceName], TimeSpan.FromMinutes(10), cancellationToken);
        return result;
    }

    public async Task<OperationProgress> DeleteSnapshotAsync(string instanceName, string snapshotName, CancellationToken cancellationToken = default)
    {
        ValidateIdentifier(instanceName); ValidateIdentifier(snapshotName);
        var exactTarget = $"{instanceName}.{snapshotName}";
        var existing = await ListSnapshotsAsync(instanceName, cancellationToken);
        if (!existing.Any(item => string.Equals(item.Name, snapshotName, StringComparison.Ordinal)))
            throw new InvalidOperationException($"Exact snapshot '{exactTarget}' does not exist.");
        return await RunOperationAsync("Delete snapshot", ["delete", exactTarget], null, TimeSpan.FromMinutes(10), cancellationToken);
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

    public static string BuildHardeningVerificationScript(SandboxHardeningOptions options)
    {
        options.Validate();
        var checks = new List<string>
        {
            "test -S /var/run/docker.sock",
            "sshd -T | grep -Fx 'passwordauthentication no' >/dev/null",
            "sshd -T | grep -Fx 'permitrootlogin no' >/dev/null"
        };
        checks.Add("sudo -n /usr/local/sbin/agent-sandbox-verify-runtime >/dev/null");
        checks.Add(options.AllowAdministrativeTools
            ? "sudo -n true >/dev/null 2>&1; docker info >/dev/null 2>&1"
            : "! sudo -n true >/dev/null 2>&1; ! docker info >/dev/null 2>&1");
        if (options.KernelHardening)
        {
            checks.Add("test \"$(sysctl -n kernel.kptr_restrict)\" -ge 2");
            checks.Add("test \"$(sysctl -n kernel.dmesg_restrict)\" -ge 1");
            checks.Add("test \"$(sysctl -n net.ipv4.conf.all.accept_redirects)\" -eq 0");
        }
        if (options.RestrictUnprivilegedFeatures)
        {
            checks.Add("test \"$(sysctl -n user.max_user_namespaces)\" -eq 0");
            checks.Add("grep -Fx '* hard core 0' /etc/security/limits.d/60-agent-sandbox.conf >/dev/null");
        }
        if (options.AuditSecurityEvents)
        {
            checks.Add("test -s /etc/audit/rules.d/60-agent-sandbox.rules");
            checks.Add("if command -v systemctl >/dev/null 2>&1; then systemctl is-active --quiet auditd; else rc-service auditd status >/dev/null; fi");
        }
        return string.Join("; ", checks);
    }

    public static void ValidateHardeningArtifact(string json, SandboxHardeningOptions expected)
    {
        ArgumentNullException.ThrowIfNull(expected);
        expected.Validate();
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var network = expected.NetworkAccess switch
        {
            NetworkAccessPolicy.Unrestricted => "unrestricted",
            NetworkAccessPolicy.WebOnly => "web-only",
            NetworkAccessPolicy.Offline => "offline",
            _ => throw new ArgumentOutOfRangeException(nameof(expected))
        };
        var matches = root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("schemaVersion", out var schema) && schema.ValueKind == JsonValueKind.Number && schema.GetInt32() == 1 &&
            RequiredString(root, "presetId") == expected.PresetId &&
            RequiredBoolean(root, "automaticSecurityUpdates") == expected.AutomaticSecurityUpdates &&
            RequiredBoolean(root, "kernelHardening") == expected.KernelHardening &&
            RequiredBoolean(root, "restrictUnprivilegedFeatures") == expected.RestrictUnprivilegedFeatures &&
            RequiredBoolean(root, "auditSecurityEvents") == expected.AuditSecurityEvents &&
            RequiredString(root, "networkAccess") == network &&
            RequiredBoolean(root, "allowAdministrativeTools") == expected.AllowAdministrativeTools;
        if (!matches) throw new InvalidDataException("The guest hardening artifact does not match the requested policy.");
    }

    private static string RequiredString(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw new InvalidDataException($"The guest hardening artifact is missing '{property}'.");

    private static bool RequiredBoolean(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : throw new InvalidDataException($"The guest hardening artifact is missing '{property}'.");

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
    private static double ReadRequiredDouble(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value) || !value.TryGetDouble(out var result) || !double.IsFinite(result))
            throw new JsonException($"Guest resource usage did not include a valid '{property}' value.");
        return result;
    }

    private static long ReadRequiredInt64(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value) || !value.TryGetInt64(out var result) || result < 0)
            throw new JsonException($"Guest resource usage did not include a valid '{property}' value.");
        return result;
    }

    private static SandboxInfo EnrichFromInfo(JsonElement root, SandboxInfo summary)
    {
        if (!root.TryGetProperty("info", out var info) || info.ValueKind != JsonValueKind.Object ||
            !info.TryGetProperty(summary.InstanceName, out var item) || item.ValueKind != JsonValueKind.Object)
            throw new JsonException($"Multipass info did not include exact instance '{summary.InstanceName}'.");
        var cpu = ReadPositiveInt(item, "cpu_count");
        var memoryBytes = ReadNestedPositiveInt64(item, "memory", "total");
        long diskBytes = 0;
        if (item.TryGetProperty("disks", out var disks) && disks.ValueKind == JsonValueKind.Object)
        {
            foreach (var disk in disks.EnumerateObject())
                diskBytes = Math.Max(diskBytes, ReadPositiveInt64(disk.Value, "total"));
        }
        if (diskBytes <= 0) throw new JsonException("Multipass info did not include a valid disk capacity.");
        var resources = new ResourceProfile(cpu, BytesToGiB(memoryBytes), BytesToGiB(diskBytes));
        return summary with
        {
            Resources = resources,
            State = ReadString(item, "state") is { } state ? ParseState(state) : summary.State,
            IPv4Address = ReadFirstString(item, "ipv4") ?? summary.IPv4Address,
            OsRelease = ReadString(item, "release") ?? summary.OsRelease,
            LastUpdatedAt = DateTimeOffset.UtcNow
        };
    }

    private static int ReadPositiveInt(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value)) throw new JsonException($"Multipass info is missing '{property}'.");
        var text = value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();
        return int.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var result) && result > 0
            ? result
            : throw new JsonException($"Multipass info has an invalid '{property}'.");
    }

    private static long ReadNestedPositiveInt64(JsonElement element, string parent, string property)
    {
        if (!element.TryGetProperty(parent, out var nested) || nested.ValueKind != JsonValueKind.Object)
            throw new JsonException($"Multipass info is missing '{parent}'.");
        return ReadPositiveInt64(nested, property);
    }

    private static long ReadPositiveInt64(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value)) throw new JsonException($"Multipass info is missing '{property}'.");
        var text = value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();
        return long.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var result) && result > 0
            ? result
            : throw new JsonException($"Multipass info has an invalid '{property}'.");
    }

    private static int BytesToGiB(long bytes) => checked((int)Math.Max(1, (bytes + 1_073_741_823L) / 1_073_741_824L));

    private static string? ReadFirstString(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value)) return null;
        if (value.ValueKind != JsonValueKind.Array) return value.ValueKind == JsonValueKind.String ? value.GetString() : null;

        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String) return item.GetString();
        }

        return null;
    }

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
