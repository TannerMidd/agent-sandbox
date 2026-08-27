using AgentSandbox.Domain;

namespace AgentSandbox.Application;

public interface ISettingsStore
{
    Task<AgentSandboxSettings> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(AgentSandboxSettings settings, CancellationToken cancellationToken = default);
}

public interface IOperationHistoryStore
{
    Task AppendAsync(OperationProgress operation, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<OperationProgress>> ReadRecentAsync(int count = 100, CancellationToken cancellationToken = default);
}

public interface IHostPrerequisiteService
{
    Task<HostReadiness> InspectAsync(CancellationToken cancellationToken = default);
    Task<SetupHelperResponse> ExecuteElevatedAsync(SetupHelperRequest request, CancellationToken cancellationToken = default);
}

public interface IMultipassService
{
    Task<SandboxInfo?> GetSandboxAsync(string instanceName, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SandboxInfo>> ListSandboxesAsync(CancellationToken cancellationToken = default);
    Task<OperationProgress> StartAsync(string instanceName, IProgress<OperationProgress>? progress = null, CancellationToken cancellationToken = default);
    Task<OperationProgress> StopAsync(string instanceName, IProgress<OperationProgress>? progress = null, CancellationToken cancellationToken = default);
    Task<OperationProgress> ProvisionAsync(ProvisionRequest request, IProgress<OperationProgress>? progress = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SnapshotInfo>> ListSnapshotsAsync(string instanceName, CancellationToken cancellationToken = default);
    Task<OperationProgress> CreateSnapshotAsync(string instanceName, string snapshotName, CancellationToken cancellationToken = default);
    Task<OperationProgress> RestoreSnapshotAsync(string instanceName, string snapshotName, CancellationToken cancellationToken = default);
    Task<OperationProgress> DeleteAsync(string instanceName, bool purge, CancellationToken cancellationToken = default);
}

public interface ISandboxLifecycleService
{
    Task<AgentSandboxSettings> ResumeSetupAsync(CancellationToken cancellationToken = default);
    Task<LegacyImportCandidate?> InspectLegacyImportAsync(CancellationToken cancellationToken = default);
    Task<AgentSandboxSettings> ImportLegacyAsync(LegacyImportCandidate candidate, CancellationToken cancellationToken = default);
    Task<AgentSandboxSettings> SelectSandboxAsync(string instanceName, CancellationToken cancellationToken = default);
    Task<OperationProgress> ProvisionAsync(string instanceName, ResourceProfile resources, IReadOnlyList<string> presetIds, IProgress<OperationProgress>? progress = null, CancellationToken cancellationToken = default);
    Task<OperationProgress> DeleteSandboxAsync(string instanceName, CancellationToken cancellationToken = default);
}

public interface IGuestFileService
{
    Task<GuestFileResponse> ExecuteAsync(GuestFileRequest request, CancellationToken cancellationToken = default);
    Task<TransferJob> UploadAsync(IReadOnlyList<string> hostPaths, IReadOnlyList<string> guestDestination, FileConflictPolicy conflictPolicy, IProgress<OperationProgress>? progress = null, CancellationToken cancellationToken = default);
    Task<TransferJob> DownloadAsync(IReadOnlyList<IReadOnlyList<string>> guestPaths, string hostDestination, FileConflictPolicy conflictPolicy, IProgress<OperationProgress>? progress = null, CancellationToken cancellationToken = default);
    Task ReconcileAsync(CancellationToken cancellationToken = default);
}

public interface IPresetService
{
    Task<IReadOnlyList<AgentPresetManifest>> GetAvailableAsync(CancellationToken cancellationToken = default);
    Task<OperationProgress> InstallAsync(string instanceName, IReadOnlyList<string> presetIds, IProgress<OperationProgress>? progress = null, CancellationToken cancellationToken = default);
}

public interface ITerminalService
{
    Task<ITerminalSession> OpenEmbeddedAsync(string instanceName, CancellationToken cancellationToken = default);
    Task OpenExternalAsync(string instanceName, CancellationToken cancellationToken = default);
}

public interface ITerminalSession : IAsyncDisposable
{
    Stream Input { get; }
    Stream Output { get; }
    Task<int> Completion { get; }
    void Resize(int columns, int rows);
}

public interface IReleaseService
{
    Task<ReleaseInfo?> CheckAsync(Version currentVersion, CancellationToken cancellationToken = default);
}

public interface IMultipassInstallerService
{
    MultipassInstallerRelease Release { get; }
    Task<string> DownloadAsync(IProgress<OperationProgress>? progress = null, CancellationToken cancellationToken = default);
}

public sealed record ProvisionRequest(
    string InstanceName,
    string Image,
    ResourceProfile Resources,
    string CloudInitPath,
    string BaselineSnapshot);

public sealed record LegacyImportCandidate(
    string InstanceName,
    SandboxState State,
    string? StoragePath,
    IReadOnlyList<string> SnapshotNames,
    IReadOnlyList<DiagnosticRecord> Diagnostics);

public sealed record ReleaseInfo(Version Version, Uri ReleasePage, string Notes, bool IsPrerelease);

public sealed record MultipassInstallerRelease(
    Version Version,
    Uri DownloadUri,
    string Sha256,
    string Publisher,
    string FileName);

public sealed record SetupHelperRequest(
    int Version,
    Guid RequestId,
    string Operation,
    string? StoragePath = null,
    string? InstallerPath = null,
    string? ExpectedSha256 = null,
    string? ExpectedPublisher = null);

public sealed record SetupHelperResponse(
    int Version,
    Guid RequestId,
    bool IsSuccess,
    bool RebootRequired,
    IReadOnlyList<DiagnosticRecord> Diagnostics,
    string? ErrorCode = null);

public static class SetupHelperOperations
{
    public const string InspectHost = "inspectHost";
    public const string EnableHyperV = "enableHyperV";
    public const string InstallMultipass = "installMultipass";
    public const string ConfigureFreshStorage = "configureFreshStorage";

    public static readonly ISet<string> Allowed = new HashSet<string>(StringComparer.Ordinal)
    {
        InspectHost, EnableHyperV, InstallMultipass, ConfigureFreshStorage
    };
}
