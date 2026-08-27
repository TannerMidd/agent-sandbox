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

    public string? Locate() => IsCanonicalExecutable(ExpectedPath) ? ExpectedPath : null;

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
            if (!string.Equals(fullPath, Path.GetFullPath(ExpectedPath), StringComparison.OrdinalIgnoreCase) ||
                !HasCanonicalWindowsRegistration()) return false;
            var info = FileVersionInfo.GetVersionInfo(path);
            return info.CompanyName?.Contains("Canonical", StringComparison.OrdinalIgnoreCase) == true &&
                   info.ProductName?.Contains("Multipass", StringComparison.OrdinalIgnoreCase) == true &&
                   WindowsAuthenticodeVerifier.IsTrustedSignedBy(fullPath, "Canonical");
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
