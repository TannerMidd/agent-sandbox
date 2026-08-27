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
                Hardening = HardeningPresets.GetRequired(HardeningPresets.RestrictedId).Options,
                Theme = "Dark",
                SetupState = SetupState.Ready,
                Sandboxes =
                [
                    new SandboxConfiguration("agent-sandbox-one", new ResourceProfile(2, 4, 30), []),
                    new SandboxConfiguration("agent-sandbox-two", new ResourceProfile(4, 8, 50), ["codex"], ImageId: "alpine-3.22", Hardening: HardeningPresets.GetRequired(HardeningPresets.RestrictedId).Options)
                ]
            };
            await store.SaveAsync(expected);
            var actual = await store.LoadAsync();
            Assert.Equal(expected.InstanceName, actual.InstanceName);
            Assert.Equal(expected.Theme, actual.Theme);
            Assert.Equal(expected.SetupState, actual.SetupState);
            Assert.Equal("alpine-3.22", actual.ImageId);
            Assert.Equal(HardeningPresets.RestrictedId, actual.Hardening.PresetId);
            Assert.Equal("alpine-3.22", actual.Sandboxes[1].ImageId);
            Assert.Equal(NetworkAccessPolicy.WebOnly, actual.Sandboxes[1].Hardening?.NetworkAccess);
            Assert.Equal(expected.Resources, actual.Resources);
            Assert.Equal(expected.SelectedPresetIds, actual.SelectedPresetIds);
            Assert.Equal(expected.Sandboxes.Select(item => item.InstanceName), actual.Sandboxes.Select(item => item.InstanceName));
            Assert.Equal(expected.Sandboxes.Select(item => item.Resources), actual.Sandboxes.Select(item => item.Resources));
            Assert.Equal(expected.Sandboxes[1].SelectedPresetIds, actual.Sandboxes[1].SelectedPresetIds);
            Assert.Equal(expected.Sandboxes[1].Hardening, actual.Sandboxes[1].Hardening);
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
            Assert.Equal(HardeningPresets.DevelopmentId, settings.Hardening.PresetId);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void RestrictedHardeningIsRenderedIntoCloudInitWithoutLeavingTemplateMarkers()
    {
        var template = $"#cloud-config{Environment.NewLine}{CloudInitRenderer.ConfigurationMarker}";

        var rendered = CloudInitRenderer.Render(template, HardeningPresets.GetRequired(HardeningPresets.RestrictedId).Options);

        Assert.Contains("hardening_preset='restricted'", rendered, StringComparison.Ordinal);
        Assert.Contains("network_access='web-only'", rendered, StringComparison.Ordinal);
        Assert.Contains("allow_administrative_tools='false'", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("{{AGENT_SANDBOX_HARDENING_CONFIGURATION}}", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void CloudInitWithoutHardeningMarkerFailsClosed() =>
        Assert.Throws<InvalidDataException>(() => CloudInitRenderer.Render("#cloud-config", SandboxHardeningOptions.Development));

    [Fact]
    public void PackagedCloudInitImplementsPortableFailClosedHardeningControls()
    {
        var template = File.ReadAllText(RepoFile("cloud-init.yaml"));

        foreach (var family in new[] { "package_family='apt'", "package_family='apk'", "package_family='dnf'", "package_family='pacman'" })
            Assert.Contains(family, template, StringComparison.Ordinal);
        Assert.Contains("apk update", template, StringComparison.Ordinal);
        Assert.Contains("set_sysctl_exact user.max_user_namespaces 0", template, StringComparison.Ordinal);
        Assert.Contains("Hardening verification failed", template, StringComparison.Ordinal);
        Assert.DoesNotContain("sysctl --system >/dev/null 2>&1 || true", template, StringComparison.Ordinal);
        Assert.Contains("management_gateway=", template, StringComparison.Ordinal);
        Assert.Contains("ufw allow from \"$management_gateway\" to any port 22", template, StringComparison.Ordinal);
        Assert.Contains("systemctl enable ufw", template, StringComparison.Ordinal);
        Assert.Contains("rc-update add ufw default", template, StringComparison.Ordinal);
        Assert.Contains("agent-sandbox-verify-runtime", template, StringComparison.Ordinal);
        Assert.Contains("ufw status | grep -F 'Status: active'", template, StringComparison.Ordinal);
        Assert.Contains("firewall-cmd --direct --get-all-rules", template, StringComparison.Ordinal);
        Assert.Contains("systemctl is-active --quiet dnf-automatic.timer", template, StringComparison.Ordinal);
        Assert.Contains("NOPASSWD: /usr/local/sbin/agent-sandbox-verify-runtime", template, StringComparison.Ordinal);
        Assert.DoesNotContain("ufw allow 22/tcp", template, StringComparison.Ordinal);
        Assert.Contains("--sport 68 --dport 67", template, StringComparison.Ordinal);
        Assert.Contains("--sport 546 --dport 547", template, StringComparison.Ordinal);
        Assert.Contains("zzzz-agent-sandbox-require-password", template, StringComparison.Ordinal);
        Assert.Contains("zzzz-agent-sandbox-admin", template, StringComparison.Ordinal);
        Assert.Contains("NOPASSWD: ALL", template, StringComparison.Ordinal);
        Assert.True(template.IndexOf("cat > /etc/agent-sandbox/hardening.json", StringComparison.Ordinal) > template.IndexOf("firewall-cmd --reload", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ProcessRunnerKeepsArgumentsSeparate()
    {
        var executable = OperatingSystem.IsWindows() ? "where.exe" : "/usr/bin/printf";
        var arguments = OperatingSystem.IsWindows() ? new[] { "definitely-not-a-command;whoami" } : new[] { "%s", "literal;whoami" };
        var result = await new ProcessRunner().RunAsync(executable, arguments, timeout: TimeSpan.FromSeconds(5));
        if (OperatingSystem.IsWindows()) Assert.NotEqual(0, result.ExitCode);
        else Assert.Equal("literal;whoami", result.StandardOutput);
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
    public async Task ExactSandboxInfoUsesReportedResourceAllocation()
    {
        var runner = new ScriptedRunner(
            new ProcessResult(0, "{\"list\":[{\"name\":\"agent-dev\",\"state\":\"RUNNING\"}]}", ""),
            new ProcessResult(0, InfoJson("agent-dev", 6, 12, 80, "Running"), ""));
        var service = new MultipassService(runner, new FixedLocator());

        var sandbox = await service.GetSandboxAsync("agent-dev");

        Assert.NotNull(sandbox);
        Assert.Equal(new ResourceProfile(6, 12, 80), sandbox.Resources);
        Assert.Equal(new[] { "info", "agent-dev", "--format", "json" }, runner.Calls[1]);
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
            new ProcessResult(0, InfoJson("agent-sandbox", 4, 4, 50, "Stopped"), ""),
            new ProcessResult(0, "", ""));
        var service = new MultipassService(runner, new FixedLocator());
        await service.DeleteAsync("agent-sandbox", purge: true);
        Assert.Equal(ExactDeleteArguments, runner.Calls[2]);
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
        await File.WriteAllTextAsync(cloudInit, $"#cloud-config{Environment.NewLine}{CloudInitRenderer.ConfigurationMarker}");
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
    public async Task SuccessfulProvisioningVerifiesHardeningArtifactAndDeletesRenderedCloudInit()
    {
        var directory = Path.Combine(Path.GetTempPath(), "AgentSandbox.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var cloudInit = Path.Combine(directory, "cloud-init.yaml");
        await File.WriteAllTextAsync(cloudInit, $"#cloud-config{Environment.NewLine}{CloudInitRenderer.ConfigurationMarker}");
        try
        {
            var policy = "{\"schemaVersion\":1,\"presetId\":\"restricted\",\"automaticSecurityUpdates\":true,\"kernelHardening\":true,\"restrictUnprivilegedFeatures\":true,\"auditSecurityEvents\":true,\"networkAccess\":\"web-only\",\"allowAdministrativeTools\":false}";
            var runner = new ScriptedRunner(
                new ProcessResult(0, "{\"list\":[]}", ""),
                new ProcessResult(0, "", ""),
                new ProcessResult(0, "", ""),
                new ProcessResult(0, "", ""),
                new ProcessResult(0, policy, ""),
                new ProcessResult(0, "", ""),
                new ProcessResult(0, "", ""),
                new ProcessResult(0, "", ""),
                new ProcessResult(0, "", ""));
            var service = new MultipassService(runner, new FixedLocator());

            var result = await service.ProvisionAsync(new ProvisionRequest(
                "agent-sandbox", "24.04", new ResourceProfile(2, 4, 30), cloudInit, "clean",
                Hardening: HardeningPresets.GetRequired(HardeningPresets.RestrictedId).Options));

            Assert.Equal(OperationState.Succeeded, result.State);
            Assert.Contains("/etc/agent-sandbox/hardening.json", runner.Calls[3][5], StringComparison.Ordinal);
            Assert.Contains("! sudo -n true", runner.Calls[8][5], StringComparison.Ordinal);
            Assert.Contains("agent-sandbox-verify-runtime", runner.Calls[8][5], StringComparison.Ordinal);
            Assert.Contains("user.max_user_namespaces", runner.Calls[8][5], StringComparison.Ordinal);
            var renderedPath = runner.Calls[1][Array.IndexOf(runner.Calls[1].ToArray(), "--cloud-init") + 1];
            Assert.False(File.Exists(renderedPath));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void RuntimeHardeningVerificationUsesNarrowRootVerifierAndEffectivePrivilegeChecks()
    {
        var restricted = MultipassService.BuildHardeningVerificationScript(HardeningPresets.GetRequired(HardeningPresets.RestrictedId).Options);
        Assert.Contains("sudo -n /usr/local/sbin/agent-sandbox-verify-runtime", restricted, StringComparison.Ordinal);
        Assert.Contains("! sudo -n true", restricted, StringComparison.Ordinal);
        Assert.Contains("! docker info", restricted, StringComparison.Ordinal);

        var development = MultipassService.BuildHardeningVerificationScript(SandboxHardeningOptions.Development);
        Assert.Contains("sudo -n /usr/local/sbin/agent-sandbox-verify-runtime", development, StringComparison.Ordinal);
        Assert.Contains("sudo -n true", development, StringComparison.Ordinal);
        Assert.Contains("docker info", development, StringComparison.Ordinal);
    }

    [Fact]
    public void HardeningArtifactMustExactlyMatchRequestedPolicy()
    {
        const string wrong = "{\"schemaVersion\":1,\"presetId\":\"restricted\",\"automaticSecurityUpdates\":true,\"kernelHardening\":false,\"restrictUnprivilegedFeatures\":true,\"auditSecurityEvents\":true,\"networkAccess\":\"web-only\",\"allowAdministrativeTools\":false}";
        Assert.Throws<InvalidDataException>(() => MultipassService.ValidateHardeningArtifact(
            wrong, HardeningPresets.GetRequired(HardeningPresets.RestrictedId).Options));
    }

    [Fact]
    public async Task HostDigestMatchesGuestProtocolDigest()
    {
        var directory = Path.Combine(Path.GetTempPath(), "AgentSandbox.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "original.txt");
            await File.WriteAllTextAsync(path, "original Ω", new System.Text.UTF8Encoding(false));
            Assert.Equal("7618fdd76e9ddb23ff940110a0020396d79bb766e09ef27cecc01d880de7eb33", await GuestFileService.ComputeDigestAsync(path));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task SnapshotCreationStopsAndRestartsRunningSandbox()
    {
        var runner = new ScriptedRunner(
            new ProcessResult(0, "{\"list\":[{\"name\":\"agent-dev\",\"state\":\"RUNNING\"}]}", ""),
            new ProcessResult(0, InfoJson("agent-dev", 2, 4, 30, "Running"), ""),
            new ProcessResult(0, "", ""),
            new ProcessResult(0, "", ""),
            new ProcessResult(0, "", ""));
        var service = new MultipassService(runner, new FixedLocator());

        var result = await service.CreateSnapshotAsync("agent-dev", "manual-one");

        Assert.Equal(OperationState.Succeeded, result.State);
        Assert.Equal(new[] { "stop", "agent-dev" }, runner.Calls[2]);
        Assert.Equal(new[] { "snapshot", "agent-dev", "--name", "manual-one" }, runner.Calls[3]);
        Assert.Equal(new[] { "start", "agent-dev" }, runner.Calls[4]);
    }

    [Fact]
    public async Task ExactSnapshotDeleteUsesQualifiedTarget()
    {
        var runner = new ScriptedRunner(
            new ProcessResult(0, "{\"snapshots\":[{\"instance\":\"agent-dev\",\"name\":\"manual-one\"}]}", ""),
            new ProcessResult(0, "", ""));
        var service = new MultipassService(runner, new FixedLocator());

        var result = await service.DeleteSnapshotAsync("agent-dev", "manual-one");

        Assert.Equal(OperationState.Succeeded, result.State);
        Assert.Equal(new[] { "delete", "agent-dev.manual-one" }, runner.Calls[1]);
    }

    [Fact]
    public async Task SnapshotRestoreRestartsPreviouslyRunningSandbox()
    {
        var runner = new ScriptedRunner(
            new ProcessResult(0, "{\"list\":[{\"name\":\"agent-dev\",\"state\":\"RUNNING\"}]}", ""),
            new ProcessResult(0, InfoJson("agent-dev", 2, 4, 30, "Running"), ""),
            new ProcessResult(0, "{\"snapshots\":[{\"instance\":\"agent-dev\",\"name\":\"manual-one\"}]}", ""),
            new ProcessResult(0, "", ""),
            new ProcessResult(0, "", ""),
            new ProcessResult(0, "", ""));
        var service = new MultipassService(runner, new FixedLocator());

        var result = await service.RestoreSnapshotAsync("agent-dev", "manual-one");

        Assert.Equal(OperationState.Succeeded, result.State);
        Assert.Equal(new[] { "stop", "agent-dev" }, runner.Calls[3]);
        Assert.Equal(new[] { "restore", "--destructive", "agent-dev.manual-one" }, runner.Calls[4]);
        Assert.Equal(new[] { "start", "agent-dev" }, runner.Calls[5]);
    }

    [Fact]
    public async Task PartialProvisioningIsReportedForRecovery()
    {
        var directory = Path.Combine(Path.GetTempPath(), "AgentSandbox.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var cloudInit = Path.Combine(directory, "cloud-init.yaml");
        await File.WriteAllTextAsync(cloudInit, $"#cloud-config{Environment.NewLine}{CloudInitRenderer.ConfigurationMarker}");
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
            Assert.DoesNotContain(runner.Calls, call => call.Count > 0 && call[0] == "info");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task ProcessRunnerTimesOutAndKillsChild()
    {
        var executable = OperatingSystem.IsWindows() ? "powershell.exe" : "/bin/sh";
        var arguments = OperatingSystem.IsWindows() ? new[] { "-NoProfile", "-Command", "Start-Sleep -Seconds 30" } : new[] { "-c", "sleep 30" };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ProcessRunner().RunAsync(
            executable, arguments, timeout: TimeSpan.FromMilliseconds(200)));
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

    [Fact]
    public void MultipassDiscoveryUsesOnlyProtectedSignedCanonicalInstallation()
    {
        var source = File.ReadAllText(RepoFile("src/AgentSandbox.Infrastructure/MultipassLocator.cs"));
        Assert.DoesNotContain("GetEnvironmentVariable(\"PATH\")", source, StringComparison.Ordinal);
        Assert.Contains("WindowsAuthenticodeVerifier.IsTrustedSignedBy", source, StringComparison.Ordinal);
        Assert.Contains("HasCanonicalWindowsRegistration", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ElevatedInstallerUsesHelperCompiledReleaseIdentity()
    {
        var helper = File.ReadAllText(RepoFile("src/AgentSandbox.SetupHelper/Program.cs"));
        Assert.Contains("MultipassInstallerService.PinnedRelease", helper, StringComparison.Ordinal);
        Assert.Contains("request.ExpectedSha256, approved.Sha256", helper, StringComparison.Ordinal);
        Assert.Contains("approved.FileName", helper, StringComparison.Ordinal);
        Assert.Contains("CreateSecuredInstallerCopy", helper, StringComparison.Ordinal);
        Assert.Contains("SecureDirectory(root)", helper, StringComparison.Ordinal);
        Assert.Contains("SecureDirectory(installers)", helper, StringComparison.Ordinal);
        Assert.Contains("security.SetOwner(administrators)", helper, StringComparison.Ordinal);
        Assert.Contains("applied.AreAccessRulesProtected", helper, StringComparison.Ordinal);
        Assert.Contains("Secured installer directories cannot be reparse points", helper, StringComparison.Ordinal);
        Assert.Contains("Reparse-point installer files are not allowed", helper, StringComparison.Ordinal);
        Assert.Contains("request?.RequestId ?? Guid.Empty", helper, StringComparison.Ordinal);
        var host = File.ReadAllText(RepoFile("src/AgentSandbox.Infrastructure/HostPrerequisiteService.cs"));
        Assert.Contains("ValidateProtectedSetupHelper", host, StringComparison.Ordinal);
        Assert.Contains("Portable builds are diagnostics-only", host, StringComparison.Ordinal);
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
    public async Task ReconcileRemovesCrashLeftHostPartialsFromJournal()
    {
        var directory = Path.Combine(Path.GetTempPath(), "AgentSandbox.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var partial = Path.Combine(directory, $"project.{Guid.NewGuid():N}.partial");
            await File.WriteAllTextAsync(partial, "incomplete");
            var journal = Path.Combine(directory, "pending-transfers.json");
            await File.WriteAllTextAsync(journal, JsonSerializer.Serialize(new[] { partial }));
            using var service = new GuestFileService(new ReconcileRunner(), new FixedLocator(), "agent-dev", RepoFile("guest/guest_helper.py"), journal);

            await service.ReconcileAsync();

            Assert.False(File.Exists(partial));
            Assert.Equal("[]", (await File.ReadAllTextAsync(journal)).Trim());
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task FailedHostPartialCleanupRemainsJournaledForRetry()
    {
        if (!OperatingSystem.IsWindows()) return;
        var directory = Path.Combine(Path.GetTempPath(), "AgentSandbox.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var partial = Path.Combine(directory, $"project.{Guid.NewGuid():N}.partial");
            await File.WriteAllTextAsync(partial, "locked");
            var journal = Path.Combine(directory, "pending-transfers.json");
            await File.WriteAllTextAsync(journal, JsonSerializer.Serialize(new[] { partial }));
            await using var held = new FileStream(partial, FileMode.Open, FileAccess.Read, FileShare.None);
            using var service = new GuestFileService(new ReconcileRunner(), new FixedLocator(), "agent-dev", RepoFile("guest/guest_helper.py"), journal);

            await service.ReconcileAsync();

            Assert.Contains(partial, await File.ReadAllTextAsync(journal), StringComparison.OrdinalIgnoreCase);
            held.Dispose();
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task ReconcileWaitsForCrossProcessTransferLease()
    {
        var directory = Path.Combine(Path.GetTempPath(), "AgentSandbox.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(directory, "locks"));
        try
        {
            var partial = Path.Combine(directory, $"project.{Guid.NewGuid():N}.partial");
            await File.WriteAllTextAsync(partial, "active");
            var journal = Path.Combine(directory, "pending-transfers.json");
            await File.WriteAllTextAsync(journal, JsonSerializer.Serialize(new[] { partial }));
            await using var held = new FileStream(Path.Combine(directory, "locks", "transfer.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            using var service = new GuestFileService(new ReconcileRunner(), new FixedLocator(), "agent-dev", RepoFile("guest/guest_helper.py"), journal);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ReconcileAsync(cancellation.Token));

            Assert.True(File.Exists(partial));
            held.Dispose();
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task LockedDirectoryBackupRemainsJournaledForRetry()
    {
        if (!OperatingSystem.IsWindows()) return;
        var directory = Path.Combine(Path.GetTempPath(), "AgentSandbox.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var id = Guid.NewGuid();
            var final = Path.Combine(directory, "project");
            var partial = final + $".{id:N}.partial";
            var backup = final + $".{id:N}.backup";
            Directory.CreateDirectory(final);
            Directory.CreateDirectory(backup);
            var locked = Path.Combine(backup, "old.txt");
            await File.WriteAllTextAsync(locked, "old");
            var transferJournal = Path.Combine(directory, "pending-transfers.json");
            await File.WriteAllTextAsync(transferJournal, "[]");
            var commitJournal = Path.Combine(directory, "pending-directory-commits.json");
            await File.WriteAllTextAsync(commitJournal, JsonSerializer.Serialize(new[]
            {
                new { PartialPath = partial, FinalPath = final, BackupPath = backup, Phase = "committed" }
            }));
            await using var held = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None);
            using var service = new GuestFileService(new ReconcileRunner(), new FixedLocator(), "agent-dev", RepoFile("guest/guest_helper.py"), transferJournal);

            await service.ReconcileAsync();

            Assert.Contains(backup, await File.ReadAllTextAsync(commitJournal), StringComparison.OrdinalIgnoreCase);
            held.Dispose();
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task ReconcileCompletesInterruptedDirectoryOverwrite()
    {
        var directory = Path.Combine(Path.GetTempPath(), "AgentSandbox.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var id = Guid.NewGuid();
            var final = Path.Combine(directory, "project");
            var partial = final + $".{id:N}.partial";
            var backup = final + $".{id:N}.backup";
            Directory.CreateDirectory(partial);
            await File.WriteAllTextAsync(Path.Combine(partial, "new.txt"), "new");
            Directory.CreateDirectory(backup);
            await File.WriteAllTextAsync(Path.Combine(backup, "old.txt"), "old");
            var transferJournal = Path.Combine(directory, "pending-transfers.json");
            await File.WriteAllTextAsync(transferJournal, "[]");
            var commitJournal = Path.Combine(directory, "pending-directory-commits.json");
            await File.WriteAllTextAsync(commitJournal, JsonSerializer.Serialize(new[]
            {
                new { PartialPath = partial, FinalPath = final, BackupPath = backup, Phase = "backedUp" }
            }));
            using var service = new GuestFileService(new ReconcileRunner(), new FixedLocator(), "agent-dev", RepoFile("guest/guest_helper.py"), transferJournal);

            await service.ReconcileAsync();

            Assert.True(File.Exists(Path.Combine(final, "new.txt")));
            Assert.False(Directory.Exists(backup));
            Assert.Equal("[]", (await File.ReadAllTextAsync(commitJournal)).Trim());
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task ReleaseCheckIncludesNewerPreviewReleases()
    {
        const string json = "[{\"tag_name\":\"v0.1.6\",\"html_url\":\"https://github.com/example/repo/releases/tag/v0.1.6\",\"body\":\"notes\",\"draft\":false,\"prerelease\":true}]";
        using var client = new HttpClient(new FixedHttpHandler(json));
        var service = new GitHubReleaseService(client, "example/repo");

        var release = await service.CheckAsync(new Version(0, 1, 5));

        Assert.NotNull(release);
        Assert.Equal(new Version(0, 1, 6), release.Version);
        Assert.True(release.IsPrerelease);
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

    private static string InfoJson(string name, int cpu, int memoryGiB, int diskGiB, string state) =>
        $"{{\"info\":{{\"{name}\":{{\"cpu_count\":\"{cpu}\",\"memory\":{{\"total\":{memoryGiB * (1L << 30)}}},\"disks\":{{\"sda1\":{{\"total\":\"{diskGiB * (1L << 30)}\"}}}},\"state\":\"{state}\",\"ipv4\":[],\"release\":\"Ubuntu\"}}}}}}";

    private static string RepoFile(string name)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AgentSandbox.slnx"))) directory = directory.Parent;
        if (directory is null) throw new DirectoryNotFoundException("Repository root was not found.");
        return Path.Combine(directory.FullName, name);
    }

    private sealed class FixedLocator : IMultipassLocator { public string? Locate() => "fake-multipass.exe"; }

    private sealed class FixedHttpHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Contains("/releases?per_page=20", request.RequestUri?.AbsoluteUri, StringComparison.Ordinal);
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class ReconcileRunner : IProcessRunner
    {
        private Guid requestId;
        public Task<ProcessResult> RunAsync(string executable, IEnumerable<string> arguments, string? standardInput = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        {
            var values = arguments.ToArray();
            if (values is ["transfer", var local, _] && local.EndsWith(".json", StringComparison.Ordinal))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(local));
                requestId = document.RootElement.GetProperty("id").GetGuid();
            }
            if (values.Length >= 5 && values[0] == "exec" && values[3] == "python3")
            {
                var response = JsonSerializer.Serialize(new
                {
                    v = 1, id = requestId, ok = true, rootId = "work", relativePath = Array.Empty<string>(),
                    entries = Array.Empty<object>(), revision = (string?)null, nextCursor = (string?)null,
                    unstable = false, content = (string?)null, warnings = Array.Empty<string>(), error = (object?)null
                });
                return Task.FromResult(new ProcessResult(0, response, ""));
            }
            return Task.FromResult(new ProcessResult(0, "", ""));
        }
    }

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
