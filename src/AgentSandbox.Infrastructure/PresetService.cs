using System.Text.Json;
using AgentSandbox.Application;
using AgentSandbox.Domain;

namespace AgentSandbox.Infrastructure;

public sealed class PresetService(IProcessRunner runner, IMultipassLocator locator, string manifestDirectory) : IPresetService
{
    public async Task<IReadOnlyList<AgentPresetManifest>> GetAvailableAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(manifestDirectory)) return [];
        var manifests = new List<AgentPresetManifest>();
        foreach (var file in Directory.EnumerateFiles(manifestDirectory, "*.json").Order(StringComparer.Ordinal))
        {
            await using var stream = File.OpenRead(file);
            var manifest = await JsonSerializer.DeserializeAsync<AgentPresetManifest>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, cancellationToken);
            if (manifest is null || manifest.SchemaVersion != 1 || manifest.Artifacts.Count != 1)
                throw new InvalidDataException($"Preset manifest '{Path.GetFileName(file)}' is invalid.");
            manifests.Add(manifest);
        }
        return manifests;
    }

    public async Task<OperationProgress> InstallAsync(
        string instanceName,
        IReadOnlyList<string> presetIds,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var operationId = Guid.NewGuid();
        var manifests = await GetAvailableAsync(cancellationToken);
        for (var index = 0; index < presetIds.Count; index++)
        {
            var manifest = manifests.SingleOrDefault(item => string.Equals(item.Id, presetIds[index], StringComparison.Ordinal))
                ?? throw new InvalidOperationException($"Unknown preset '{presetIds[index]}'.");
            var artifact = manifest.Artifacts.Single();
            if (artifact.InstallKind != "npm" || !artifact.Source.StartsWith("npm:", StringComparison.Ordinal) || !artifact.Integrity.StartsWith("sha512-", StringComparison.Ordinal))
                throw new InvalidDataException($"Preset '{manifest.Id}' does not have an approved npm artifact.");
            var package = artifact.Source[4..];
            progress?.Report(Progress(operationId, OperationState.Running, $"Installing {manifest.DisplayName}", index, presetIds.Count));
            var metadata = await RunAsync(instanceName, ["npm", "view", package, "dist.integrity", "--json"], TimeSpan.FromMinutes(2), cancellationToken);
            var publishedIntegrity = JsonSerializer.Deserialize<string>(metadata.StandardOutput.Trim());
            if (!string.Equals(publishedIntegrity, artifact.Integrity, StringComparison.Ordinal))
                throw new InvalidDataException($"Preset '{manifest.Id}' registry integrity did not match its pinned manifest.");
            await RunAsync(instanceName, ["npm", "install", "--global", "--prefix", "/home/ubuntu/.local", "--ignore-scripts=false", "--audit=false", "--fund=false", package], TimeSpan.FromMinutes(15), cancellationToken);
            await RunAsync(instanceName, ["/home/ubuntu/.local/bin/" + manifest.Executable, "--version"], TimeSpan.FromMinutes(2), cancellationToken);
        }
        return Progress(operationId, OperationState.Succeeded, "Preset installation complete", presetIds.Count, presetIds.Count);
    }

    private async Task<ProcessResult> RunAsync(string instanceName, IReadOnlyList<string> command, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var executable = locator.Locate() ?? throw new FileNotFoundException("Multipass was not found.");
        var arguments = new List<string> { "exec", instanceName, "--" };
        arguments.AddRange(command);
        var result = await runner.RunAsync(executable, arguments, timeout: timeout, cancellationToken: cancellationToken);
        if (!result.IsSuccess) throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput.Trim() : result.StandardError.Trim());
        return result;
    }

    private static OperationProgress Progress(Guid id, OperationState state, string phase, int completed, int total) =>
        new(id, "Install agent presets", state, phase, total == 0 ? 100 : completed * 100 / total, completed, total, null, null, DateTimeOffset.UtcNow);
}
