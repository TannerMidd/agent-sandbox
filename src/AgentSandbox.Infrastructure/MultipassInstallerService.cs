using System.Security.Cryptography;
using AgentSandbox.Application;
using AgentSandbox.Domain;

namespace AgentSandbox.Infrastructure;

public sealed class MultipassInstallerService(HttpClient httpClient, string? downloadDirectory = null) : IMultipassInstallerService
{
    public MultipassInstallerRelease Release { get; } = new(
        new Version(1, 16, 3),
        new Uri("https://github.com/canonical/multipass/releases/download/v1.16.3/multipass-1.16.3+win-win64.msi"),
        "F5BFF63D13FB1377A72B8DD6D277BBDD3369B1F278F4C85D2C8427A2E7D38D39",
        "Canonical",
        "multipass-1.16.3+win-win64.msi");

    public async Task<string> DownloadAsync(IProgress<OperationProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var id = Guid.NewGuid();
        var directory = downloadDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentSandbox", "Downloads");
        Directory.CreateDirectory(directory);
        var finalPath = Path.Combine(directory, Release.FileName);
        var partialPath = finalPath + ".partial";

        if (File.Exists(finalPath) && await HasExpectedHashAsync(finalPath, cancellationToken))
            return finalPath;

        TryDelete(partialPath);
        progress?.Report(Progress(id, OperationState.Running, "Downloading verified Canonical Multipass", 0, null));
        using var response = await httpClient.GetAsync(Release.DownloadUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength;
        await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
        await using (var destination = new FileStream(partialPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 128, FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            var buffer = new byte[1024 * 128];
            long completed = 0;
            while (true)
            {
                var count = await source.ReadAsync(buffer, cancellationToken);
                if (count == 0) break;
                await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                completed += count;
                int? percent = total is > 0 ? (int)Math.Clamp(completed * 100 / total.Value, 0, 100) : null;
                progress?.Report(Progress(id, OperationState.Running, "Downloading verified Canonical Multipass", percent, completed));
            }
        }

        if (!await HasExpectedHashAsync(partialPath, cancellationToken))
        {
            TryDelete(partialPath);
            throw new InvalidDataException("The downloaded Multipass installer did not match the pinned SHA-256 value.");
        }

        File.Move(partialPath, finalPath, overwrite: true);
        progress?.Report(Progress(id, OperationState.Succeeded, "Multipass download verified", 100, new FileInfo(finalPath).Length));
        return finalPath;
    }

    private async Task<bool> HasExpectedHashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
        return CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.ASCII.GetBytes(actual),
            System.Text.Encoding.ASCII.GetBytes(Release.Sha256));
    }

    private static OperationProgress Progress(Guid id, OperationState state, string phase, int? percent, long? bytes) =>
        new(id, "Install Multipass", state, phase, percent, bytes, null, null, null, DateTimeOffset.UtcNow);

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
