using System.Text;
using AgentSandbox.Domain;

namespace AgentSandbox.Infrastructure;

public static class CloudInitRenderer
{
    public const string ConfigurationMarker = "      # {{AGENT_SANDBOX_HARDENING_CONFIGURATION}}";

    public static string Render(string template, SandboxHardeningOptions options)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        if (!template.Contains(ConfigurationMarker, StringComparison.Ordinal))
            throw new InvalidDataException("The cloud-init template does not contain the hardening configuration marker.");

        var configuration = string.Join('\n', new[]
        {
            Assignment("hardening_preset", options.PresetId),
            Assignment("automatic_security_updates", options.AutomaticSecurityUpdates),
            Assignment("kernel_hardening", options.KernelHardening),
            Assignment("restrict_unprivileged_features", options.RestrictUnprivilegedFeatures),
            Assignment("audit_security_events", options.AuditSecurityEvents),
            Assignment("network_access", options.NetworkAccess switch
            {
                NetworkAccessPolicy.Unrestricted => "unrestricted",
                NetworkAccessPolicy.WebOnly => "web-only",
                NetworkAccessPolicy.Offline => "offline",
                _ => throw new ArgumentOutOfRangeException(nameof(options))
            }),
            Assignment("allow_administrative_tools", options.AllowAdministrativeTools)
        });
        return template.Replace(ConfigurationMarker, configuration, StringComparison.Ordinal);
    }

    public static async Task<string> CreateTemporaryAsync(string templatePath, SandboxHardeningOptions options, CancellationToken cancellationToken)
    {
        if (!File.Exists(templatePath)) throw new FileNotFoundException("Cloud-init configuration was not found.", templatePath);
        var rendered = Render(await File.ReadAllTextAsync(templatePath, cancellationToken), options);
        var directory = Path.Combine(Path.GetTempPath(), "AgentSandbox", "cloud-init");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"cloud-init-{Guid.NewGuid():N}.yaml");
        await File.WriteAllTextAsync(path, rendered, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken);
        return path;
    }

    private static string Assignment(string name, bool value) => Assignment(name, value ? "true" : "false");

    private static string Assignment(string name, string value)
    {
        if (value.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_')))
            throw new ArgumentException("Hardening configuration contains an unsafe value.", nameof(value));
        return $"      {name}='{value}'";
    }
}
