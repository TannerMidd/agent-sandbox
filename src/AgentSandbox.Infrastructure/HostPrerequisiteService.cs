using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using AgentSandbox.Application;
using AgentSandbox.Domain;
using Microsoft.Win32;

namespace AgentSandbox.Infrastructure;

public sealed class HostPrerequisiteService(
    IProcessRunner runner,
    IMultipassLocator multipassLocator,
    string setupHelperPath) : IHostPrerequisiteService
{
    public async Task<HostReadiness> InspectAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Agent Sandbox host inspection requires Windows.");
        var diagnostics = new List<DiagnosticRecord>();
        var version = Environment.OSVersion.Version;
        var isWindows11 = OperatingSystem.IsWindows() && version.Build >= 22000;
        var edition = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "EditionID", "") as string ?? "";
        var supportedEdition = edition.Contains("Professional", StringComparison.OrdinalIgnoreCase) ||
                               edition.Contains("Enterprise", StringComparison.OrdinalIgnoreCase);
        var isX64 = RuntimeInformation.OSArchitecture == Architecture.X64;
        var (totalMemory, availableMemory) = GetMemoryStatus();
        var virtualization = false;
        var hyperV = false;

        if (!isWindows11) diagnostics.Add(Error("HOST_WINDOWS_11", "Windows 11 is required", $"Detected Windows build {version.Build}."));
        if (!supportedEdition) diagnostics.Add(Error("HOST_EDITION", "Windows Pro or Enterprise is required", $"Detected edition: {edition}."));
        if (!isX64) diagnostics.Add(Error("HOST_ARCH", "An x64 processor is required", $"Detected architecture: {RuntimeInformation.OSArchitecture}."));

        try
        {
            const string script = "$p=Get-CimInstance Win32_Processor | Select-Object -First 1 VirtualizationFirmwareEnabled,SecondLevelAddressTranslationExtensions; $h=(Get-WindowsOptionalFeature -Online -FeatureName Microsoft-Hyper-V-All).State; [pscustomobject]@{virtualization=[bool]$p.VirtualizationFirmwareEnabled;slat=[bool]$p.SecondLevelAddressTranslationExtensions;hyperv=($h -eq 'Enabled')} | ConvertTo-Json -Compress";
            var result = await runner.RunAsync("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", script], timeout: TimeSpan.FromSeconds(30), cancellationToken: cancellationToken);
            if (result.IsSuccess)
            {
                using var document = JsonDocument.Parse(result.StandardOutput);
                virtualization = document.RootElement.GetProperty("virtualization").GetBoolean() && document.RootElement.GetProperty("slat").GetBoolean();
                hyperV = document.RootElement.GetProperty("hyperv").GetBoolean();
            }
        }
        catch (Exception exception)
        {
            diagnostics.Add(new DiagnosticRecord("HOST_INSPECTION", "Some host checks could not run", DiagnosticSeverity.Warning, exception.Message, "Run Agent Sandbox again as a local administrator."));
        }

        if (!virtualization) diagnostics.Add(Error("HOST_VIRTUALIZATION", "Hardware virtualization is unavailable", "Enable virtualization and SLAT in firmware before continuing."));
        if (!hyperV) diagnostics.Add(new DiagnosticRecord("HOST_HYPERV", "Hyper-V must be enabled", DiagnosticSeverity.Warning, "Agent Sandbox can enable the Windows Hyper-V feature after explicit UAC consent.", "Save your work before enabling Hyper-V because a reboot may be required."));

        var rebootPending = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending") is not null;
        var multipassPath = multipassLocator.Locate();
        string? multipassVersion = null;
        string? driver = null;
        if (multipassPath is not null)
        {
            var versionResult = await runner.RunAsync(multipassPath, ["version", "--format", "json"], timeout: TimeSpan.FromSeconds(20), cancellationToken: cancellationToken);
            multipassVersion = TryReadVersion(versionResult.StandardOutput) ?? versionResult.StandardOutput.Trim();
            var driverResult = await runner.RunAsync(multipassPath, ["get", "local.driver"], timeout: TimeSpan.FromSeconds(20), cancellationToken: cancellationToken);
            if (driverResult.IsSuccess) driver = driverResult.StandardOutput.Trim();
        }

        var compatible = multipassPath is not null && string.Equals(driver, "hyperv", StringComparison.OrdinalIgnoreCase);
        if (multipassPath is not null && !compatible)
            diagnostics.Add(Error("MULTIPASS_DRIVER", "Existing Multipass driver is not Hyper-V", $"Detected driver: {driver ?? "unknown"}. Agent Sandbox will not switch a driver automatically."));

        return new HostReadiness(
            isWindows11, supportedEdition, isX64, virtualization, hyperV, rebootPending,
            multipassPath is not null, compatible, multipassPath, multipassVersion, driver,
            Environment.GetEnvironmentVariable("MULTIPASS_STORAGE", EnvironmentVariableTarget.Machine),
            totalMemory, availableMemory, diagnostics);
    }

    public async Task<SetupHelperResponse> ExecuteElevatedAsync(SetupHelperRequest request, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Agent Sandbox elevated setup requires Windows.");
        if (!SetupHelperOperations.Allowed.Contains(request.Operation))
            throw new InvalidOperationException("The requested elevated operation is not allow-listed.");
        if (!File.Exists(setupHelperPath))
            throw new FileNotFoundException("The compiled setup helper was not found.", setupHelperPath);

        var pipeName = $"AgentSandbox.Setup.{Guid.NewGuid():N}";
        var currentSid = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("The current Windows identity has no SID.");
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new PipeAccessRule(currentSid, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));

        await using var pipe = NamedPipeServerStreamAcl.Create(
            pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous,
            64 * 1024, 64 * 1024, security);
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = setupHelperPath,
            UseShellExecute = true,
            Verb = "runas",
            Arguments = $"--pipe {pipeName}",
            WindowStyle = ProcessWindowStyle.Hidden
        }) ?? throw new InvalidOperationException("Windows could not start the elevated setup helper.");

        await pipe.WaitForConnectionAsync(cancellationToken);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
        await writer.WriteLineAsync(JsonSerializer.Serialize(request).AsMemory(), cancellationToken);
        var responseLine = await reader.ReadLineAsync(cancellationToken);
        var response = responseLine is null ? null : JsonSerializer.Deserialize<SetupHelperResponse>(responseLine);
        return response ?? throw new InvalidDataException("The setup helper returned an empty response.");
    }

    private static DiagnosticRecord Error(string code, string title, string detail) => new(code, title, DiagnosticSeverity.Error, detail);

    private static string? TryReadVersion(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            foreach (var name in new[] { "multipass", "multipassd" })
                if (document.RootElement.TryGetProperty(name, out var value)) return value.GetString();
        }
        catch (JsonException) { }
        return null;
    }

    private static (long Total, long Available) GetMemoryStatus()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        return GlobalMemoryStatusEx(ref status) ? ((long)status.TotalPhysical, (long)status.AvailablePhysical) : (0, 0);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}
