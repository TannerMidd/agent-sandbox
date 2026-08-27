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

    public static bool IsCanonicalExecutable(string path) =>
        IsCanonicalExecutable(path, ExpectedPath, HasCanonicalWindowsRegistration());

    internal static bool IsCanonicalExecutable(string path, string expectedPath, bool hasCanonicalWindowsRegistration)
    {
        // Canonical signs the installer, but Multipass 1.16.3 ships this executable without
        // Authenticode or version-resource publisher metadata. Validate its protected install
        // location and Canonical's machine registration instead of rejecting the official binary.
        if (!hasCanonicalWindowsRegistration || !File.Exists(path)) return false;
        try
        {
            var fullPath = Path.GetFullPath(path);
            if (!string.Equals(fullPath, Path.GetFullPath(expectedPath), StringComparison.OrdinalIgnoreCase))
                return false;
            var file = new FileInfo(fullPath);
            if (file.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
                file.Directory?.Attributes.HasFlag(FileAttributes.ReparsePoint) == true)
                return false;
            return true;
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
