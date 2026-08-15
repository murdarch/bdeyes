namespace Bdeyes.Models;

public sealed record BdWorkspaceSnapshot(
    string WorkspacePath,
    string BdVersion,
    DateTimeOffset LoadedAt,
    IReadOnlyList<BeadIssue> Issues);
