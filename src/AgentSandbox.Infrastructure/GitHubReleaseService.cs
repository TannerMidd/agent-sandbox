using System.Net.Http.Headers;
using System.Text.Json;
using AgentSandbox.Application;

namespace AgentSandbox.Infrastructure;

public sealed class GitHubReleaseService(HttpClient httpClient, string repository) : IReleaseService
{
    public async Task<ReleaseInfo?> CheckAsync(Version currentVersion, CancellationToken cancellationToken = default)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(repository, "^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$"))
            throw new InvalidOperationException("GitHub repository must use owner/name form.");
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{repository}/releases/latest");
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("AgentSandbox", currentVersion.ToString()));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;
        var tag = root.GetProperty("tag_name").GetString()?.TrimStart('v') ?? "";
        if (!Version.TryParse(tag.Split('-')[0], out var version) || version <= currentVersion) return null;
        var page = new Uri(root.GetProperty("html_url").GetString()!, UriKind.Absolute);
        return new ReleaseInfo(version, page, root.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "", root.TryGetProperty("prerelease", out var pre) && pre.GetBoolean());
    }
}
