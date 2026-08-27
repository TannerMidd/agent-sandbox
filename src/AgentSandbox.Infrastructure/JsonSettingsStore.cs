using System.Text.Json;
using AgentSandbox.Application;
using AgentSandbox.Domain;

namespace AgentSandbox.Infrastructure;

public sealed class JsonSettingsStore : ISettingsStore
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _path;

    public JsonSettingsStore(string? path = null)
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _path = path ?? Path.Combine(localAppData, "AgentSandbox", "settings.json");
    }

    public async Task<AgentSandboxSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_path)) return new AgentSandboxSettings();
        await using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        var settings = await JsonSerializer.DeserializeAsync<AgentSandboxSettings>(stream, Options, cancellationToken)
            ?? throw new InvalidDataException("Agent Sandbox settings are empty.");
        if (settings.SchemaVersion != AgentSandboxSettings.CurrentSchemaVersion)
            throw new InvalidDataException($"Unsupported settings schema version {settings.SchemaVersion}.");
        return settings;
    }

    public async Task SaveAsync(AgentSandboxSettings settings, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(_path) ?? throw new InvalidOperationException("Settings path has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporary = _path + ".tmp";
        await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            await JsonSerializer.SerializeAsync(stream, settings, Options, cancellationToken);
        File.Move(temporary, _path, overwrite: true);
    }
}
