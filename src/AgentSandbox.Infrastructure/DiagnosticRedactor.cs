using System.Text.RegularExpressions;

namespace AgentSandbox.Infrastructure;

public static partial class DiagnosticRedactor
{
    public static string Redact(string value)
    {
        var result = value.Replace(Environment.UserName, "<user>", StringComparison.OrdinalIgnoreCase);
        result = HomePath().Replace(result, "$1<user>");
        result = SecretAssignment().Replace(result, "$1=<redacted>");
        result = BearerToken().Replace(result, "$1 <redacted>");
        return result;
    }

    [GeneratedRegex(@"(?i)(C:\\Users\\|/home/)[^\\/\s]+")]
    private static partial Regex HomePath();
    [GeneratedRegex(@"(?i)\b(api[_-]?key|token|password|secret)\s*=\s*[^\s,;]+")]
    private static partial Regex SecretAssignment();
    [GeneratedRegex(@"(?i)\b(Bearer)\s+[A-Za-z0-9._~+/=-]+")]
    private static partial Regex BearerToken();
}
