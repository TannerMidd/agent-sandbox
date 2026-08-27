using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using AgentSandbox.Application;
using Microsoft.Win32.SafeHandles;

namespace AgentSandbox.Infrastructure;

public sealed class TerminalService(IMultipassLocator locator) : ITerminalService
{
    private const string GuestShellCommand = "cd /home/ubuntu/work && export PATH=/home/ubuntu/.local/bin:$PATH && exec bash -i";

    public Task<ITerminalSession> OpenEmbeddedAsync(string instanceName, CancellationToken cancellationToken = default)
    {
        ValidateInstance(instanceName);
        cancellationToken.ThrowIfCancellationRequested();
        var executable = locator.Locate() ?? throw new FileNotFoundException("Multipass was not found.");
        return Task.FromResult<ITerminalSession>(ConPtyTerminalSession.Start(executable, ShellArguments(instanceName), 120, 32));
    }

    public Task OpenExternalAsync(string instanceName, CancellationToken cancellationToken = default)
    {
        ValidateInstance(instanceName);
        cancellationToken.ThrowIfCancellationRequested();
        var executable = locator.Locate() ?? throw new FileNotFoundException("Multipass was not found.");
        var terminal = WindowsTerminalAliasPath(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        if (!File.Exists(terminal))
            throw new FileNotFoundException("Windows Terminal is not installed or its app execution alias is disabled. Use Embedded terminal or enable the wt.exe alias in Windows Settings.", terminal);
        var start = new ProcessStartInfo { FileName = terminal, UseShellExecute = true };
        start.ArgumentList.Add("new-tab"); start.ArgumentList.Add("--title"); start.ArgumentList.Add("Agent Sandbox");
        start.ArgumentList.Add(executable);
        foreach (var argument in ShellArguments(instanceName)) start.ArgumentList.Add(argument);
        _ = Process.Start(start) ?? throw new InvalidOperationException("Windows accepted the terminal request but did not create a launcher process.");
        return Task.CompletedTask;
    }

    public static string WindowsTerminalAliasPath(string localApplicationData) =>
        Path.Combine(Path.GetFullPath(localApplicationData), "Microsoft", "WindowsApps", "wt.exe");

    private static IReadOnlyList<string> ShellArguments(string instanceName) =>
        ["exec", instanceName, "--", "bash", "-lc", GuestShellCommand];

    private static void ValidateInstance(string value)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(value, "^[A-Za-z][A-Za-z0-9-]{0,62}$"))
            throw new ArgumentException("Invalid sandbox instance name.", nameof(value));
    }
}

internal sealed class ConPtyTerminalSession : ITerminalSession
{
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint Infinite = 0xFFFFFFFF;
    private static readonly IntPtr PseudoConsoleAttribute = (IntPtr)0x00020016;

    private readonly SafeFileHandle inputWrite;
    private readonly SafeFileHandle outputRead;
    private readonly IntPtr pseudoConsole;
    private readonly IntPtr processHandle;
    private readonly IntPtr threadHandle;
    private readonly Task<int> completion;
    private bool disposed;

    private ConPtyTerminalSession(SafeFileHandle inputWrite, SafeFileHandle outputRead, IntPtr pseudoConsole, IntPtr processHandle, IntPtr threadHandle)
    {
        this.inputWrite = inputWrite;
        this.outputRead = outputRead;
        this.pseudoConsole = pseudoConsole;
        this.processHandle = processHandle;
        this.threadHandle = threadHandle;
        Input = new FileStream(inputWrite, FileAccess.Write, 4096, isAsync: true);
        Output = new FileStream(outputRead, FileAccess.Read, 4096, isAsync: true);
        completion = WaitAsync();
    }

    public Stream Input { get; }
    public Stream Output { get; }
    public Task<int> Completion => completion;

    public static ConPtyTerminalSession Start(string executable, IReadOnlyList<string> arguments, int columns, int rows)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
            throw new PlatformNotSupportedException("Embedded terminals require Windows ConPTY support.");

        CreatePipe(out var inputReadRaw, out var inputWriteRaw, IntPtr.Zero, 0).ThrowIfFalse("CreatePipe(input)");
        CreatePipe(out var outputReadRaw, out var outputWriteRaw, IntPtr.Zero, 0).ThrowIfFalse("CreatePipe(output)");
        var inputRead = new SafeFileHandle(inputReadRaw, ownsHandle: true);
        var inputWrite = new SafeFileHandle(inputWriteRaw, ownsHandle: true);
        var outputRead = new SafeFileHandle(outputReadRaw, ownsHandle: true);
        var outputWrite = new SafeFileHandle(outputWriteRaw, ownsHandle: true);
        IntPtr pseudoConsole = IntPtr.Zero;
        IntPtr attributeList = IntPtr.Zero;
        IntPtr pseudoConsoleValue = IntPtr.Zero;
        try
        {
            ThrowIfFailed(CreatePseudoConsole(new Coord((short)columns, (short)rows), inputRead.DangerousGetHandle(), outputWrite.DangerousGetHandle(), 0, out pseudoConsole), "CreatePseudoConsole");
            inputRead.Dispose(); outputWrite.Dispose();

            nuint bytes = 0;
            _ = InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref bytes);
            attributeList = Marshal.AllocHGlobal((nint)bytes);
            InitializeProcThreadAttributeList(attributeList, 1, 0, ref bytes).ThrowIfFalse("InitializeProcThreadAttributeList");
            pseudoConsoleValue = Marshal.AllocHGlobal(IntPtr.Size);
            Marshal.WriteIntPtr(pseudoConsoleValue, pseudoConsole);
            UpdateProcThreadAttribute(attributeList, 0, PseudoConsoleAttribute, pseudoConsoleValue, (nuint)IntPtr.Size, IntPtr.Zero, IntPtr.Zero)
                .ThrowIfFalse("UpdateProcThreadAttribute");

            var startup = new StartupInfoEx { StartupInfo = new StartupInfo { Cb = Marshal.SizeOf<StartupInfoEx>() }, AttributeList = attributeList };
            var command = new StringBuilder(BuildCommandLine(executable, arguments));
            if (!CreateProcessW(null, command, IntPtr.Zero, IntPtr.Zero, false, ExtendedStartupInfoPresent | CreateUnicodeEnvironment,
                    IntPtr.Zero, Path.GetDirectoryName(executable), ref startup, out var process))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateProcess for the embedded terminal failed.");

            return new ConPtyTerminalSession(inputWrite, outputRead, pseudoConsole, process.Process, process.Thread);
        }
        catch
        {
            inputRead.Dispose(); inputWrite.Dispose(); outputRead.Dispose(); outputWrite.Dispose();
            if (pseudoConsole != IntPtr.Zero) ClosePseudoConsole(pseudoConsole);
            throw;
        }
        finally
        {
            if (attributeList != IntPtr.Zero) { DeleteProcThreadAttributeList(attributeList); Marshal.FreeHGlobal(attributeList); }
            if (pseudoConsoleValue != IntPtr.Zero) Marshal.FreeHGlobal(pseudoConsoleValue);
        }
    }

    public void Resize(int columns, int rows)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (columns is < 20 or > short.MaxValue || rows is < 5 or > short.MaxValue) throw new ArgumentOutOfRangeException(nameof(columns));
        ThrowIfFailed(ResizePseudoConsole(pseudoConsole, new Coord((short)columns, (short)rows)), "ResizePseudoConsole");
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        try { await Input.DisposeAsync(); } catch { }
        try
        {
            if (WaitForSingleObject(processHandle, 0) == 0x00000102) _ = TerminateProcess(processHandle, 0);
        }
        catch { }
        try { await Output.DisposeAsync(); } catch { }
        ClosePseudoConsole(pseudoConsole);
        _ = CloseHandle(threadHandle);
        _ = CloseHandle(processHandle);
    }

    private Task<int> WaitAsync() => Task.Run(() =>
    {
        _ = WaitForSingleObject(processHandle, Infinite);
        return GetExitCodeProcess(processHandle, out var exitCode) ? unchecked((int)exitCode) : -1;
    });

    private static string BuildCommandLine(string executable, IReadOnlyList<string> arguments) =>
        string.Join(' ', new[] { executable }.Concat(arguments).Select(Quote));

    private static string Quote(string value)
    {
        if (value.Length > 0 && value.All(ch => !char.IsWhiteSpace(ch) && ch != '"')) return value;
        var output = new StringBuilder("\"");
        var slashes = 0;
        foreach (var ch in value)
        {
            if (ch == '\\') { slashes++; continue; }
            if (ch == '"') output.Append('\\', slashes * 2 + 1).Append(ch);
            else { output.Append('\\', slashes).Append(ch); }
            slashes = 0;
        }
        output.Append('\\', slashes * 2).Append('"');
        return output.ToString();
    }

    private static void ThrowIfFailed(int hresult, string operation)
    {
        if (hresult < 0) Marshal.ThrowExceptionForHR(hresult, new IntPtr(-1));
    }

    [StructLayout(LayoutKind.Sequential)] private readonly record struct Coord(short X, short Y);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Cb; public string? Reserved; public string? Desktop; public string? Title;
        public int X; public int Y; public int XSize; public int YSize; public int XCountChars; public int YCountChars;
        public int FillAttribute; public int Flags; public short ShowWindow; public short Reserved2; public IntPtr Reserved2Ptr;
        public IntPtr StdInput; public IntPtr StdOutput; public IntPtr StdError;
    }

    [StructLayout(LayoutKind.Sequential)] private struct StartupInfoEx { public StartupInfo StartupInfo; public IntPtr AttributeList; }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInformation { public IntPtr Process; public IntPtr Thread; public uint ProcessId; public uint ThreadId; }

    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreatePipe(out IntPtr readPipe, out IntPtr writePipe, IntPtr pipeAttributes, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern int CreatePseudoConsole(Coord size, IntPtr input, IntPtr output, uint flags, out IntPtr pseudoConsole);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern int ResizePseudoConsole(IntPtr pseudoConsole, Coord size);
    [DllImport("kernel32.dll")] private static extern void ClosePseudoConsole(IntPtr pseudoConsole);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref nuint size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, nuint size, IntPtr previous, IntPtr returnSize);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr list);
#pragma warning disable CA1838 // CreateProcessW requires a mutable, null-terminated command-line buffer.
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(string? applicationName, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint creationFlags, IntPtr environment, string? currentDirectory,
        ref StartupInfoEx startupInfo, out ProcessInformation processInformation);
#pragma warning restore CA1838
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool TerminateProcess(IntPtr process, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
}

internal static class Win32ResultExtensions
{
    public static void ThrowIfFalse(this bool result, string operation)
    {
        if (!result) throw new Win32Exception(Marshal.GetLastWin32Error(), $"{operation} failed.");
    }
}
