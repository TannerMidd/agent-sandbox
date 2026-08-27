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
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{repository}/releases?per_page=20");
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("AgentSandbox", currentVersion.ToString()));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (document.RootElement.ValueKind != JsonValueKind.Array) throw new JsonException("GitHub releases response was not an array.");
        ReleaseInfo? newest = null;
        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (item.TryGetProperty("draft", out var draft) && draft.GetBoolean()) continue;
            var tag = item.GetProperty("tag_name").GetString()?.TrimStart('v') ?? "";
            if (!Version.TryParse(tag.Split('-')[0], out var version) || version <= currentVersion || newest?.Version >= version) continue;
            var page = new Uri(item.GetProperty("html_url").GetString()!, UriKind.Absolute);
            newest = new ReleaseInfo(version, page,
                item.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "",
                item.TryGetProperty("prerelease", out var pre) && pre.GetBoolean());
        }
        return newest;
    }
}
