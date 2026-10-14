using Bdeyes.Models;

namespace Bdeyes.Services;

internal enum BdEventApplyResult
{
    Applied,
    Duplicate,
    Rebaseline,
}

internal sealed class BdIssueEventState
{
    private readonly Dictionary<string, BeadIssue> _issues =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _order = [];

    public BdIssueEventState(IReadOnlyList<BeadIssue> issues, long sequence)
    {
        foreach (var issue in issues)
        {
            _issues[issue.Id] = issue;
            _order.Add(issue.Id);
        }

        Sequence = sequence;
    }

    public long Sequence { get; private set; }

    public IReadOnlyList<BeadIssue> Issues =>
        _order
            .Where(_issues.ContainsKey)
            .Select(id => _issues[id])
            .ToArray();

    public BdEventApplyResult Apply(BdEventRecord record)
    {
        if (record.Sequence <= Sequence)
        {
            return BdEventApplyResult.Duplicate;
        }

        if (record.Sequence != Sequence + 1 || string.IsNullOrWhiteSpace(record.IssueId))
        {
            return BdEventApplyResult.Rebaseline;
        }

        var applied = record.Operation switch
        {
            "create" or "update" or "close" => ApplyIssue(record),
            "delete" => ApplyDelete(record),
            "dep_add" => ApplyDependency(record, add: true),
            "dep_remove" => ApplyDependency(record, add: false),
            "comment" => ApplyComment(record),
            _ => false,
        };
        if (!applied)
        {
            return BdEventApplyResult.Rebaseline;
        }

        Sequence = record.Sequence;
        return BdEventApplyResult.Applied;
    }

    private bool ApplyIssue(BdEventRecord record)
    {
        if (record.Issue is null ||
            !string.Equals(record.Issue.Id, record.IssueId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        Upsert(MergeIssue(record.IssueId, record.Issue));
        return true;
    }

    private bool ApplyDelete(BdEventRecord record)
    {
        _issues.Remove(record.IssueId);
        _order.RemoveAll(id => string.Equals(id, record.IssueId, StringComparison.OrdinalIgnoreCase));

        foreach (var issue in _issues.Values.ToArray())
        {
            var dependencies = issue.Dependencies
                .Where(dependency =>
                    !string.Equals(
                        dependency.DependsOnId,
                        record.IssueId,
                        StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (dependencies.Length != issue.Dependencies.Count)
            {
                _issues[issue.Id] = issue with { Dependencies = dependencies };
            }
        }

        RecomputeGraphMetadata();
        return true;
    }

    private bool ApplyDependency(BdEventRecord record, bool add)
    {
        if (record.Dependency is null ||
            string.IsNullOrWhiteSpace(record.Dependency.Kind) ||
            string.IsNullOrWhiteSpace(record.Dependency.Target))
        {
            return false;
        }

        if (record.Issue is not null)
        {
            if (!string.Equals(record.Issue.Id, record.IssueId, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            Upsert(MergeIssue(record.IssueId, record.Issue));
        }

        if (!_issues.TryGetValue(record.IssueId, out var source))
        {
            return record.Issue is null;
        }

        var dependencies = source.Dependencies.ToList();
        var index = dependencies.FindIndex(dependency =>
            string.Equals(
                dependency.DependsOnId,
                record.Dependency.Target,
                StringComparison.OrdinalIgnoreCase) &&
            string.Equals(
                dependency.Type,
                record.Dependency.Kind,
                StringComparison.OrdinalIgnoreCase));
        if (add && index < 0)
        {
            dependencies.Add(new BeadDependency
            {
                IssueId = record.IssueId,
                DependsOnId = record.Dependency.Target,
                Type = record.Dependency.Kind,
            });
        }
        else if (!add && index >= 0)
        {
            dependencies.RemoveAt(index);
        }

        _issues[source.Id] = source with { Dependencies = dependencies };
        RecomputeGraphMetadata();
        return true;
    }

    private bool ApplyComment(BdEventRecord record)
    {
        if (record.Issue is null ||
            record.Comment is null ||
            !string.Equals(record.Issue.Id, record.IssueId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var merged = MergeIssue(record.IssueId, record.Issue);
        var comments = merged.Comments.ToList();
        if (!comments.Any(comment =>
                string.Equals(comment.Id, record.Comment.Id, StringComparison.OrdinalIgnoreCase)))
        {
            comments.Add(new BeadComment
            {
                Id = record.Comment.Id,
                IssueId = record.IssueId,
                Author = record.Comment.Author,
                Text = record.Comment.Text,
                CreatedAt = record.Comment.CreatedAt,
            });
        }

        Upsert(merged with { Comments = comments });
        return true;
    }

    private BeadIssue MergeIssue(string id, BeadIssue incoming)
    {
        if (!_issues.TryGetValue(id, out var existing))
        {
            return incoming;
        }

        return incoming with
        {
            Parent = existing.Parent,
            Dependencies = existing.Dependencies,
            Dependents = existing.Dependents,
            Comments = existing.Comments,
            DependencyCount = existing.DependencyCount,
            DependentCount = existing.DependentCount,
            CommentCount = existing.CommentCount,
        };
    }

    private void Upsert(BeadIssue issue)
    {
        if (!_issues.ContainsKey(issue.Id))
        {
            _order.Add(issue.Id);
        }

        _issues[issue.Id] = issue;
    }

    private void RecomputeGraphMetadata()
    {
        var dependentCounts = _issues.Keys.ToDictionary(
            id => id,
            _ => 0,
            StringComparer.OrdinalIgnoreCase);
        foreach (var issue in _issues.Values)
        {
            foreach (var dependency in issue.Dependencies)
            {
                if (dependentCounts.TryGetValue(dependency.DependsOnId, out var count))
                {
                    dependentCounts[dependency.DependsOnId] = count + 1;
                }
            }
        }

        foreach (var issue in _issues.Values.ToArray())
        {
            var parent = issue.Dependencies
                .FirstOrDefault(dependency =>
                    string.Equals(
                        dependency.Type,
                        "parent-child",
                        StringComparison.OrdinalIgnoreCase))
                ?.DependsOnId;
            _issues[issue.Id] = issue with
            {
                Parent = parent,
                DependencyCount = issue.Dependencies.Count,
                DependentCount = dependentCounts.GetValueOrDefault(issue.Id),
            };
        }
    }
}
