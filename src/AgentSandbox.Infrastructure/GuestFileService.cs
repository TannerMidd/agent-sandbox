using System.Text.Json;
using AgentSandbox.Application;
using AgentSandbox.Domain;

namespace AgentSandbox.Infrastructure;

public sealed class GuestFileService(
    IProcessRunner runner,
    IMultipassLocator locator,
    string instanceName,
    string guestHelperPath) : IGuestFileService
{
    private const string RemoteHelper = "/home/ubuntu/.local/lib/agent-sandbox/guest_helper.py";
    private readonly SemaphoreSlim deploymentLock = new(1, 1);
    private bool deployed;

    public async Task<GuestFileResponse> ExecuteAsync(GuestFileRequest request, CancellationToken cancellationToken = default)
    {
        GuestPathPolicy.ValidateRequest(request);
        await EnsureDeployedAsync(cancellationToken);
        var json = JsonSerializer.Serialize(request);
        var result = await RunMultipassAsync(["exec", instanceName, "--", "python3", RemoteHelper], json, TimeSpan.FromMinutes(5), cancellationToken);
        GuestFileResponse? response;
        try { response = JsonSerializer.Deserialize<GuestFileResponse>(result.StandardOutput); }
        catch (JsonException exception) { throw new InvalidDataException("The guest helper returned malformed JSON.", exception); }
        return response ?? throw new InvalidDataException("The guest helper returned an empty response.");
    }

    public async Task<TransferJob> UploadAsync(
        IReadOnlyList<string> hostPaths,
        IReadOnlyList<string> guestDestination,
        FileConflictPolicy conflictPolicy,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        GuestPathPolicy.ValidateComponents(guestDestination);
        await EnsureDeployedAsync(cancellationToken);
        var jobId = Guid.NewGuid();
        var started = DateTimeOffset.UtcNow;
        var results = new List<TransferItemResult>();
        for (var index = 0; index < hostPaths.Count; index++)
        {
            var source = ValidateHostSource(hostPaths[index]);
            var isDirectory = Directory.Exists(source);
            var fileName = ValidateWindowsName(Path.GetFileName(source));
            var stageParts = new[] { ".agent-sandbox", "staging", jobId.ToString("N"), fileName };
            var destination = guestDestination.Concat([fileName]).ToArray();
            Report(progress, jobId, "Upload files", OperationState.Running, $"Uploading {fileName}", index, hostPaths.Count);
            try
            {
                await RunMultipassAsync(["exec", instanceName, "--", "mkdir", "-p", $"/home/ubuntu/work/.agent-sandbox/staging/{jobId:N}"], null, TimeSpan.FromMinutes(2), cancellationToken);
                var transferArguments = isDirectory
                    ? new[] { "transfer", "--recursive", source, $"{instanceName}:/home/ubuntu/work/{string.Join('/', stageParts)}" }
                    : new[] { "transfer", source, $"{instanceName}:/home/ubuntu/work/{string.Join('/', stageParts)}" };
                await RunMultipassAsync(transferArguments, null, TimeSpan.FromMinutes(30), cancellationToken);
                var response = await ExecuteAsync(new GuestFileRequest
                {
                    Operation = "move", RelativePath = stageParts, DestinationPath = destination,
                    Conflict = ConflictName(conflictPolicy)
                }, cancellationToken);
                if (!response.IsSuccess) throw new IOException(response.Error?.Message ?? "The guest rejected the upload.");
                results.Add(new TransferItemResult([source], destination, OperationState.Succeeded, SizeOf(source), null, null));
            }
            catch (OperationCanceledException)
            {
                results.Add(new TransferItemResult([source], destination, OperationState.CleanupPending, null, "CANCELED", "Staged data will be reconciled on the next launch."));
                break;
            }
            catch (Exception exception)
            {
                results.Add(new TransferItemResult([source], destination, OperationState.Failed, null, "UPLOAD_FAILED", exception.Message));
            }
        }
        var state = Aggregate(results);
        Report(progress, jobId, "Upload files", state, state == OperationState.Succeeded ? "Complete" : "Finished with issues", results.Count, hostPaths.Count);
        return new TransferJob(jobId, true, conflictPolicy, state, results, started, DateTimeOffset.UtcNow);
    }

    public async Task<TransferJob> DownloadAsync(
        IReadOnlyList<IReadOnlyList<string>> guestPaths,
        string hostDestination,
        FileConflictPolicy conflictPolicy,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var destinationDirectory = ValidateHostDestination(hostDestination);
        var jobId = Guid.NewGuid();
        var started = DateTimeOffset.UtcNow;
        var results = new List<TransferItemResult>();
        for (var index = 0; index < guestPaths.Count; index++)
        {
            var sourceParts = guestPaths[index];
            GuestPathPolicy.ValidateComponents(sourceParts);
            var name = ValidateWindowsName(sourceParts.Last());
            var finalPath = ResolveHostConflict(Path.Combine(destinationDirectory, name), conflictPolicy);
            var partialPath = finalPath + $".{jobId:N}.partial";
            Report(progress, jobId, "Download files", OperationState.Running, $"Downloading {name}", index, guestPaths.Count);
            try
            {
                var inspected = await ExecuteAsync(new GuestFileRequest { Operation = "download", RelativePath = sourceParts }, cancellationToken);
                var source = inspected.Entries.SingleOrDefault() ?? throw new FileNotFoundException("The guest source no longer exists.");
                if (source.Kind == "file")
                {
                    await RunMultipassAsync(["transfer", $"{instanceName}:/home/ubuntu/work/{string.Join('/', sourceParts)}", partialPath], null, TimeSpan.FromMinutes(30), cancellationToken);
                    if (new FileInfo(partialPath).Length != source.Size) throw new IOException("The downloaded file size did not match the inspected source.");
                    CommitHostFile(partialPath, finalPath, conflictPolicy, jobId);
                }
                else if (source.Kind == "directory")
                {
                    await RunMultipassAsync(["transfer", "--recursive", $"{instanceName}:/home/ubuntu/work/{string.Join('/', sourceParts)}", partialPath], null, TimeSpan.FromMinutes(30), cancellationToken);
                    ValidateDownloadedTree(partialPath);
                    CommitHostDirectory(partialPath, finalPath, conflictPolicy, jobId);
                }
                else throw new NotSupportedException("Only regular files and directories can be downloaded.");
                results.Add(new TransferItemResult(sourceParts, [finalPath], OperationState.Succeeded, source.Size, null, null));
            }
            catch (OperationCanceledException)
            {
                TryDelete(partialPath);
                results.Add(new TransferItemResult(sourceParts, [finalPath], OperationState.Canceled, null, "CANCELED", null));
                break;
            }
            catch (Exception exception)
            {
                TryDelete(partialPath);
                results.Add(new TransferItemResult(sourceParts, [finalPath], OperationState.Failed, null, "DOWNLOAD_FAILED", exception.Message));
            }
        }
        var state = Aggregate(results);
        Report(progress, jobId, "Download files", state, state == OperationState.Succeeded ? "Complete" : "Finished with issues", results.Count, guestPaths.Count);
        return new TransferJob(jobId, false, conflictPolicy, state, results, started, DateTimeOffset.UtcNow);
    }

    public async Task ReconcileAsync(CancellationToken cancellationToken = default) =>
        _ = await ExecuteAsync(new GuestFileRequest { Operation = "list", RelativePath = [], Content = "reconcile" }, cancellationToken);

    private async Task EnsureDeployedAsync(CancellationToken cancellationToken)
    {
        if (deployed) return;
        await deploymentLock.WaitAsync(cancellationToken);
        try
        {
            if (deployed) return;
            if (!File.Exists(guestHelperPath)) throw new FileNotFoundException("The bundled guest helper was not found.", guestHelperPath);
            await RunMultipassAsync(["exec", instanceName, "--", "mkdir", "-p", "/home/ubuntu/.local/lib/agent-sandbox"], null, TimeSpan.FromMinutes(2), cancellationToken);
            await RunMultipassAsync(["transfer", Path.GetFullPath(guestHelperPath), $"{instanceName}:{RemoteHelper}"], null, TimeSpan.FromMinutes(5), cancellationToken);
            deployed = true;
        }
        finally { deploymentLock.Release(); }
    }

    private async Task<ProcessResult> RunMultipassAsync(IReadOnlyList<string> arguments, string? input, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var executable = locator.Locate() ?? throw new FileNotFoundException("A verified Canonical Multipass executable was not found.");
        var result = await runner.RunAsync(executable, arguments, input, timeout, cancellationToken);
        if (!result.IsSuccess && string.IsNullOrWhiteSpace(result.StandardOutput))
            throw new IOException(string.IsNullOrWhiteSpace(result.StandardError) ? "Multipass operation failed." : result.StandardError.Trim());
        return result;
    }

    private static string ValidateHostSource(string candidate)
    {
        var path = Path.GetFullPath(candidate);
        if (!File.Exists(path) && !Directory.Exists(path)) throw new FileNotFoundException("Upload source was not found.", path);
        if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Reparse-point upload sources are not allowed.");
        for (var parent = Directory.GetParent(path); parent is not null; parent = parent.Parent)
            if (parent.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Reparse-point parent directories are not allowed.");
        if (Directory.Exists(path)) ValidateDownloadedTree(path);
        return path;
    }

    private static string ValidateHostDestination(string candidate)
    {
        var path = Path.GetFullPath(candidate);
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException("The host destination does not exist.");
        for (var parent = new DirectoryInfo(path); parent is not null; parent = parent.Parent)
            if (parent.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Reparse-point destination directories are not allowed.");
        return path;
    }

    private static string ValidateWindowsName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.EndsWith(' ') || name.EndsWith('.'))
            throw new IOException("The item name is not supported on Windows.");
        var stem = name.Split('.')[0];
        string[] reserved = ["CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"];
        if (reserved.Contains(stem, StringComparer.OrdinalIgnoreCase)) throw new IOException("The item uses a reserved Windows name.");
        return name;
    }

    private static string ResolveHostConflict(string path, FileConflictPolicy policy)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return path;
        if (policy == FileConflictPolicy.Fail) throw new IOException("The host destination already exists.");
        if (policy == FileConflictPolicy.Overwrite) return path;
        var directory = Path.GetDirectoryName(path)!; var stem = Path.GetFileNameWithoutExtension(path); var extension = Path.GetExtension(path);
        for (var number = 1; number < 10_000; number++)
        {
            var candidate = Path.Combine(directory, $"{stem} ({number}){extension}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
        throw new IOException("No available conflict-free host name was found.");
    }

    private static string ConflictName(FileConflictPolicy policy) => policy.ToString().ToLowerInvariant();
    private static OperationState Aggregate(IReadOnlyList<TransferItemResult> items) => items.Count > 0 && items.All(item => item.State == OperationState.Succeeded) ? OperationState.Succeeded : items.Any(item => item.State == OperationState.CleanupPending) ? OperationState.CleanupPending : OperationState.Failed;
    private static void ValidateDownloadedTree(string path)
    {
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException("The transferred directory was not created.");
        foreach (var entry in Directory.EnumerateFileSystemEntries(path, "*", SearchOption.AllDirectories))
        {
            _ = ValidateWindowsName(Path.GetFileName(entry));
            if (File.GetAttributes(entry).HasFlag(FileAttributes.ReparsePoint))
                throw new IOException("Transferred directories cannot contain reparse points.");
        }
    }

    private static long SizeOf(string path) => File.Exists(path)
        ? new FileInfo(path).Length
        : Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Sum(file => new FileInfo(file).Length);

    private static void CommitHostFile(string partialPath, string finalPath, FileConflictPolicy policy, Guid jobId)
    {
        if (!File.Exists(finalPath) && !Directory.Exists(finalPath)) { File.Move(partialPath, finalPath); return; }
        if (policy != FileConflictPolicy.Overwrite || Directory.Exists(finalPath)) throw new IOException("The exact host destination cannot be overwritten with this item type.");
        var backup = finalPath + $".{jobId:N}.backup";
        File.Replace(partialPath, finalPath, backup, ignoreMetadataErrors: true);
        TryDelete(backup);
    }

    private static void CommitHostDirectory(string partialPath, string finalPath, FileConflictPolicy policy, Guid jobId)
    {
        if (!File.Exists(finalPath) && !Directory.Exists(finalPath)) { Directory.Move(partialPath, finalPath); return; }
        if (policy != FileConflictPolicy.Overwrite || File.Exists(finalPath)) throw new IOException("The exact host destination cannot be overwritten with this item type.");
        var backup = finalPath + $".{jobId:N}.backup";
        Directory.Move(finalPath, backup);
        try { Directory.Move(partialPath, finalPath); }
        catch { if (!Directory.Exists(finalPath) && Directory.Exists(backup)) Directory.Move(backup, finalPath); throw; }
        TryDelete(backup);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
            else if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch { }
    }
    private static void Report(IProgress<OperationProgress>? progress, Guid id, string title, OperationState state, string phase, int completed, int total) =>
        progress?.Report(new OperationProgress(id, title, state, phase, total == 0 ? 100 : completed * 100 / total, completed, total, null, null, DateTimeOffset.UtcNow));
}
