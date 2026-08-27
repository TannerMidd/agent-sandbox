using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using AgentSandbox.Application;
using AgentSandbox.Domain;
using AgentSandbox.Infrastructure;

return await SetupHelperProgram.RunAsync(args);

internal static class SetupHelperProgram
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args is not ["--pipe", var pipeName] || !IsSafePipeName(pipeName) || !IsAdministrator())
            return 2;

        await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(30_000);
        using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        var line = await reader.ReadLineAsync();
        SetupHelperResponse response;
        try
        {
            var request = JsonSerializer.Deserialize<SetupHelperRequest>(line ?? "")
                ?? throw new InvalidDataException("The setup request was empty.");
            response = await ExecuteAsync(request);
        }
        catch (Exception exception)
        {
            response = new SetupHelperResponse(1, Guid.Empty, false, false,
                [new DiagnosticRecord("HELPER_FAILURE", "Setup operation failed", DiagnosticSeverity.Error, exception.Message)],
                "HELPER_FAILURE");
        }
        await writer.WriteLineAsync(JsonSerializer.Serialize(response));
        return response.IsSuccess ? 0 : 1;
    }

    private static async Task<SetupHelperResponse> ExecuteAsync(SetupHelperRequest request)
    {
        if (request.Version != 1 || !SetupHelperOperations.Allowed.Contains(request.Operation))
            return Failure(request, "HELPER_REQUEST_REJECTED", "The request version or operation is not allowed.");

        return request.Operation switch
        {
            SetupHelperOperations.InspectHost => Success(request, false, "The elevated helper is ready."),
            SetupHelperOperations.EnableHyperV => await EnableHyperVAsync(request),
            SetupHelperOperations.ConfigureFreshStorage => ConfigureStorage(request),
            SetupHelperOperations.InstallMultipass => await InstallMultipassAsync(request),
            _ => Failure(request, "HELPER_OPERATION_REJECTED", "The operation is not allow-listed.")
        };
    }

    private static async Task<SetupHelperResponse> EnableHyperVAsync(SetupHelperRequest request)
    {
        var result = await new ProcessRunner().RunAsync(
            Path.Combine(Environment.SystemDirectory, "dism.exe"),
            ["/Online", "/Enable-Feature", "/FeatureName:Microsoft-Hyper-V", "/All", "/NoRestart"],
            timeout: TimeSpan.FromMinutes(15));
        return result.IsSuccess
            ? Success(request, true, "Hyper-V was enabled. Windows must be restarted before setup continues.")
            : Failure(request, "HYPERV_ENABLE_FAILED", Redact(result.StandardError));
    }

    private static SetupHelperResponse ConfigureStorage(SetupHelperRequest request)
    {
        var path = ValidateLocalNtfsPath(request.StoragePath, mustExist: false);
        if (Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any())
            return Failure(request, "STORAGE_NOT_EMPTY", "Fresh Multipass storage must be an empty directory.");
        Directory.CreateDirectory(path);
        Environment.SetEnvironmentVariable("MULTIPASS_STORAGE", path, EnvironmentVariableTarget.Machine);
        return Success(request, false, "The fresh Multipass storage directory was configured.");
    }

    private static async Task<SetupHelperResponse> InstallMultipassAsync(SetupHelperRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ExpectedSha256) || request.ExpectedSha256.Length != 64)
            return Failure(request, "INSTALLER_HASH_MISSING", "A pinned SHA-256 value is required.");
        var path = ValidateLocalNtfsPath(request.InstallerPath, mustExist: true);
        if (!string.Equals(Path.GetExtension(path), ".msi", StringComparison.OrdinalIgnoreCase))
            return Failure(request, "INSTALLER_TYPE", "Only a local MSI installer is accepted.");

        await using var stream = File.OpenRead(path);
        var actualHash = Convert.ToHexString(await SHA256.HashDataAsync(stream));
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(actualHash), Encoding.ASCII.GetBytes(request.ExpectedSha256.ToUpperInvariant())))
            return Failure(request, "INSTALLER_HASH", "The Multipass installer SHA-256 did not match the pinned value.");

        try
        {
            if (!VerifyAuthenticode(path))
                return Failure(request, "INSTALLER_SIGNATURE", "Windows could not validate the installer's Authenticode signature.");
            // CreateFromSignedFile is the only BCL API that extracts an Authenticode signer
            // certificate; WinVerifyTrust has already validated the signed file above.
#pragma warning disable SYSLIB0057
            using var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
            using var chain = new X509Chain();
            chain.ChainPolicy.RevocationMode = X509RevocationMode.Online;
            chain.ChainPolicy.RevocationFlag = X509RevocationFlag.ExcludeRoot;
            var publisherMatches = !string.IsNullOrWhiteSpace(request.ExpectedPublisher) && certificate.Subject.Contains(request.ExpectedPublisher, StringComparison.OrdinalIgnoreCase);
            if (!publisherMatches || !chain.Build(certificate))
                return Failure(request, "INSTALLER_SIGNATURE", "The installer publisher or certificate chain could not be verified.");
        }
        catch (CryptographicException)
        {
            return Failure(request, "INSTALLER_SIGNATURE", "The installer does not have a valid Authenticode certificate.");
        }

        var result = await new ProcessRunner().RunAsync(
            Path.Combine(Environment.SystemDirectory, "msiexec.exe"),
            ["/i", path, "/passive", "/norestart"], timeout: TimeSpan.FromMinutes(15));
        return result.IsSuccess
            ? Success(request, result.ExitCode == 3010, "Canonical Multipass was installed.")
            : Failure(request, "MULTIPASS_INSTALL", Redact(result.StandardError));
    }

    private static string ValidateLocalNtfsPath(string? candidate, bool mustExist)
    {
        if (string.IsNullOrWhiteSpace(candidate)) throw new InvalidDataException("A local path is required.");
        var fullPath = Path.GetFullPath(candidate);
        var root = Path.GetPathRoot(fullPath) ?? throw new InvalidDataException("The path has no volume root.");
        var drive = new DriveInfo(root);
        if (drive.DriveType != DriveType.Fixed || !string.Equals(drive.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The path must be on a local fixed NTFS volume.");
        if (mustExist && !File.Exists(fullPath)) throw new FileNotFoundException("The selected file does not exist.", fullPath);

        var current = mustExist ? Directory.GetParent(fullPath) : new DirectoryInfo(fullPath).Parent;
        while (current is not null)
        {
            if (current.Exists && current.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidDataException("Reparse-point parent directories are not allowed.");
            current = current.Parent;
        }
        return fullPath;
    }

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static bool IsSafePipeName(string value) => value.StartsWith("AgentSandbox.Setup.", StringComparison.Ordinal) &&
                                                         value.Length == 51 && value[19..].All(Uri.IsHexDigit);
    private static string Redact(string text) => text.Replace(Environment.UserName, "<user>", StringComparison.OrdinalIgnoreCase).Trim();

    private static bool VerifyAuthenticode(string path)
    {
        var fileInfo = new WinTrustFileInfo
        {
            StructSize = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
            FilePath = path,
            FileHandle = IntPtr.Zero,
            KnownSubject = IntPtr.Zero
        };
        var filePointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
        var dataPointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustData>());
        try
        {
            Marshal.StructureToPtr(fileInfo, filePointer, fDeleteOld: false);
            var data = new WinTrustData
            {
                StructSize = (uint)Marshal.SizeOf<WinTrustData>(),
                PolicyCallbackData = IntPtr.Zero,
                SipClientData = IntPtr.Zero,
                UIChoice = 2,
                RevocationChecks = 1,
                UnionChoice = 1,
                FileInfo = filePointer,
                StateAction = 0,
                StateData = IntPtr.Zero,
                UrlReference = IntPtr.Zero,
                ProviderFlags = 0x00000040,
                UIContext = 0,
                SignatureSettings = IntPtr.Zero
            };
            Marshal.StructureToPtr(data, dataPointer, fDeleteOld: false);
            var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
            return WinVerifyTrust(IntPtr.Zero, action, dataPointer) == 0;
        }
        finally
        {
            Marshal.FreeHGlobal(dataPointer);
            Marshal.FreeHGlobal(filePointer);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        public uint StructSize;
        [MarshalAs(UnmanagedType.LPWStr)] public string FilePath;
        public IntPtr FileHandle;
        public IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        public uint StructSize;
        public IntPtr PolicyCallbackData;
        public IntPtr SipClientData;
        public uint UIChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr FileInfo;
        public uint StateAction;
        public IntPtr StateData;
        public IntPtr UrlReference;
        public uint ProviderFlags;
        public uint UIContext;
        public IntPtr SignatureSettings;
    }

    [DllImport("wintrust.dll", ExactSpelling = true, PreserveSig = true)]
    private static extern int WinVerifyTrust(IntPtr windowHandle, [MarshalAs(UnmanagedType.LPStruct)] Guid actionId, IntPtr trustData);
    private static SetupHelperResponse Success(SetupHelperRequest request, bool reboot, string detail) =>
        new(1, request.RequestId, true, reboot, [new DiagnosticRecord("HELPER_OK", "Setup operation complete", DiagnosticSeverity.Information, detail)]);
    private static SetupHelperResponse Failure(SetupHelperRequest request, string code, string detail) =>
        new(1, request.RequestId, false, false, [new DiagnosticRecord(code, "Setup operation failed", DiagnosticSeverity.Error, detail)], code);
}
