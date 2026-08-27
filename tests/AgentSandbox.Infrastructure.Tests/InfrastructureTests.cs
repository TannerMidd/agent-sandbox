using AgentSandbox.Domain;
using AgentSandbox.Infrastructure;
using AgentSandbox.Application;
using System.Text.Json;

namespace AgentSandbox.Infrastructure.Tests;

public sealed class InfrastructureTests
{
    [Fact]
    public async Task Settings_are_written_atomically_and_round_trip()
    {
        var directory = Path.Combine(Path.GetTempPath(), "AgentSandbox.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "settings.json");
            var store = new JsonSettingsStore(path);
            var expected = new AgentSandboxSettings { InstanceName = "agent-sandbox", Theme = "Dark", SetupState = SetupState.ResourceConfiguration };
            await store.SaveAsync(expected);
            var actual = await store.LoadAsync();
            Assert.Equal(expected.InstanceName, actual.InstanceName);
            Assert.Equal(expected.Theme, actual.Theme);
            Assert.Equal(expected.SetupState, actual.SetupState);
            Assert.Equal(expected.Resources, actual.Resources);
            Assert.Equal(expected.SelectedPresetIds, actual.SelectedPresetIds);
            Assert.False(File.Exists(path + ".tmp"));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task Process_runner_keeps_arguments_separate()
    {
        var result = await new ProcessRunner().RunAsync("where.exe", ["definitely-not-a-command;whoami"], timeout: TimeSpan.FromSeconds(5));
        Assert.NotEqual(0, result.ExitCode);
        Assert.DoesNotContain(Environment.UserName, result.StandardOutput, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Diagnostics_redact_credentials_and_user_paths()
    {
        var value = $@"C:\Users\{Environment.UserName}\project token=abc Bearer secret-token";
        var redacted = DiagnosticRedactor.Redact(value);
        Assert.DoesNotContain(Environment.UserName, redacted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("abc", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-token", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Multipass_contract_rejects_malformed_json()
    {
        var runner = new ScriptedRunner(new ProcessResult(0, "not-json", ""));
        var service = new MultipassService(runner, new FixedLocator());
        await Assert.ThrowsAnyAsync<JsonException>(() => service.ListSandboxesAsync());
    }

    [Fact]
    public async Task Exact_delete_never_uses_global_purge()
    {
        var runner = new ScriptedRunner(
            new ProcessResult(0, "{\"list\":[{\"name\":\"agent-sandbox\",\"state\":\"STOPPED\"}]}", ""),
            new ProcessResult(0, "", ""));
        var service = new MultipassService(runner, new FixedLocator());
        await service.DeleteAsync("agent-sandbox", purge: true);
        Assert.Equal(new[] { "delete", "--purge", "agent-sandbox" }, runner.Calls[1]);
        Assert.DoesNotContain(runner.Calls, call => call.SequenceEqual(new[] { "purge" }));
    }

    [Fact]
    public async Task Partial_provisioning_is_reported_for_recovery()
    {
        var directory = Path.Combine(Path.GetTempPath(), "AgentSandbox.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var cloudInit = Path.Combine(directory, "cloud-init.yaml");
        await File.WriteAllTextAsync(cloudInit, "#cloud-config");
        try
        {
            var runner = new ScriptedRunner(
                new ProcessResult(0, "{\"list\":[]}", ""),
                new ProcessResult(0, "", ""),
                new ProcessResult(1, "", "cloud-init failed"),
                new ProcessResult(0, "{\"list\":[{\"name\":\"agent-sandbox\",\"state\":\"STOPPED\"}]}", ""));
            var service = new MultipassService(runner, new FixedLocator());
            var result = await service.ProvisionAsync(new ProvisionRequest("agent-sandbox", "24.04", new ResourceProfile(2, 4, 30), cloudInit, "clean"));
            Assert.Equal(OperationState.CleanupPending, result.State);
            Assert.Equal("PROVISION_FAILED", result.ErrorCode);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task Process_runner_times_out_and_kills_child()
    {
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ProcessRunner().RunAsync(
            "powershell.exe", ["-NoProfile", "-Command", "Start-Sleep -Seconds 30"], timeout: TimeSpan.FromMilliseconds(200)));
    }

    [Fact]
    public void Multipass_installer_metadata_is_immutable_and_pinned()
    {
        var release = new MultipassInstallerService(new HttpClient()).Release;
        Assert.Equal(new Version(1, 16, 3), release.Version);
        Assert.Equal(64, release.Sha256.Length);
        Assert.Contains("/releases/download/v1.16.3/", release.DownloadUri.AbsoluteUri, StringComparison.Ordinal);
        Assert.Equal("Canonical", release.Publisher);
    }

    [Fact]
    public async Task Operation_history_rolls_forward_without_secrets()
    {
        var directory = Path.Combine(Path.GetTempPath(), "AgentSandbox.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var store = new OperationHistoryStore(Path.Combine(directory, "operations.jsonl"));
            await store.AppendAsync(new OperationProgress(Guid.NewGuid(), "Test", OperationState.Failed, "Failed", null, null, null, "TEST", $"token=secret C:\\Users\\{Environment.UserName}", DateTimeOffset.UtcNow));
            var item = Assert.Single(await store.ReadRecentAsync());
            Assert.DoesNotContain("secret", item.Detail!, StringComparison.Ordinal);
            Assert.DoesNotContain(Environment.UserName, item.Detail!, StringComparison.OrdinalIgnoreCase);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private sealed class FixedLocator : IMultipassLocator { public string? Locate() => "fake-multipass.exe"; }

    private sealed class ScriptedRunner(params ProcessResult[] results) : IProcessRunner
    {
        private readonly Queue<ProcessResult> results = new(results);
        public List<IReadOnlyList<string>> Calls { get; } = [];
        public Task<ProcessResult> RunAsync(string executable, IEnumerable<string> arguments, string? standardInput = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add(arguments.ToArray());
            if (results.Count == 0) throw new InvalidOperationException("No scripted process result remains.");
            return Task.FromResult(results.Dequeue());
        }
    }
}
