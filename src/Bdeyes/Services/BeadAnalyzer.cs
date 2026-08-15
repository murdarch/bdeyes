using Bdeyes.Models;

namespace Bdeyes.Services;

public sealed class BeadAnalyzer
{
    public static readonly TimeSpan StaleAfter = TimeSpan.FromDays(7);

    private readonly IReadOnlyList<BeadIssue> _issues;
    private readonly Dictionary<string, BeadIssue> _issuesById;
    private readonly Dictionary<string, IReadOnlyList<BeadIssue>> _childrenByParent;
    private readonly Dictionary<string, string> _parentByChild;
    private readonly DateTimeOffset _now;

    public BeadAnalyzer(IReadOnlyList<BeadIssue> issues, DateTimeOffset now)
    {
        _issues = issues;
        _now = now;
        _issuesById = issues
            .Where(issue => !string.IsNullOrWhiteSpace(issue.Id))
            .GroupBy(issue => issue.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        var parentCandidates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var issue in _issuesById.Values)
        {
            var parentId = ParentIdOf(issue);
            if (!string.IsNullOrWhiteSpace(parentId) &&
                !string.Equals(parentId, issue.Id, StringComparison.OrdinalIgnoreCase))
            {
                parentCandidates[issue.Id] = parentId;
            }
        }

        _parentByChild = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (childId, parentId) in parentCandidates
                     .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (!WouldCreateParentCycle(childId, parentId, _parentByChild))
            {
                _parentByChild[childId] = parentId;
            }
        }

        _childrenByParent = _issuesById.Values
            .Where(issue => _parentByChild.ContainsKey(issue.Id))
            .GroupBy(issue => _parentByChild[issue.Id], StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<BeadIssue>)group
                    .OrderBy(issue => issue.Priority)
                    .ThenByDescending(issue => issue.UpdatedAt)
                    .ThenBy(issue => issue.Id, StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<BeadIssue> Issues => _issues;

    public BeadIssue? Find(string id) => _issuesById.GetValueOrDefault(id);

    public BeadFacts Analyze(BeadIssue issue)
    {
        var children = ChildrenOf(issue.Id);
        var isClosed = IsClosed(issue);
        var directBlockers = ActiveBlockerIds(issue);
        var blockSeverity = GetBlockSeverity(issue, directBlockers.Count > 0 || IsStoredBlocked(issue));
        var activityAge = ActivityAge(issue);
        var isOpen = IsOpen(issue);
        var isReady = isOpen && blockSeverity == BlockSeverity.None && issue.DeferUntil is null;

        return new BeadFacts(
            issue,
            isClosed,
            IsActive(issue),
            isReady,
            isReady && string.IsNullOrWhiteSpace(issue.Assignee),
            !isClosed && activityAge >= StaleAfter,
            blockSeverity,
            activityAge,
            directBlockers,
            children,
            children.Count(IsClosed),
            children.Count(child => !IsClosed(child)));
    }

    public IReadOnlyList<BeadIssue> ChildrenOf(string issueId) =>
        _childrenByParent.GetValueOrDefault(issueId) ?? [];

    public BeadIssue? ParentOf(BeadIssue issue) =>
        _parentByChild.TryGetValue(issue.Id, out var parentId)
            ? Find(parentId)
            : null;

    public IReadOnlyList<BeadIssue> AncestorsOf(string issueId)
    {
        var ancestors = new List<BeadIssue>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { issueId };
        var current = Find(issueId);

        while (current is not null)
        {
            var parent = ParentOf(current);
            if (parent is null || !seen.Add(parent.Id))
            {
                break;
            }

            ancestors.Add(parent);
            current = parent;
        }

        ancestors.Reverse();
        return ancestors;
    }

    public IReadOnlyList<BeadIssue> DescendantsOf(string issueId)
    {
        var descendants = new List<BeadIssue>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { issueId };
        var pending = new Stack<BeadIssue>(ChildrenOf(issueId).Reverse());

        while (pending.Count > 0)
        {
            var candidate = pending.Pop();
            if (!seen.Add(candidate.Id))
            {
                continue;
            }

            descendants.Add(candidate);
            foreach (var child in ChildrenOf(candidate.Id).Reverse())
            {
                pending.Push(child);
            }
        }

        return descendants;
    }

    public IReadOnlyList<string> ActiveBlockerIds(BeadIssue issue)
    {
        var blockerIds = new List<string>();
        foreach (var dependency in issue.Dependencies ?? [])
        {
            if (!string.Equals(dependency.Type, "blocks", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!_issuesById.TryGetValue(dependency.DependsOnId, out var blocker) || !IsClosed(blocker))
            {
                blockerIds.Add(dependency.DependsOnId);
            }
        }

        return blockerIds;
    }

    public string? ParentTitle(BeadIssue issue) => ParentOf(issue)?.Title;

    private BlockSeverity GetBlockSeverity(BeadIssue issue, bool isDirectlyBlocked)
    {
        if (!IsEpic(issue))
        {
            return isDirectlyBlocked ? BlockSeverity.Direct : BlockSeverity.None;
        }

        if (isDirectlyBlocked)
        {
            return BlockSeverity.Complete;
        }

        var unresolvedDescendants = DescendantsOf(issue.Id)
            .Where(descendant => !IsClosed(descendant))
            .ToArray();
        if (unresolvedDescendants.Length == 0)
        {
            return BlockSeverity.None;
        }

        var unresolvedIds = unresolvedDescendants
            .Select(descendant => descendant.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var leaves = unresolvedDescendants
            .Where(descendant => !ChildrenOf(descendant.Id).Any(child => unresolvedIds.Contains(child.Id)))
            .ToArray();
        if (leaves.Length == 0)
        {
            return BlockSeverity.None;
        }

        var blockedLeafCount = leaves.Count(HasDirectBlocker);
        return blockedLeafCount switch
        {
            0 => BlockSeverity.None,
            var count when count == leaves.Length => BlockSeverity.Complete,
            _ => BlockSeverity.Partial,
        };
    }

    private bool HasDirectBlocker(BeadIssue issue) =>
        IsStoredBlocked(issue) || ActiveBlockerIds(issue).Count > 0;

    private TimeSpan ActivityAge(BeadIssue issue)
    {
        var activityAt = issue.UpdatedAt ?? issue.CreatedAt ?? _now;
        return activityAt >= _now ? TimeSpan.Zero : _now - activityAt;
    }

    private static string? ParentIdOf(BeadIssue issue)
    {
        if (!string.IsNullOrWhiteSpace(issue.Parent))
        {
            return issue.Parent;
        }

        return (issue.Dependencies ?? [])
            .FirstOrDefault(dependency =>
                string.Equals(
                    dependency.Type,
                    "parent-child",
                    StringComparison.OrdinalIgnoreCase))
            ?.DependsOnId;
    }

    private static bool WouldCreateParentCycle(
        string childId,
        string parentId,
        IReadOnlyDictionary<string, string> acceptedParents)
    {
        var current = parentId;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (seen.Add(current))
        {
            if (string.Equals(current, childId, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!acceptedParents.TryGetValue(current, out var next))
            {
                return false;
            }

            current = next;
        }

        return true;
    }

    private static bool IsEpic(BeadIssue issue) =>
        string.Equals(issue.IssueType, "epic", StringComparison.OrdinalIgnoreCase);

    private static bool IsOpen(BeadIssue issue) =>
        string.Equals(issue.Status, "open", StringComparison.OrdinalIgnoreCase);

    private static bool IsActive(BeadIssue issue) =>
        string.Equals(issue.Status, "in_progress", StringComparison.OrdinalIgnoreCase);

    private static bool IsStoredBlocked(BeadIssue issue) =>
        string.Equals(issue.Status, "blocked", StringComparison.OrdinalIgnoreCase);

    private static bool IsClosed(BeadIssue issue) =>
        string.Equals(issue.Status, "closed", StringComparison.OrdinalIgnoreCase);
}
