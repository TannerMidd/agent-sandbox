using AgentSandbox.Application;
using AgentSandbox.Infrastructure;
using System.Collections.Concurrent;

namespace AgentSandbox.App;

public sealed class AppServices : IDisposable
{
    private readonly HttpClient httpClient = new() { Timeout = TimeSpan.FromMinutes(30) };
    private readonly ConcurrentDictionary<string, IGuestFileService> guestFiles = new(StringComparer.Ordinal);

    public AppServices()
    {
        var runner = new ProcessRunner();
        var locator = new MultipassLocator();
        Settings = new JsonSettingsStore();
        Multipass = new MultipassService(runner, locator);
        Presets = new PresetService(runner, locator, Path.Combine(AppContext.BaseDirectory, "presets"));
        Prerequisites = new HostPrerequisiteService(runner, locator, Path.Combine(AppContext.BaseDirectory, "helper", "AgentSandbox.SetupHelper.exe"));
        Lifecycle = new SetupCoordinator(Settings, Prerequisites, Multipass, Presets, Path.Combine(AppContext.BaseDirectory, "cloud-init.yaml"));
        Terminal = new TerminalService(locator);
        MultipassInstaller = new MultipassInstallerService(httpClient);
        History = new OperationHistoryStore();
    }

    public ISettingsStore Settings { get; }
    public IMultipassService Multipass { get; }
    public IPresetService Presets { get; }
    public IHostPrerequisiteService Prerequisites { get; }
    public ISandboxLifecycleService Lifecycle { get; }
    public ITerminalService Terminal { get; }
    public IMultipassInstallerService MultipassInstaller { get; }
    public IOperationHistoryStore History { get; }

    public IReleaseService CreateReleaseService(string repository) => new GitHubReleaseService(httpClient, repository);

    public IGuestFileService CreateGuestFiles(string instanceName) => guestFiles.GetOrAdd(instanceName, name => new GuestFileService(
        new ProcessRunner(), new MultipassLocator(), name,
        Path.Combine(AppContext.BaseDirectory, "guest", "guest_helper.py")));

    public void Dispose()
    {
        foreach (var service in guestFiles.Values)
            if (service is IDisposable disposable) disposable.Dispose();
        if (History is IDisposable history) history.Dispose();
        httpClient.Dispose();
        GC.SuppressFinalize(this);
    }
}
