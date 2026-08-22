namespace Bdeyes.Models;

public readonly record struct WorkspaceContentRevision(int Length, ulong Fingerprint);

public sealed record BdWorkspaceSnapshot(
    string WorkspacePath,
    string BdVersion,
    DateTimeOffset LoadedAt,
    IReadOnlyList<BeadIssue> Issues,
    WorkspaceContentRevision ContentRevision);
