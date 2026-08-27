using System.Text.Json;
using AgentSandbox.Application;
using AgentSandbox.Domain;

namespace AgentSandbox.Infrastructure;

public sealed class OperationHistoryStore : IOperationHistoryStore, IDisposable
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    private readonly string path;
    private readonly SemaphoreSlim writeLock = new(1, 1);

    public OperationHistoryStore(string? path = null)
    {
        this.path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentSandbox", "operations.jsonl");
    }

    public async Task AppendAsync(OperationProgress operation, CancellationToken cancellationToken = default)
    {
        await writeLock.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (File.Exists(path) && new FileInfo(path).Length > 2 * 1024 * 1024)
                File.Move(path, path + ".1", overwrite: true);
            var redacted = operation with { Detail = operation.Detail is null ? null : DiagnosticRedactor.Redact(operation.Detail) };
            await File.AppendAllTextAsync(path, JsonSerializer.Serialize(redacted, Options) + Environment.NewLine, cancellationToken);
        }
        finally { writeLock.Release(); }
    }

    public async Task<IReadOnlyList<OperationProgress>> ReadRecentAsync(int count = 100, CancellationToken cancellationToken = default)
    {
        if (count is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(count));
        if (!File.Exists(path)) return [];
        var lines = await File.ReadAllLinesAsync(path, cancellationToken);
        return lines.TakeLast(count).Select(line => JsonSerializer.Deserialize<OperationProgress>(line, Options))
            .Where(item => item is not null).Cast<OperationProgress>().ToArray();
    }

    public void Dispose()
    {
        writeLock.Dispose();
        GC.SuppressFinalize(this);
    }
}
