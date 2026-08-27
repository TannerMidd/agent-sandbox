using System.Diagnostics;
using Microsoft.Win32;

namespace AgentSandbox.Infrastructure;

public interface IMultipassLocator
{
    string? Locate();
}

public sealed class MultipassLocator : IMultipassLocator
{
    private static readonly string ExpectedPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        "Multipass", "bin", "multipass.exe");

    public string? Locate()
    {
        if (IsCanonicalExecutable(ExpectedPath)) return ExpectedPath;
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.GetFullPath(Path.Combine(directory, "multipass.exe"));
                if (IsCanonicalExecutable(candidate)) return candidate;
            }
            catch { }
        }
        return null;
    }

    public static bool IsCanonicalExecutable(string path)
    {
        if (!File.Exists(path)) return false;
        try
        {
            var fullPath = Path.GetFullPath(path);
            var file = new FileInfo(fullPath);
            if (file.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
                file.Directory?.Attributes.HasFlag(FileAttributes.ReparsePoint) == true)
                return false;
            var info = FileVersionInfo.GetVersionInfo(path);
            if ((info.CompanyName?.Contains("Canonical", StringComparison.OrdinalIgnoreCase) ?? false) ||
                (info.ProductName?.Contains("Multipass", StringComparison.OrdinalIgnoreCase) ?? false))
                return true;
            return string.Equals(fullPath, Path.GetFullPath(ExpectedPath), StringComparison.OrdinalIgnoreCase) &&
                   HasCanonicalWindowsRegistration();
        }
        catch { return false; }
    }

    public static bool IsCanonicalRegistration(string? displayName, string? publisher) =>
        string.Equals(displayName, "Multipass", StringComparison.Ordinal) &&
        (publisher?.StartsWith("Canonical", StringComparison.OrdinalIgnoreCase) ?? false);

    private static bool HasCanonicalWindowsRegistration()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using var uninstall = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
            if (uninstall is null) return false;
            foreach (var name in uninstall.GetSubKeyNames())
            {
                using var product = uninstall.OpenSubKey(name);
                if (IsCanonicalRegistration(product?.GetValue("DisplayName") as string, product?.GetValue("Publisher") as string))
                    return true;
            }
        }
        catch { }
        return false;
    }
}
