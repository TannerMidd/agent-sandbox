using System.Collections.ObjectModel;
using System.IO.Compression;
using System.Text.Json;
using AgentSandbox.Application;
using AgentSandbox.Domain;
using AgentSandbox.Infrastructure;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AgentSandbox.App.ViewModels;

public sealed record HostFileItem(string Name, string Kind, string Detail, string FullPath);

public partial class MainPageViewModel : ObservableObject, IDisposable
{
    private static readonly JsonSerializerOptions IndentedJsonOptions = new() { WriteIndented = true };
    private readonly AppServices services = App.Services;
    private AgentSandboxSettings settings = new();
    private SetupState currentSetupState = SetupState.Welcome;
    private readonly List<string> guestPathComponents = [];
    private string guestRootId = GuestRoots.Work;
    private string guestQuery = "";
    private bool showHiddenGuest;
    private CancellationTokenSource? transferCancellation;

    [ObservableProperty] public partial string PageTitle { get; set; } = "Dashboard";
    [ObservableProperty] public partial string SandboxStatus { get; set; } = "Checking environment";
    [ObservableProperty] public partial string SandboxDetail { get; set; } = "Agent Sandbox is inspecting Windows and Multipass.";
    [ObservableProperty] public partial string SetupHeading { get; set; } = "Finish setting up your sandbox";
    [ObservableProperty] public partial string SetupDetail { get; set; } = "The guided setup checks prerequisites and resumes safely after a restart.";
    [ObservableProperty] public partial string SetupActionLabel { get; set; } = "Continue setup";
    [ObservableProperty] public partial Visibility SetupVisibility { get; set; } = Visibility.Visible;
    [ObservableProperty] public partial Visibility ErrorVisibility { get; set; } = Visibility.Collapsed;
    [ObservableProperty] public partial string ErrorMessage { get; set; } = "";
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial bool CanOperateSandbox { get; set; }
    [ObservableProperty] public partial string HostPath { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    [ObservableProperty] public partial string GuestPath { get; set; } = "/home/ubuntu/work";
    [ObservableProperty] public partial string CpuLabel { get; set; } = "—";
    [ObservableProperty] public partial string MemoryLabel { get; set; } = "—";
    [ObservableProperty] public partial string DiskLabel { get; set; } = "—";
    [ObservableProperty] public partial string OperationLabel { get; set; } = "No active operation";
    [ObservableProperty] public partial bool HasActiveTransfer { get; set; }
    [ObservableProperty] public partial string GuestConnectionStatus { get; set; } = "Connection not tested";
    [ObservableProperty] public partial string GuestConnectionDetail { get; set; } = "Start the sandbox, then test the guest connection.";
    [ObservableProperty] public partial InfoBarSeverity GuestConnectionSeverity { get; set; } = InfoBarSeverity.Informational;

    public ObservableCollection<HostFileItem> HostEntries { get; } = [];
    public ObservableCollection<GuestFileEntry> GuestEntries { get; } = [];
    public ObservableCollection<SnapshotInfo> Snapshots { get; } = [];
    public ObservableCollection<DiagnosticRecord> Diagnostics { get; } = [];
    public ObservableCollection<AgentPresetManifest> Presets { get; } = [];
    public ObservableCollection<string> TransferActivity { get; } = [];
    public event EventHandler? SetupRequested;

    public AgentSandboxSettings CurrentSettings => settings;
    public SetupState CurrentSetupState => currentSetupState;
    public IReadOnlyList<string> GuestPathComponents => guestPathComponents;
    public string GuestRootId => guestRootId;

    [RelayCommand]
    public async Task InitializeAsync()
    {
        await RunBusyAsync(async () =>
        {
            settings = await services.Lifecycle.ResumeSetupAsync();
            currentSetupState = settings.SetupState;
            await ApplySetupPresentationAsync();
            CpuLabel = $"{settings.Resources.CpuCount} vCPU";
            MemoryLabel = $"{settings.Resources.MemoryGiB} GiB";
            DiskLabel = $"{settings.Resources.DiskGiB} GiB";
            await LoadPresetsAsync();
            await RefreshSandboxCoreAsync();
            LoadHostFiles();
        });
    }

    [RelayCommand]
    public async Task RefreshSandboxAsync()
    {
        await RunBusyAsync(RefreshSandboxCoreAsync);
    }

    [RelayCommand] public Task StartAsync() => RunOperationAsync(() => services.Multipass.StartAsync(settings.InstanceName), "Starting sandbox");
    [RelayCommand] public Task StopAsync() => RunOperationAsync(() => services.Multipass.StopAsync(settings.InstanceName), "Stopping sandbox");

    [RelayCommand]
    public async Task OpenTerminalAsync()
    {
        await RunBusyAsync(async () =>
        {
            var operationId = Guid.NewGuid();
            try
            {
                if (!await ProbeGuestConnectionAsync())
                    throw new InvalidOperationException($"{GuestConnectionStatus}: {GuestConnectionDetail}");
                await services.Terminal.OpenExternalAsync(settings.InstanceName);
                OperationLabel = $"Windows Terminal launched for {settings.InstanceName}";
                await services.History.AppendAsync(new OperationProgress(operationId, "Open Windows Terminal", OperationState.Succeeded,
                    "Terminal launcher started", 100, null, null, null, settings.InstanceName, DateTimeOffset.UtcNow));
            }
            catch (Exception exception)
            {
                await services.History.AppendAsync(new OperationProgress(operationId, "Open Windows Terminal", OperationState.Failed,
                    "Terminal launch failed", null, null, null, "TERMINAL_LAUNCH_FAILED", exception.Message, DateTimeOffset.UtcNow));
                throw;
            }
        });
    }

    [RelayCommand]
    public async Task TestConnectionAsync()
    {
        await RunBusyAsync(async () =>
        {
            GuestConnectionStatus = "Testing guest connection";
            GuestConnectionDetail = $"Contacting {settings.InstanceName} and checking /home/ubuntu/work…";
            GuestConnectionSeverity = InfoBarSeverity.Informational;
            if (await ProbeGuestConnectionAsync()) OperationLabel = $"Connected to {settings.InstanceName}";
        });
    }

    [RelayCommand]
    public async Task LoadGuestFilesAsync()
    {
        await RunBusyAsync(LoadGuestFilesCoreAsync);
    }

    [RelayCommand]
    public async Task CreateSnapshotAsync()
    {
        await RunOperationAsync(
            () => services.Multipass.CreateSnapshotAsync(settings.InstanceName, $"manual-{DateTimeOffset.Now:yyyyMMdd-HHmmss}"),
            "Creating a recoverable snapshot");
    }

    [RelayCommand]
    public void ContinueSetup() => SetupRequested?.Invoke(this, EventArgs.Empty);

    public async Task<HostReadiness> InspectHostAsync() => await services.Prerequisites.InspectAsync();

    public async Task<SetupHelperResponse> EnableHyperVAsync()
    {
        var response = await services.Prerequisites.ExecuteElevatedAsync(
            new SetupHelperRequest(1, Guid.NewGuid(), SetupHelperOperations.EnableHyperV));
        if (!response.IsSuccess) throw new InvalidOperationException(FirstDiagnosticOr(response, "Hyper-V could not be enabled."));
        await ReloadSetupAsync();
        return response;
    }

    public async Task InstallMultipassAsync(string? storagePath, IProgress<OperationProgress>? progress = null)
    {
        if (!string.IsNullOrWhiteSpace(storagePath))
        {
            var storage = await services.Prerequisites.ExecuteElevatedAsync(
                new SetupHelperRequest(1, Guid.NewGuid(), SetupHelperOperations.ConfigureFreshStorage, StoragePath: storagePath));
            if (!storage.IsSuccess) throw new InvalidOperationException(FirstDiagnosticOr(storage, "The storage directory could not be configured."));
            settings = settings with { StoragePath = storagePath };
            await services.Settings.SaveAsync(settings);
        }

        var installerPath = await services.MultipassInstaller.DownloadAsync(progress);
        var release = services.MultipassInstaller.Release;
        var response = await services.Prerequisites.ExecuteElevatedAsync(new SetupHelperRequest(
            1, Guid.NewGuid(), SetupHelperOperations.InstallMultipass,
            InstallerPath: installerPath, ExpectedSha256: release.Sha256, ExpectedPublisher: release.Publisher));
        if (!response.IsSuccess) throw new InvalidOperationException(FirstDiagnosticOr(response, "Multipass installation failed."));
        settings = settings with { SetupState = response.RebootRequired ? SetupState.RebootRequired : SetupState.CheckingHost };
        await services.Settings.SaveAsync(settings);
        await ReloadSetupAsync();
    }

    public async Task<LegacyImportCandidate?> InspectLegacyImportAsync() => await services.Lifecycle.InspectLegacyImportAsync();

    public async Task ImportLegacyAsync(LegacyImportCandidate candidate)
    {
        settings = await services.Lifecycle.ImportLegacyAsync(candidate);
        await ReloadSetupAsync();
    }

    public async Task ProvisionAsync(ResourceProfile resources, IReadOnlyList<string> presetIds, IProgress<OperationProgress>? progress = null)
    {
        var result = await services.Lifecycle.ProvisionAsync(resources, presetIds, progress);
        await services.History.AppendAsync(result);
        if (result.State != OperationState.Succeeded) throw new InvalidOperationException(result.Detail ?? result.Phase);
        OperationLabel = result.Phase;
        await ReloadSetupAsync();
    }

    public async Task SavePreferencesAsync(string theme, bool reducedMotion, bool updates, bool advancedBrowsing, string? releaseRepository = null)
    {
        settings = settings with
        {
            Theme = theme,
            ReducedMotion = reducedMotion,
            CheckForUpdates = updates,
            AdvancedGuestBrowsing = advancedBrowsing,
            ReleaseRepository = string.IsNullOrWhiteSpace(releaseRepository) ? settings.ReleaseRepository : releaseRepository
        };
        await services.Settings.SaveAsync(settings);
    }

    public async Task<ReleaseInfo?> CheckForUpdateAsync(bool force = false)
    {
        if (!settings.CheckForUpdates || string.IsNullOrWhiteSpace(settings.ReleaseRepository)) return null;
        if (!force && settings.LastUpdateCheck is { } last && DateTimeOffset.UtcNow - last < TimeSpan.FromDays(1)) return null;
        var service = services.CreateReleaseService(settings.ReleaseRepository);
        var release = await service.CheckAsync(typeof(MainPageViewModel).Assembly.GetName().Version ?? new Version(0, 1), default);
        settings = settings with { LastUpdateCheck = DateTimeOffset.UtcNow };
        await services.Settings.SaveAsync(settings);
        return release;
    }

    public async Task NavigateGuestAsync(GuestFileEntry? entry)
    {
        if (entry is null) return;
        if (entry.Kind != "directory") throw new InvalidOperationException("Only folders can be opened.");
        guestPathComponents.Add(entry.Name);
        await LoadGuestFilesCoreAsync();
    }

    public async Task NavigateGuestUpAsync()
    {
        if (guestPathComponents.Count > 0) guestPathComponents.RemoveAt(guestPathComponents.Count - 1);
        await LoadGuestFilesCoreAsync();
    }

    public async Task SetGuestRootAsync(string rootId)
    {
        if (rootId == GuestRoots.System && !settings.AdvancedGuestBrowsing)
            throw new UnauthorizedAccessException("Enable read-only system browsing in Settings first.");
        if (rootId is not (GuestRoots.Work or GuestRoots.System)) throw new ArgumentException("Unknown guest root.", nameof(rootId));
        guestRootId = rootId;
        guestPathComponents.Clear();
        await LoadGuestFilesCoreAsync();
    }

    public async Task SearchGuestAsync(string query, bool showHidden)
    {
        guestQuery = query.Trim();
        showHiddenGuest = showHidden;
        await LoadGuestFilesCoreAsync();
    }

    public void NavigateHost(string path)
    {
        HostPath = Path.GetFullPath(path);
        LoadHostFiles();
    }

    public async Task UploadAsync(IReadOnlyList<string> hostPaths, FileConflictPolicy conflict = FileConflictPolicy.Fail)
    {
        if (guestRootId != GuestRoots.Work) throw new UnauthorizedAccessException("Uploads are limited to the guest workspace.");
        transferCancellation?.Dispose(); transferCancellation = new CancellationTokenSource(); HasActiveTransfer = true;
        try
        {
            var progress = new Progress<OperationProgress>(item => OperationLabel = item.Phase);
            var job = await services.CreateGuestFiles(settings.InstanceName).UploadAsync(hostPaths, guestPathComponents, conflict, progress, transferCancellation.Token);
            AddTransfer(job);
            await services.History.AppendAsync(TransferProgress(job, "Upload files"));
            await LoadGuestFilesCoreAsync();
        }
        finally { HasActiveTransfer = false; transferCancellation.Dispose(); transferCancellation = null; }
    }

    public async Task DownloadAsync(IReadOnlyList<GuestFileEntry> entries, string hostDestination, FileConflictPolicy conflict = FileConflictPolicy.Fail)
    {
        if (guestRootId != GuestRoots.Work) throw new UnauthorizedAccessException("Use the text viewer for read-only system files; bulk transfer is workspace-only.");
        var paths = entries.Select(item => (IReadOnlyList<string>)guestPathComponents.Concat([item.Name]).ToArray()).ToArray();
        transferCancellation?.Dispose(); transferCancellation = new CancellationTokenSource(); HasActiveTransfer = true;
        try
        {
            var progress = new Progress<OperationProgress>(item => OperationLabel = item.Phase);
            var job = await services.CreateGuestFiles(settings.InstanceName).DownloadAsync(paths, hostDestination, conflict, progress, transferCancellation.Token);
            AddTransfer(job);
            await services.History.AppendAsync(TransferProgress(job, "Download files"));
            NavigateHost(hostDestination);
        }
        finally { HasActiveTransfer = false; transferCancellation.Dispose(); transferCancellation = null; }
    }

    [RelayCommand]
    public void CancelTransfer()
    {
        transferCancellation?.Cancel();
        OperationLabel = "Canceling transfer and cleaning partial data";
    }

    public async Task<GuestFileResponse> GuestOperationAsync(GuestFileRequest request, bool refresh = true)
    {
        request = request with { RootId = guestRootId };
        var response = await services.CreateGuestFiles(settings.InstanceName).ExecuteAsync(request);
        if (!response.IsSuccess) throw new IOException(response.Error?.Message ?? "The guest operation failed.");
        if (refresh) await LoadGuestFilesCoreAsync();
        return response;
    }

    public IReadOnlyList<string> GuestItemPath(string name) => guestPathComponents.Concat([name]).ToArray();

    public async Task RestoreSnapshotAsync(SnapshotInfo snapshot)
    {
        var result = await services.Multipass.RestoreSnapshotAsync(settings.InstanceName, snapshot.Name);
        OperationLabel = result.Phase;
        await RefreshSandboxCoreAsync();
    }

    public async Task<string> ExportDiagnosticsAsync(string destinationDirectory)
    {
        var root = Path.Combine(Path.GetTempPath(), $"AgentSandbox-Diagnostics-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var readiness = await services.Prerequisites.InspectAsync();
            var diagnosticPath = Path.Combine(root, "diagnostics.json");
            var json = JsonSerializer.Serialize(readiness with { MultipassPath = readiness.MultipassPath is null ? null : "<redacted-path>" }, IndentedJsonOptions);
            await File.WriteAllTextAsync(diagnosticPath, DiagnosticRedactor.Redact(json));
            var history = await services.History.ReadRecentAsync();
            await File.WriteAllTextAsync(Path.Combine(root, "operations.json"), DiagnosticRedactor.Redact(JsonSerializer.Serialize(history, IndentedJsonOptions)));
            await File.WriteAllTextAsync(Path.Combine(root, "about.txt"), $"Agent Sandbox {typeof(MainPageViewModel).Assembly.GetName().Version}\nExported {DateTimeOffset.UtcNow:O}\nNo telemetry or credentials are included.\n");
            var output = Path.Combine(destinationDirectory, $"AgentSandbox-Diagnostics-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.zip");
            ZipFile.CreateFromDirectory(root, output, CompressionLevel.Optimal, includeBaseDirectory: false);
            return output;
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    public async Task ReloadSetupAsync()
    {
        settings = await services.Lifecycle.ResumeSetupAsync();
        currentSetupState = settings.SetupState;
        await ApplySetupPresentationAsync();
        CpuLabel = $"{settings.Resources.CpuCount} vCPU";
        MemoryLabel = $"{settings.Resources.MemoryGiB} GiB";
        DiskLabel = $"{settings.Resources.DiskGiB} GiB";
        await RefreshSandboxCoreAsync();
    }

    private async Task ApplySetupPresentationAsync()
    {
        SetupVisibility = settings.IsReady ? Visibility.Collapsed : Visibility.Visible;
        SetupHeading = SetupTitle(currentSetupState);
        SetupDetail = SetupDescription(currentSetupState);
        SetupActionLabel = SetupAction(currentSetupState);

        if (currentSetupState != SetupState.NeedsReview ||
            settings.ImportedLegacyInstance ||
            string.Equals(settings.InstanceName, "agent-dev", StringComparison.Ordinal))
            return;

        if (await InspectLegacyImportAsync() is null) return;
        SetupHeading = "Use your existing agent-dev sandbox";
        SetupDetail = "Import preserves its name, data, storage, and snapshots. Nothing is renamed, migrated, or rebuilt.";
        SetupActionLabel = "Import existing sandbox";
    }

    private async Task LoadPresetsAsync()
    {
        Presets.Clear();
        foreach (var preset in await services.Presets.GetAvailableAsync()) Presets.Add(preset);
    }

    private async Task LoadGuestFilesCoreAsync()
    {
        var response = await services.CreateGuestFiles(settings.InstanceName).ExecuteAsync(
            new GuestFileRequest { Operation = string.IsNullOrWhiteSpace(guestQuery) ? "list" : "search", RootId = guestRootId, RelativePath = guestPathComponents.ToArray(), Content = guestQuery });
        if (!response.IsSuccess) throw new IOException(response.Error?.Message ?? "The guest listing failed.");
        GuestEntries.Clear();
        foreach (var item in response.Entries.Where(item => showHiddenGuest || item.Name.Length == 0 || item.Name[0] != '.')) GuestEntries.Add(item);
        var root = guestRootId == GuestRoots.Work ? "/home/ubuntu/work" : "";
        GuestPath = root + "/" + string.Join('/', guestPathComponents);
        if (GuestPath.Length > 1) GuestPath = GuestPath.TrimEnd('/');
        OperationLabel = $"Loaded {response.Entries.Count} guest items";
    }

    private void AddTransfer(TransferJob job)
    {
        var direction = job.HostToGuest ? "Upload" : "Download";
        TransferActivity.Insert(0, $"{direction} • {job.State} • {job.Items.Count} item(s)");
        foreach (var item in job.Items.Reverse())
            TransferActivity.Insert(1, $"  {string.Join('/', item.SourcePath)} • {item.State}{(string.IsNullOrWhiteSpace(item.Detail) ? "" : " • " + item.Detail)}");
        while (TransferActivity.Count > 20) TransferActivity.RemoveAt(TransferActivity.Count - 1);
    }

    private static OperationProgress TransferProgress(TransferJob job, string title) => new(
        job.Id, title, job.State, job.State.ToString(),
        job.Items.Count == 0 ? 100 : job.Items.Count(item => item.State == OperationState.Succeeded) * 100 / job.Items.Count,
        job.Items.Sum(item => item.Bytes ?? 0), null,
        job.Items.FirstOrDefault(item => item.State != OperationState.Succeeded)?.ErrorCode,
        job.Items.FirstOrDefault(item => item.State != OperationState.Succeeded)?.Detail,
        job.UpdatedAt);

    private async Task RefreshDiagnosticsCoreAsync()
    {
        Diagnostics.Clear();
        var readiness = await services.Prerequisites.InspectAsync();
        foreach (var item in readiness.Diagnostics) Diagnostics.Add(item);
        if (Diagnostics.Count == 0)
            Diagnostics.Add(new DiagnosticRecord("HOST_READY", "Host checks passed", DiagnosticSeverity.Information, "Windows, virtualization, Hyper-V, and Multipass are ready."));
    }

    private void LoadHostFiles()
    {
        HostEntries.Clear();
        try
        {
            var directory = new DirectoryInfo(HostPath);
            foreach (var child in directory.EnumerateDirectories().OrderBy(item => item.Name).Take(100))
                HostEntries.Add(new HostFileItem(child.Name, "Folder", "Folder", child.FullName));
            foreach (var child in directory.EnumerateFiles().OrderBy(item => item.Name).Take(100 - HostEntries.Count))
                HostEntries.Add(new HostFileItem(child.Name, "File", FormatSize(child.Length), child.FullName));
        }
        catch (Exception exception) { ShowError(exception); }
    }

    private async Task RunOperationAsync(Func<Task<OperationProgress>> action, string label)
    {
        await RunBusyAsync(async () =>
        {
            OperationLabel = label;
            var result = await action();
            OperationLabel = result.Phase;
            await services.History.AppendAsync(result);
            await RefreshSandboxCoreAsync();
        });
    }

    private async Task RefreshSandboxCoreAsync()
    {
        SandboxInfo? sandbox;
        try
        {
            sandbox = await services.Multipass.GetSandboxAsync(settings.InstanceName);
        }
        catch (FileNotFoundException) when (!settings.IsReady)
        {
            sandbox = null;
        }
        SandboxStatus = sandbox?.State.ToString() ?? "Not provisioned";
        CanOperateSandbox = sandbox is not null;
        SandboxDetail = sandbox is null ? "Complete setup to create the Ubuntu 24.04 development VM." : $"{sandbox.InstanceName} • Ubuntu {sandbox.UbuntuRelease ?? "24.04"} • {sandbox.IPv4Address ?? "No IP yet"}";
        if (sandbox is null)
        {
            GuestConnectionStatus = "Not connected";
            GuestConnectionDetail = $"The exact sandbox '{settings.InstanceName}' was not found.";
            GuestConnectionSeverity = InfoBarSeverity.Warning;
        }
        Snapshots.Clear();
        if (sandbox is not null)
        {
            if (sandbox.State == SandboxState.Running)
                await ProbeGuestConnectionAsync();
            else
            {
                GuestConnectionStatus = $"Guest is {sandbox.State.ToString().ToLowerInvariant()}";
                GuestConnectionDetail = "Start the sandbox to test the workspace and open a terminal.";
                GuestConnectionSeverity = InfoBarSeverity.Warning;
            }
            foreach (var item in await services.Multipass.ListSnapshotsAsync(settings.InstanceName)) Snapshots.Add(item);
        }
        await RefreshDiagnosticsCoreAsync();
    }

    private async Task<bool> ProbeGuestConnectionAsync()
    {
        try
        {
            var sandbox = await services.Multipass.GetSandboxAsync(settings.InstanceName);
            if (sandbox is null)
                throw new InvalidOperationException($"The exact sandbox '{settings.InstanceName}' was not found.");
            if (sandbox.State != SandboxState.Running)
                throw new InvalidOperationException($"The sandbox is {sandbox.State.ToString().ToLowerInvariant()}. Start it before connecting.");
            await services.CreateGuestFiles(settings.InstanceName).ReconcileAsync();
            GuestConnectionStatus = $"Connected to {settings.InstanceName}";
            GuestConnectionDetail = $"Ubuntu {sandbox.UbuntuRelease ?? "24.04"} responded and /home/ubuntu/work is accessible at {sandbox.IPv4Address ?? "its Multipass address"}.";
            GuestConnectionSeverity = InfoBarSeverity.Success;
            return true;
        }
        catch (Exception exception)
        {
            GuestConnectionStatus = "Guest connection failed";
            GuestConnectionDetail = exception.Message;
            GuestConnectionSeverity = InfoBarSeverity.Error;
            return false;
        }
    }

    private async Task RunBusyAsync(Func<Task> action)
    {
        if (IsBusy) return;
        IsBusy = true; ErrorVisibility = Visibility.Collapsed;
        try { await action(); }
        catch (Exception exception) { ShowError(exception); }
        finally { IsBusy = false; }
    }

    private void ShowError(Exception exception)
    {
        ErrorMessage = exception.Message;
        ErrorVisibility = Visibility.Visible;
        OperationLabel = "Action needs attention";
    }

    private static string SetupTitle(SetupState state) => state switch
    {
        SetupState.HyperVRequired => "Enable Hyper-V",
        SetupState.RebootRequired => "Restart Windows to continue",
        SetupState.MultipassRequired => "Install Canonical Multipass",
        SetupState.ResourceConfiguration => "Choose sandbox resources",
        SetupState.NeedsReview => "Review host compatibility",
        _ => "Finish setting up your sandbox"
    };

    private static string SetupDescription(SetupState state) => state switch
    {
        SetupState.HyperVRequired => "Agent Sandbox will request UAC only for the compiled Hyper-V setup operation.",
        SetupState.RebootRequired => "Setup state is saved and will resume after the restart.",
        SetupState.MultipassRequired => "An existing compatible installation is preserved. Fresh installs use a pinned, verified Canonical MSI.",
        SetupState.ResourceConfiguration => "Review the recommended CPU, memory, and disk allocation before provisioning.",
        SetupState.NeedsReview => "Open Diagnostics to see the exact compatibility issue and remediation.",
        _ => "The guided setup checks prerequisites and creates one isolated Ubuntu 24.04 VM."
    };

    private static string SetupAction(SetupState state) => state switch
    {
        SetupState.HyperVRequired => "Enable Hyper-V",
        SetupState.RebootRequired => "Restart Windows",
        SetupState.NeedsReview => "View diagnostics",
        SetupState.ResourceConfiguration => "Review resources",
        SetupState.MultipassRequired => "Install Multipass",
        _ => "Continue setup"
    };

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1_073_741_824 => $"{bytes / 1_073_741_824d:F1} GiB",
        >= 1_048_576 => $"{bytes / 1_048_576d:F1} MiB",
        >= 1024 => $"{bytes / 1024d:F1} KiB",
        _ => $"{bytes} B"
    };

    private static string FirstDiagnosticOr(SetupHelperResponse response, string fallback) =>
        response.Diagnostics.Count > 0 ? response.Diagnostics[0].Detail : fallback;

    public void Dispose()
    {
        transferCancellation?.Cancel();
        transferCancellation?.Dispose();
        transferCancellation = null;
        GC.SuppressFinalize(this);
    }
}
