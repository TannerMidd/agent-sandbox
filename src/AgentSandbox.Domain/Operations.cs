namespace AgentSandbox.Domain;

public sealed record OperationProgress(
    Guid OperationId,
    string Title,
    OperationState State,
    string Phase,
    int? Percent,
    long? BytesCompleted,
    long? BytesTotal,
    string? ErrorCode,
    string? Detail,
    DateTimeOffset UpdatedAt);

public sealed record TransferItemResult(
    IReadOnlyList<string> SourcePath,
    IReadOnlyList<string> DestinationPath,
    OperationState State,
    long? Bytes,
    string? ErrorCode,
    string? Detail);

public sealed record TransferJob(
    Guid Id,
    bool HostToGuest,
    FileConflictPolicy ConflictPolicy,
    OperationState State,
    IReadOnlyList<TransferItemResult> Items,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
