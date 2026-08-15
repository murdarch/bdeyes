namespace Bdeyes.Models;

public enum BlockSeverity
{
    None,
    Direct,
    Partial,
    Complete,
}

public sealed record BeadFacts(
    BeadIssue Issue,
    bool IsClosed,
    bool IsActive,
    bool IsReady,
    bool IsUnclaimed,
    bool IsStale,
    BlockSeverity BlockSeverity,
    TimeSpan ActivityAge,
    IReadOnlyList<string> ActiveBlockerIds,
    IReadOnlyList<BeadIssue> Children,
    int ClosedChildCount,
    int OpenChildCount);
