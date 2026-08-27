using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentSandbox.Application;
using AgentSandbox.Domain;

namespace AgentSandbox.Infrastructure;

public sealed class GuestFileService(
    IProcessRunner runner,
    IMultipassLocator locator,
    string instanceName,
    string guestHelperPath,
    string? recoveryJournalPath = null) : IGuestFileService, IDisposable
{
    private const string RemoteHelper = "/home/ubuntu/.local/lib/agent-sandbox/guest_helper.py";
    private static readonly SemaphoreSlim RecoveryJournalLock = new(1, 1);
    private readonly SemaphoreSlim deploymentLock = new(1, 1);
    private readonly SemaphoreSlim transferLock = new(1, 1);
    private readonly string recoveryJournalPath = recoveryJournalPath ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentSandbox", "pending-transfers.json");
    private string JournalDirectory => Path.GetDirectoryName(recoveryJournalPath)
        ?? throw new InvalidOperationException("The recovery journal has no parent directory.");
    private string DirectoryCommitJournalPath => Path.Combine(JournalDirectory, "pending-directory-commits.json");
    private string TransferLeasePath => Path.Combine(JournalDirectory, "locks", "transfer.lock");
    private bool deployed;

    public async Task<GuestFileResponse> ExecuteAsync(GuestFileRequest request, CancellationToken cancellationToken = default)
    {
        GuestPathPolicy.ValidateRequest(request);
        await EnsureDeployedAsync(cancellationToken);
        var json = JsonSerializer.Serialize(request);
        var transportId = Guid.NewGuid().ToString("N");
        var localDirectory = Path.Combine(Path.GetTempPath(), "AgentSandbox", "requests");
        Directory.CreateDirectory(localDirectory);
        var localRequest = Path.Combine(localDirectory, transportId + ".json");
        var remoteRequest = $"/home/ubuntu/.local/lib/agent-sandbox/requests/{transportId}.json";
        ProcessResult result;
        try
        {
            await File.WriteAllTextAsync(localRequest, json, cancellationToken);
            await RunMultipassAsync(["transfer", localRequest, $"{instanceName}:{remoteRequest}"], null, TimeSpan.FromMinutes(5), cancellationToken);
            result = await RunMultipassAsync(["exec", instanceName, "--", "python3", RemoteHelper, "--request-file", remoteRequest], null, TimeSpan.FromMinutes(5), cancellationToken);
        }
        finally
        {
            TryDelete(localRequest);
            try { await RunMultipassAsync(["exec", instanceName, "--", "rm", "-f", remoteRequest], null, TimeSpan.FromSeconds(30), CancellationToken.None); }
            catch { }
        }
        GuestFileResponse? response;
        try { response = JsonSerializer.Deserialize<GuestFileResponse>(result.StandardOutput); }
        catch (JsonException exception) { throw new InvalidDataException("The guest helper returned malformed JSON.", exception); }
        if (response is null) throw new InvalidDataException("The guest helper returned an empty response.");
        if (response.Version != request.Version || response.Id != request.Id)
            throw new InvalidDataException("The guest helper response did not match the current request.");
        return response;
    }

    public async Task<TransferJob> UploadAsync(
        IReadOnlyList<string> hostPaths,
        IReadOnlyList<string> guestDestination,
        FileConflictPolicy conflictPolicy,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        GuestPathPolicy.ValidateComponents(guestDestination);
        await transferLock.WaitAsync(cancellationToken);
        try
        {
            await using var lease = await AcquireTransferLeaseAsync(cancellationToken);
            return await UploadCoreAsync(hostPaths, guestDestination, conflictPolicy, progress, cancellationToken);
        }
        finally { transferLock.Release(); }
    }

    private async Task<TransferJob> UploadCoreAsync(
        IReadOnlyList<string> hostPaths,
        IReadOnlyList<string> guestDestination,
        FileConflictPolicy conflictPolicy,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
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
        try { await RunMultipassAsync(["exec", instanceName, "--", "rm", "-rf", $"/home/ubuntu/work/.agent-sandbox/staging/{jobId:N}"], null, TimeSpan.FromSeconds(30), CancellationToken.None); }
        catch { }
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
        await transferLock.WaitAsync(cancellationToken);
        try
        {
            await using var lease = await AcquireTransferLeaseAsync(cancellationToken);
            return await DownloadCoreAsync(guestPaths, destinationDirectory, conflictPolicy, progress, cancellationToken);
        }
        finally { transferLock.Release(); }
    }

    private async Task<TransferJob> DownloadCoreAsync(
        IReadOnlyList<IReadOnlyList<string>> guestPaths,
        string destinationDirectory,
        FileConflictPolicy conflictPolicy,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        await EnsureDeployedAsync(cancellationToken);
        var jobId = Guid.NewGuid();
        var started = DateTimeOffset.UtcNow;
        var results = new List<TransferItemResult>();
        for (var index = 0; index < guestPaths.Count; index++)
        {
            var sourceParts = guestPaths[index];
            GuestPathPolicy.ValidateComponents(sourceParts);
            var name = ValidateWindowsName(sourceParts[^1]);
            var finalPath = ResolveHostConflict(Path.Combine(destinationDirectory, name), conflictPolicy);
            var partialPath = finalPath + $".{jobId:N}.partial";
            var stageJob = Guid.NewGuid().ToString("N");
            var stageParts = new[] { ".agent-sandbox", "staging", "downloads", stageJob, name };
            Report(progress, jobId, "Download files", OperationState.Running, $"Downloading {name}", index, guestPaths.Count);
            await RegisterPartialAsync(partialPath, cancellationToken);
            try
            {
                var inspected = await ExecuteAsync(new GuestFileRequest { Operation = "download", RelativePath = sourceParts }, cancellationToken);
                var source = inspected.Entries.SingleOrDefault() ?? throw new FileNotFoundException("The guest source no longer exists.");
                var staged = await ExecuteAsync(new GuestFileRequest
                {
                    Operation = "stageDownload",
                    RelativePath = sourceParts,
                    DestinationPath = stageParts,
                    Expected = new GuestFileExpectation(source.Kind, source.Size, source.ModifiedNanoseconds, source.Mode)
                }, cancellationToken);
                var expectedDigest = staged.Content;
                if (string.IsNullOrWhiteSpace(expectedDigest) || expectedDigest.Length != 64)
                    throw new InvalidDataException("The guest did not return a valid staged-content digest.");
                var stagedSource = $"{instanceName}:/home/ubuntu/work/{string.Join('/', stageParts)}";
                if (source.Kind == "file")
                {
                    await RunMultipassAsync(["transfer", stagedSource, partialPath], null, TimeSpan.FromMinutes(30), cancellationToken);
                    if (new FileInfo(partialPath).Length != source.Size) throw new IOException("The downloaded file size did not match the staged source.");
                    await VerifyDigestAsync(partialPath, expectedDigest, cancellationToken);
                    CommitHostFile(partialPath, finalPath, conflictPolicy);
                }
                else if (source.Kind == "directory")
                {
                    await RunMultipassAsync(["transfer", "--recursive", stagedSource, partialPath], null, TimeSpan.FromMinutes(30), cancellationToken);
                    ValidateDownloadedTree(partialPath);
                    await VerifyDigestAsync(partialPath, expectedDigest, cancellationToken);
                    await CommitHostDirectoryAsync(partialPath, finalPath, conflictPolicy, jobId, cancellationToken);
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
            finally
            {
                TryDelete(partialPath);
                await UnregisterPartialAsync(partialPath);
                try { await RunMultipassAsync(["exec", instanceName, "--", "rm", "-rf", $"/home/ubuntu/work/.agent-sandbox/staging/downloads/{stageJob}"], null, TimeSpan.FromSeconds(30), CancellationToken.None); }
                catch { }
            }
        }
        var state = Aggregate(results);
        Report(progress, jobId, "Download files", state, state == OperationState.Succeeded ? "Complete" : "Finished with issues", results.Count, guestPaths.Count);
        return new TransferJob(jobId, false, conflictPolicy, state, results, started, DateTimeOffset.UtcNow);
    }

    public async Task ReconcileAsync(CancellationToken cancellationToken = default)
    {
        await transferLock.WaitAsync(cancellationToken);
        try
        {
            await using var lease = await AcquireTransferLeaseAsync(cancellationToken);
            await ReconcileDirectoryCommitsAsync(cancellationToken);
            await ReconcileHostPartialsAsync(cancellationToken);
            _ = await ExecuteAsync(new GuestFileRequest { Operation = "list", RelativePath = [], Content = "reconcile" }, cancellationToken);
        }
        finally { transferLock.Release(); }
    }

    private async Task<FileStream> AcquireTransferLeaseAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(TransferLeasePath)!);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(TransferLeasePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
                    1, FileOptions.Asynchronous | FileOptions.WriteThrough);
            }
            catch (IOException)
            {
                await Task.Delay(100, cancellationToken);
            }
        }
    }

    private async Task EnsureDeployedAsync(CancellationToken cancellationToken)
    {
        if (deployed) return;
        await deploymentLock.WaitAsync(cancellationToken);
        try
        {
            if (deployed) return;
            if (!File.Exists(guestHelperPath)) throw new FileNotFoundException("The bundled guest helper was not found.", guestHelperPath);
            await RunMultipassAsync(["exec", instanceName, "--", "mkdir", "-p", "/home/ubuntu/.local/lib/agent-sandbox/requests"], null, TimeSpan.FromMinutes(2), cancellationToken);
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
    private static OperationState Aggregate(List<TransferItemResult> items) => items.Count > 0 && items.All(item => item.State == OperationState.Succeeded) ? OperationState.Succeeded : items.Any(item => item.State == OperationState.CleanupPending) ? OperationState.CleanupPending : OperationState.Failed;
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

    private async Task ReconcileDirectoryCommitsAsync(CancellationToken cancellationToken)
    {
        await RecoveryJournalLock.WaitAsync(cancellationToken);
        try
        {
            var transactions = await ReadDirectoryCommitsAsync(cancellationToken);
            var remaining = new List<HostDirectoryCommit>();
            foreach (var transaction in transactions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ValidateDirectoryCommit(transaction);
                try
                {
                    if (transaction.Phase == "prepared" && Directory.Exists(transaction.FinalPath))
                    {
                        TryDelete(transaction.PartialPath);
                    }
                    else if (Directory.Exists(transaction.FinalPath))
                    {
                        TryDelete(transaction.PartialPath);
                        TryDelete(transaction.BackupPath);
                    }
                    else if (Directory.Exists(transaction.PartialPath))
                    {
                        Directory.Move(transaction.PartialPath, transaction.FinalPath);
                        TryDelete(transaction.BackupPath);
                    }
                    else if (Directory.Exists(transaction.BackupPath))
                    {
                        Directory.Move(transaction.BackupPath, transaction.FinalPath);
                    }
                    if (Directory.Exists(transaction.PartialPath) || Directory.Exists(transaction.BackupPath)) remaining.Add(transaction);
                }
                catch
                {
                    remaining.Add(transaction);
                }
            }
            await WriteDirectoryCommitsWithoutLockAsync(remaining, cancellationToken);
        }
        finally { RecoveryJournalLock.Release(); }
    }

    private async Task<IReadOnlyList<HostDirectoryCommit>> ReadDirectoryCommitsAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(DirectoryCommitJournalPath)) return [];
        await using var stream = new FileStream(DirectoryCommitJournalPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        return await JsonSerializer.DeserializeAsync<HostDirectoryCommit[]>(stream, cancellationToken: cancellationToken)
            ?? throw new InvalidDataException("The directory-commit recovery journal is empty.");
    }

    private async Task WriteDirectoryCommitsAsync(IEnumerable<HostDirectoryCommit> transactions, CancellationToken cancellationToken)
    {
        await RecoveryJournalLock.WaitAsync(cancellationToken);
        try { await WriteDirectoryCommitsWithoutLockAsync(transactions, cancellationToken); }
        finally { RecoveryJournalLock.Release(); }
    }

    private async Task WriteDirectoryCommitsWithoutLockAsync(IEnumerable<HostDirectoryCommit> transactions, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(DirectoryCommitJournalPath)!;
        Directory.CreateDirectory(directory);
        var temporary = DirectoryCommitJournalPath + ".tmp";
        await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            await JsonSerializer.SerializeAsync(stream, transactions.ToArray(), cancellationToken: cancellationToken);
        File.Move(temporary, DirectoryCommitJournalPath, overwrite: true);
    }

    private static void ValidateDirectoryCommit(HostDirectoryCommit transaction)
    {
        ValidateGeneratedPartialPath(transaction.PartialPath);
        var final = Path.GetFullPath(transaction.FinalPath);
        var backup = Path.GetFullPath(transaction.BackupPath);
        if (transaction.Phase is not ("prepared" or "backedUp" or "committed") ||
            !backup.StartsWith(final + ".", StringComparison.OrdinalIgnoreCase) ||
            !backup.EndsWith(".backup", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("A directory-commit recovery transaction is invalid.");
    }

    private sealed record HostDirectoryCommit(string PartialPath, string FinalPath, string BackupPath, string Phase);

    private async Task RegisterPartialAsync(string path, CancellationToken cancellationToken)
    {
        ValidateGeneratedPartialPath(path);
        await RecoveryJournalLock.WaitAsync(cancellationToken);
        try
        {
            var paths = await ReadRecoveryJournalAsync(cancellationToken);
            if (paths.Add(Path.GetFullPath(path))) await WriteRecoveryJournalAsync(paths, cancellationToken);
        }
        finally { RecoveryJournalLock.Release(); }
    }

    private async Task UnregisterPartialAsync(string path)
    {
        await RecoveryJournalLock.WaitAsync();
        try
        {
            var paths = await ReadRecoveryJournalAsync(CancellationToken.None);
            if (paths.Remove(Path.GetFullPath(path))) await WriteRecoveryJournalAsync(paths, CancellationToken.None);
        }
        finally { RecoveryJournalLock.Release(); }
    }

    private async Task ReconcileHostPartialsAsync(CancellationToken cancellationToken)
    {
        await RecoveryJournalLock.WaitAsync(cancellationToken);
        try
        {
            var paths = await ReadRecoveryJournalAsync(cancellationToken);
            var remaining = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in paths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ValidateGeneratedPartialPath(path);
                TryDelete(path);
                if (File.Exists(path) || Directory.Exists(path)) remaining.Add(path);
            }
            await WriteRecoveryJournalAsync(remaining, cancellationToken);
        }
        finally { RecoveryJournalLock.Release(); }
    }

    private async Task<HashSet<string>> ReadRecoveryJournalAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(recoveryJournalPath)) return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var stream = new FileStream(recoveryJournalPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        var values = await JsonSerializer.DeserializeAsync<string[]>(stream, cancellationToken: cancellationToken)
            ?? throw new InvalidDataException("The pending-transfer recovery journal is empty.");
        return new HashSet<string>(values.Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);
    }

    private async Task WriteRecoveryJournalAsync(IEnumerable<string> paths, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(recoveryJournalPath) ?? throw new InvalidOperationException("The recovery journal has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporary = recoveryJournalPath + ".tmp";
        await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            await JsonSerializer.SerializeAsync(stream, paths.Order(StringComparer.OrdinalIgnoreCase).ToArray(), cancellationToken: cancellationToken);
        File.Move(temporary, recoveryJournalPath, overwrite: true);
    }

    private static void ValidateGeneratedPartialPath(string path)
    {
        var name = Path.GetFileName(Path.GetFullPath(path));
        const string suffix = ".partial";
        if (!name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("A transfer recovery path did not use the generated partial-file format.");
        var prefix = name[..^suffix.Length];
        var separator = prefix.LastIndexOf('.');
        var id = separator >= 0 ? prefix[(separator + 1)..] : "";
        if (id.Length != 32 || id.Any(character => !Uri.IsHexDigit(character)))
            throw new InvalidDataException("A transfer recovery path did not contain a valid job identifier.");
    }

    public static async Task<string> ComputeDigestAsync(string path, CancellationToken cancellationToken = default)
    {
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        if (File.Exists(path))
        {
            digest.AppendData("agent-sandbox-file-v1\0"u8);
            await AppendFileAsync(digest, path, cancellationToken);
            return Convert.ToHexString(digest.GetHashAndReset()).ToLowerInvariant();
        }
        if (!Directory.Exists(path)) throw new FileNotFoundException("Digest source was not found.", path);
        digest.AppendData("agent-sandbox-tree-v1\0"u8);
        var root = Path.GetFullPath(path);
        foreach (var child in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
                     .OrderBy(item => Convert.ToHexString(Encoding.UTF8.GetBytes(Path.GetRelativePath(root, item).Replace('\\', '/'))), StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(root, child).Replace('\\', '/');
            if (Directory.Exists(child))
            {
                digest.AppendData(Encoding.UTF8.GetBytes($"D\0{relative}\0"));
            }
            else if (File.Exists(child))
            {
                digest.AppendData(Encoding.UTF8.GetBytes($"F\0{relative}\0{new FileInfo(child).Length}\0"));
                await AppendFileAsync(digest, child, cancellationToken);
            }
            else throw new IOException("Digest trees may contain only regular files and directories.");
        }
        return Convert.ToHexString(digest.GetHashAndReset()).ToLowerInvariant();
    }

    private static async Task AppendFileAsync(IncrementalHash digest, string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[1024 * 1024];
        while (true)
        {
            var count = await stream.ReadAsync(buffer, cancellationToken);
            if (count == 0) return;
            digest.AppendData(buffer, 0, count);
        }
    }

    private static async Task VerifyDigestAsync(string path, string expected, CancellationToken cancellationToken)
    {
        var actual = await ComputeDigestAsync(path, cancellationToken);
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(actual), Encoding.ASCII.GetBytes(expected.ToLowerInvariant())))
            throw new IOException("The downloaded content digest did not match the immutable guest staging object.");
    }

    public void Dispose()
    {
        deploymentLock.Dispose();
        transferLock.Dispose();
        GC.SuppressFinalize(this);
    }

    private static void CommitHostFile(string partialPath, string finalPath, FileConflictPolicy policy)
    {
        if (!File.Exists(finalPath) && !Directory.Exists(finalPath)) { File.Move(partialPath, finalPath); return; }
        if (policy != FileConflictPolicy.Overwrite || Directory.Exists(finalPath)) throw new IOException("The exact host destination cannot be overwritten with this item type.");
        File.Replace(partialPath, finalPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
    }

    private async Task CommitHostDirectoryAsync(string partialPath, string finalPath, FileConflictPolicy policy, Guid jobId, CancellationToken cancellationToken)
    {
        if (!File.Exists(finalPath) && !Directory.Exists(finalPath)) { Directory.Move(partialPath, finalPath); return; }
        if (policy != FileConflictPolicy.Overwrite || File.Exists(finalPath)) throw new IOException("The exact host destination cannot be overwritten with this item type.");
        var backup = finalPath + $".{jobId:N}.backup";
        var transaction = new HostDirectoryCommit(partialPath, finalPath, backup, "prepared");
        await WriteDirectoryCommitsAsync([transaction], cancellationToken);
        Directory.Move(finalPath, backup);
        transaction = transaction with { Phase = "backedUp" };
        await WriteDirectoryCommitsAsync([transaction], CancellationToken.None);
        try
        {
            Directory.Move(partialPath, finalPath);
            transaction = transaction with { Phase = "committed" };
            await WriteDirectoryCommitsAsync([transaction], CancellationToken.None);
        }
        catch
        {
            if (!Directory.Exists(finalPath) && Directory.Exists(backup))
            {
                try
                {
                    Directory.Move(backup, finalPath);
                    await WriteDirectoryCommitsAsync([], CancellationToken.None);
                }
                catch
                {
                    // The last durable phase remains journaled for the next reconciliation.
                }
            }
            // If the final directory exists, the durable backedUp phase remains sufficient
            // to remove the backup without ever rolling back the committed content.
            throw;
        }
        TryDelete(backup);
        await WriteDirectoryCommitsAsync(Directory.Exists(backup) ? [transaction] : [], CancellationToken.None);
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
