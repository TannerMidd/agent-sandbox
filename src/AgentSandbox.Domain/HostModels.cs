namespace AgentSandbox.Domain;

public sealed record DiagnosticRecord(
    string Code,
    string Title,
    DiagnosticSeverity Severity,
    string Detail,
    string? Remediation = null);

public sealed record HostReadiness(
    bool IsWindows11,
    bool IsSupportedEdition,
    bool IsX64,
    bool HasVirtualization,
    bool IsHyperVEnabled,
    bool IsRebootPending,
    bool IsMultipassInstalled,
    bool IsMultipassCompatible,
    string? MultipassPath,
    string? MultipassVersion,
    string? MultipassDriver,
    string? MultipassStoragePath,
    long TotalMemoryBytes,
    long AvailableMemoryBytes,
    IReadOnlyList<DiagnosticRecord> Diagnostics)
{
    public bool CanProvision =>
        IsWindows11 && IsSupportedEdition && IsX64 && HasVirtualization &&
        IsHyperVEnabled && !IsRebootPending && IsMultipassInstalled && IsMultipassCompatible &&
        Diagnostics.All(diagnostic => diagnostic.Severity != DiagnosticSeverity.Error);
}

public sealed record ResourceProfile(int CpuCount, int MemoryGiB, int DiskGiB)
{
    public static ResourceProfile Recommend(int logicalProcessors, long availableMemoryBytes, long freeDiskBytes)
    {
        var cpu = Math.Clamp(logicalProcessors / 2, 2, 8);
        var availableGiB = (int)(availableMemoryBytes / 1_073_741_824L);
        var memory = Math.Clamp(Math.Max(4, availableGiB - 6), 4, 16);
        var freeDiskGiB = (int)(freeDiskBytes / 1_073_741_824L);
        var disk = Math.Clamp(Math.Min(50, freeDiskGiB - 10), 30, Math.Max(30, freeDiskGiB - 10));
        return new ResourceProfile(cpu, memory, disk);
    }

    public IReadOnlyList<string> Validate(int logicalProcessors, long totalMemoryBytes, long freeDiskBytes)
    {
        var errors = new List<string>();
        var totalMemoryGiB = (int)(totalMemoryBytes / 1_073_741_824L);
        var freeDiskGiB = (int)(freeDiskBytes / 1_073_741_824L);

        if (CpuCount < 2 || CpuCount > 8 || CpuCount > Math.Max(2, logicalProcessors - 2))
            errors.Add("CPU count must be between 2 and 8 and leave at least two logical processors for Windows.");
        if (MemoryGiB < 4 || MemoryGiB > 16 || MemoryGiB > Math.Max(4, totalMemoryGiB - 6))
            errors.Add("Memory must be between 4 and 16 GiB and leave at least 6 GiB for Windows.");
        if (DiskGiB < 30 || DiskGiB > freeDiskGiB - 10)
            errors.Add("Disk must be at least 30 GiB and leave at least 10 GiB free.");
        return errors;
    }
}

public sealed record SandboxInfo(
    string InstanceName,
    SandboxState State,
    ResourceProfile Resources,
    string? IPv4Address,
    string? UbuntuRelease,
    DateTimeOffset LastUpdatedAt,
    bool IsLegacyImport = false);

public sealed record SnapshotInfo(
    string Name,
    string InstanceName,
    DateTimeOffset? CreatedAt,
    string? Comment,
    bool IsBaseline);

public sealed record SandboxResourceUsage(
    double CpuPercent,
    long UsedMemoryBytes,
    long TotalMemoryBytes,
    long UsedDiskBytes,
    long TotalDiskBytes,
    DateTimeOffset SampledAt);
