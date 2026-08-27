using System.Diagnostics;

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
            var info = FileVersionInfo.GetVersionInfo(path);
            return (info.CompanyName?.Contains("Canonical", StringComparison.OrdinalIgnoreCase) ?? false) ||
                   (info.ProductName?.Contains("Multipass", StringComparison.OrdinalIgnoreCase) ?? false);
        }
        catch { return false; }
    }
}
