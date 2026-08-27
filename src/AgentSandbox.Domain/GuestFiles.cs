using System.Text.Json.Serialization;

namespace AgentSandbox.Domain;

public static class GuestRoots
{
    public const string Work = "work";
    public const string System = "system";
}

public static class GuestFileOperations
{
    public static readonly ISet<string> Allowed = new HashSet<string>(StringComparer.Ordinal)
    {
        "list", "stat", "search", "upload", "download", "mkdir", "createFile", "rename",
        "copy", "move", "trash", "restore", "purge", "readText", "writeText", "chmod",
        "archive", "extract"
    };
}

public sealed record GuestFileRequest
{
    [JsonPropertyName("v")] public int Version { get; init; } = 1;
    [JsonPropertyName("id")] public Guid Id { get; init; } = Guid.NewGuid();
    [JsonPropertyName("op")] public required string Operation { get; init; }
    [JsonPropertyName("rootId")] public string RootId { get; init; } = GuestRoots.Work;
    [JsonPropertyName("relativePath")] public IReadOnlyList<string> RelativePath { get; init; } = [];
    [JsonPropertyName("destinationPath")] public IReadOnlyList<string>? DestinationPath { get; init; }
    [JsonPropertyName("pageSize")] public int PageSize { get; init; } = 200;
    [JsonPropertyName("cursor")] public string? Cursor { get; init; }
    [JsonPropertyName("conflict")] public string Conflict { get; init; } = "fail";
    [JsonPropertyName("content")] public string? Content { get; init; }
    [JsonPropertyName("mode")] public int? Mode { get; init; }
    [JsonPropertyName("expected")] public GuestFileExpectation? Expected { get; init; }
}

public sealed record GuestFileExpectation(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("mtimeNs")] long ModifiedNanoseconds,
    [property: JsonPropertyName("mode")] int Mode);

public sealed record GuestFileEntry(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("mtimeNs")] long ModifiedNanoseconds,
    [property: JsonPropertyName("mode")] int Mode,
    [property: JsonPropertyName("uid")] int UserId,
    [property: JsonPropertyName("gid")] int GroupId,
    [property: JsonPropertyName("linkTarget")] string? LinkTarget);

public sealed record GuestFileError(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message);

public sealed record GuestFileResponse(
    [property: JsonPropertyName("v")] int Version,
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("ok")] bool IsSuccess,
    [property: JsonPropertyName("rootId")] string RootId,
    [property: JsonPropertyName("relativePath")] IReadOnlyList<string> RelativePath,
    [property: JsonPropertyName("entries")] IReadOnlyList<GuestFileEntry> Entries,
    [property: JsonPropertyName("revision")] string? Revision,
    [property: JsonPropertyName("nextCursor")] string? NextCursor,
    [property: JsonPropertyName("unstable")] bool IsUnstable,
    [property: JsonPropertyName("content")] string? Content,
    [property: JsonPropertyName("warnings")] IReadOnlyList<string> Warnings,
    [property: JsonPropertyName("error")] GuestFileError? Error);

public static class GuestPathPolicy
{
    public static void ValidateRequest(GuestFileRequest request)
    {
        if (request.Version != 1) throw new ArgumentOutOfRangeException(nameof(request), "Unsupported protocol version.");
        if (!GuestFileOperations.Allowed.Contains(request.Operation)) throw new ArgumentException("Unsupported operation.", nameof(request));
        if (request.RootId is not (GuestRoots.Work or GuestRoots.System)) throw new ArgumentException("Unknown guest root.", nameof(request));
        if (request.PageSize is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(request), "Page size must be between 1 and 200.");
        ValidateComponents(request.RelativePath);
        if (request.DestinationPath is not null) ValidateComponents(request.DestinationPath);
        if (request.RootId == GuestRoots.System && IsMutation(request.Operation))
            throw new UnauthorizedAccessException("System-root operations are read-only.");
    }

    public static void ValidateComponents(IEnumerable<string> components)
    {
        foreach (var component in components)
        {
            if (string.IsNullOrEmpty(component) || component is "." or ".." ||
                component.IndexOfAny(['\0', '/', '\\']) >= 0)
                throw new ArgumentException("Path components cannot be empty, dot segments, or contain NUL or separators.", nameof(components));
        }
    }

    public static bool IsMutation(string operation) => operation is not ("list" or "stat" or "search" or "download" or "readText");
}
