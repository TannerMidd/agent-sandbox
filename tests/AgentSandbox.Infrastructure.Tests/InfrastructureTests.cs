using AgentSandbox.Domain;
using AgentSandbox.Infrastructure;
using AgentSandbox.Application;
using System.Text.Json;

namespace AgentSandbox.Infrastructure.Tests;

public sealed class InfrastructureTests
{
    private static readonly string[] ExactDeleteArguments = ["delete", "--purge", "agent-sandbox"];
    private static readonly string[] GlobalPurgeArguments = ["purge"];
    private static readonly string[] ResourceUsageArgumentsPrefix = ["exec", "agent-dev", "--", "python3", "-c"];

    [Fact]
    public async Task SettingsAreWrittenAtomicallyAndRoundTrip()
    {
        var directory = Path.Combine(Path.GetTempPath(), "AgentSandbox.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "settings.json");
            var store = new JsonSettingsStore(path);
            var expected = new AgentSandboxSettings
            {
                InstanceName = "agent-sandbox-two",
                ImageId = "alpine-3.22",
                Theme = "Dark",
                SetupState = SetupState.Ready,
                Sandboxes =
                [
                    new SandboxConfiguration("agent-sandbox-one", new ResourceProfile(2, 4, 30), []),
                    new SandboxConfiguration("agent-sandbox-two", new ResourceProfile(4, 8, 50), ["codex"], ImageId: "alpine-3.22")
                ]
            };
            await store.SaveAsync(expected);
            var actual = await store.LoadAsync();
            Assert.Equal(expected.InstanceName, actual.InstanceName);
            Assert.Equal(expected.Theme, actual.Theme);
            Assert.Equal(expected.SetupState, actual.SetupState);
            Assert.Equal("alpine-3.22", actual.ImageId);
            Assert.Equal("alpine-3.22", actual.Sandboxes[1].ImageId);
            Assert.Equal(expected.Resources, actual.Resources);
            Assert.Equal(expected.SelectedPresetIds, actual.SelectedPresetIds);
            Assert.Equal(expected.Sandboxes, actual.Sandboxes);
            Assert.False(File.Exists(path + ".tmp"));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task ExistingSettingsWithoutImageSelectionUseUbuntuDefault()
    {
        var directory = Path.Combine(Path.GetTempPath(), "AgentSandbox.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "settings.json");
            await File.WriteAllTextAsync(path, "{\"schemaVersion\":1,\"instanceName\":\"agent-sandbox-old\",\"sandboxes\":[],\"setupState\":0,\"resources\":{\"cpuCount\":2,\"memoryGiB\":4,\"diskGiB\":30}}");
            var settings = await new JsonSettingsStore(path).LoadAsync();
            Assert.Equal(LinuxImages.DefaultId, settings.ImageId);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task ProcessRunnerKeepsArgumentsSeparate()
    {
        var result = await new ProcessRunner().RunAsync("where.exe", ["definitely-not-a-command;whoami"], timeout: TimeSpan.FromSeconds(5));
        Assert.NotEqual(0, result.ExitCode);
        Assert.DoesNotContain(Environment.UserName, result.StandardOutput, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DiagnosticsRedactCredentialsAndUserPaths()
    {
        var value = $@"C:\Users\{Environment.UserName}\project token=abc Bearer secret-token";
        var redacted = DiagnosticRedactor.Redact(value);
        Assert.DoesNotContain(Environment.UserName, redacted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("abc", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-token", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MultipassContractRejectsMalformedJson()
    {
        var runner = new ScriptedRunner(new ProcessResult(0, "not-json", ""));
        var service = new MultipassService(runner, new FixedLocator());
        await Assert.ThrowsAnyAsync<JsonException>(() => service.ListSandboxesAsync());
    }

    [Fact]
    public async Task StoppedSandboxWithNoIpAddressIsParsed()
    {
        var runner = new ScriptedRunner(new ProcessResult(0,
            "{\"list\":[{\"ipv4\":[],\"name\":\"agent-dev\",\"release\":\"Ubuntu 24.04 LTS\",\"state\":\"Stopped\"}]}", ""));
        var service = new MultipassService(runner, new FixedLocator());

        var sandbox = Assert.Single(await service.ListSandboxesAsync());

        Assert.Equal("agent-dev", sandbox.InstanceName);
        Assert.Equal(SandboxState.Stopped, sandbox.State);
        Assert.Equal("Ubuntu 24.04 LTS", sandbox.OsRelease);
        Assert.Null(sandbox.IPv4Address);
    }

    [Fact]
    public async Task RunningSandboxResourceUsageIsParsedFromGuestMetrics()
    {
        var runner = new ScriptedRunner(new ProcessResult(0,
            "{\"cpuPercent\":37.5,\"usedMemoryBytes\":2147483648,\"totalMemoryBytes\":4294967296,\"usedDiskBytes\":10737418240,\"totalDiskBytes\":53687091200}", ""));
        var service = new MultipassService(runner, new FixedLocator());

        var usage = await service.GetResourceUsageAsync("agent-dev");

        Assert.Equal(37.5, usage.CpuPercent);
        Assert.Equal(2L << 30, usage.UsedMemoryBytes);
        Assert.Equal(4L << 30, usage.TotalMemoryBytes);
        Assert.Equal(10L << 30, usage.UsedDiskBytes);
        Assert.Equal(50L << 30, usage.TotalDiskBytes);
        Assert.Equal(ResourceUsageArgumentsPrefix, runner.Calls[0].Take(5));
        Assert.Contains("/proc/stat", runner.Calls[0][5], StringComparison.Ordinal);
    }

    [Fact]
    public async Task GuestResourceUsageRejectsZeroCapacity()
    {
        var runner = new ScriptedRunner(new ProcessResult(0,
            "{\"cpuPercent\":0,\"usedMemoryBytes\":0,\"totalMemoryBytes\":0,\"usedDiskBytes\":0,\"totalDiskBytes\":1}", ""));
        var service = new MultipassService(runner, new FixedLocator());

        await Assert.ThrowsAsync<JsonException>(() => service.GetResourceUsageAsync("agent-dev"));
    }

    [Fact]
    public async Task ExactDeleteNeverUsesGlobalPurge()
    {
        var runner = new ScriptedRunner(
            new ProcessResult(0, "{\"list\":[{\"name\":\"agent-sandbox\",\"state\":\"STOPPED\"}]}", ""),
            new ProcessResult(0, "", ""));
        var service = new MultipassService(runner, new FixedLocator());
        await service.DeleteAsync("agent-sandbox", purge: true);
        Assert.Equal(ExactDeleteArguments, runner.Calls[1]);
        Assert.DoesNotContain(runner.Calls, call => call.SequenceEqual(GlobalPurgeArguments));
    }

    [Fact]
    public async Task ProvisioningRejectsImageReferencesOutsideTheCatalog()
    {
        var service = new MultipassService(new ScriptedRunner(), new FixedLocator());
        var request = new ProvisionRequest("agent-sandbox", "https://example.invalid/untrusted.qcow2", new ResourceProfile(2, 4, 30), "cloud-init.yaml", "clean");
        await Assert.ThrowsAsync<ArgumentException>(() => service.ProvisionAsync(request));
    }

    [Fact]
    public async Task UserSuppliedHttpsImagePassesCatalogBoundary()
    {
        var directory = Path.Combine(Path.GetTempPath(), "AgentSandbox.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var cloudInit = Path.Combine(directory, "cloud-init.yaml");
        await File.WriteAllTextAsync(cloudInit, "#cloud-config");
        try
        {
            var runner = new ScriptedRunner(
                new ProcessResult(0, "{\"list\":[]}", ""),
                new ProcessResult(1, "", "download stopped"),
                new ProcessResult(0, "{\"list\":[]}", ""));
            var service = new MultipassService(runner, new FixedLocator());
            var request = new ProvisionRequest("agent-custom", "https://images.example.org/linux.qcow2", new ResourceProfile(2, 2, 20), cloudInit, "clean", true);

            var result = await service.ProvisionAsync(request);

            Assert.Equal(OperationState.Failed, result.State);
            Assert.Equal("https://images.example.org/linux.qcow2", runner.Calls[1][1]);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task PartialProvisioningIsReportedForRecovery()
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
    public async Task ProcessRunnerTimesOutAndKillsChild()
    {
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ProcessRunner().RunAsync(
            "powershell.exe", ["-NoProfile", "-Command", "Start-Sleep -Seconds 30"], timeout: TimeSpan.FromMilliseconds(200)));
    }

    [Theory]
    [InlineData("1.16.0", true)]
    [InlineData("multipass 1.16.3+win", true)]
    [InlineData("1.15.1", false)]
    [InlineData("unknown", false)]
    public void MultipassVersionRequiresCustomImageSupport(string version, bool expected) =>
        Assert.Equal(expected, HostPrerequisiteService.IsSupportedMultipassVersion(version));

    [Fact]
    public void MultipassInstallerMetadataIsImmutableAndPinned()
    {
        var release = new MultipassInstallerService(new HttpClient()).Release;
        Assert.Equal(new Version(1, 16, 3), release.Version);
        Assert.Equal(64, release.Sha256.Length);
        Assert.Contains("/releases/download/v1.16.3/", release.DownloadUri.AbsoluteUri, StringComparison.Ordinal);
        Assert.Equal("Canonical", release.Publisher);
    }

    [Theory]
    [InlineData("Multipass", "Canonical Ltd", true)]
    [InlineData("Multipass", "Canonical", true)]
    [InlineData("Multipass", "Unknown publisher", false)]
    [InlineData("Another product", "Canonical Ltd", false)]
    public void MultipassRegistrationRequiresExactProductAndCanonicalPublisher(string name, string publisher, bool expected)
    {
        Assert.Equal(expected, MultipassLocator.IsCanonicalRegistration(name, publisher));
    }

    [Fact]
    public void WindowsTerminalUsesThePerUserExecutionAlias()
    {
        var path = TerminalService.WindowsTerminalAliasPath(@"C:\Users\Developer\AppData\Local");
        Assert.Equal(@"C:\Users\Developer\AppData\Local\Microsoft\WindowsApps\wt.exe", path);
    }

    [Fact]
    public async Task OperationHistoryRollsForwardWithoutSecrets()
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
