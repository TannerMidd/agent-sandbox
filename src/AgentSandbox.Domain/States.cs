namespace AgentSandbox.Domain;

public enum SetupState
{
    Welcome,
    CheckingHost,
    HyperVRequired,
    RebootRequired,
    MultipassRequired,
    StorageRequired,
    ResourceConfiguration,
    Provisioning,
    InstallingPresets,
    Ready,
    NeedsReview
}

public enum SandboxState
{
    Missing,
    Stopped,
    Starting,
    Running,
    Stopping,
    Suspended,
    Failed,
    Unknown
}

public enum OperationState
{
    Queued,
    Running,
    Succeeded,
    Warning,
    Failed,
    Canceled,
    CleanupPending
}

public enum DiagnosticSeverity { Information, Warning, Error }
public enum GuestEntryKind { File, Directory, Symlink, Other }
public enum FileConflictPolicy { Fail, Overwrite, Rename }
public enum FileRootAccess { ReadOnly, ReadWrite }
