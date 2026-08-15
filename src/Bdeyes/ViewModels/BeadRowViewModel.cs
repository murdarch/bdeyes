using Avalonia.Media;
using Bdeyes.Models;
using Bdeyes.Services;

namespace Bdeyes.ViewModels;

public sealed class BeadRowViewModel
{
    public BeadRowViewModel(BeadFacts facts, BeadAnalyzer analyzer)
    {
        Facts = facts;
        Issue = facts.Issue;
        ParentTitle = analyzer.ParentTitle(Issue);
        StatusLabel = FormatStatus(Issue.Status);
        TypeLabel = FormatStatus(Issue.IssueType);
        PriorityLabel = $"P{Issue.Priority}";
        ActivityLabel = $"updated {AgeFormatter.Relative(facts.ActivityAge)}";
        ActivityTooltip = Issue.UpdatedAt?.ToLocalTime().ToString("f") ?? "No update timestamp";
        AssigneeLabel = string.IsNullOrWhiteSpace(Issue.Assignee) ? "unclaimed" : Issue.Assignee!;
        BlockLabel = facts.BlockSeverity switch
        {
            BlockSeverity.Direct => "blocked",
            BlockSeverity.Partial => "partially blocked",
            BlockSeverity.Complete => "fully blocked",
            _ => string.Empty,
        };
        BlockerSummary = BuildBlockerSummary(facts);
        ChildProgress = facts.Children.Count == 0
            ? string.Empty
            : $"{facts.ClosedChildCount}/{facts.Children.Count} children closed";
        SearchCorpus = string.Join(
            '\n',
            Issue.Id,
            Issue.Title,
            Issue.Description,
            Issue.Owner,
            Issue.Assignee,
            ParentTitle,
            string.Join(' ', Issue.Labels ?? []));

        (StatusBrush, StatusBackground) = Palette.ForStatus(Issue.Status, facts.BlockSeverity);
    }

    public BeadIssue Issue { get; }

    public BeadFacts Facts { get; }

    public string Id => Issue.Id;

    public string Title => Issue.Title;

    public string StatusLabel { get; }

    public string TypeLabel { get; }

    public string PriorityLabel { get; }

    public string ActivityLabel { get; }

    public string ActivityTooltip { get; }

    public string AssigneeLabel { get; }

    public string? ParentTitle { get; }

    public string BlockLabel { get; }

    public string BlockerSummary { get; }

    public string ChildProgress { get; }

    public string SearchCorpus { get; }

    public IBrush StatusBrush { get; }

    public IBrush StatusBackground { get; }

    public bool HasParent => !string.IsNullOrWhiteSpace(ParentTitle);

    public bool HasBlockState => Facts.BlockSeverity != BlockSeverity.None;

    public bool HasBlockerSummary => !string.IsNullOrWhiteSpace(BlockerSummary);

    public bool HasChildren => Facts.Children.Count > 0;

    public bool IsEpic => string.Equals(Issue.IssueType, "epic", StringComparison.OrdinalIgnoreCase);

    public bool IsClosed => Facts.IsClosed;

    public bool Matches(string query) => SearchCorpus.Contains(query, StringComparison.OrdinalIgnoreCase);

    public override string ToString() => $"{Id}: {Title}";

    private static string BuildBlockerSummary(BeadFacts facts)
    {
        if (facts.ActiveBlockerIds.Count > 0)
        {
            var shown = string.Join(", ", facts.ActiveBlockerIds.Take(2));
            return facts.ActiveBlockerIds.Count > 2
                ? $"waiting on {shown} +{facts.ActiveBlockerIds.Count - 2}"
                : $"waiting on {shown}";
        }

        return facts.BlockSeverity switch
        {
            BlockSeverity.Partial => "some open paths can move; others cannot",
            BlockSeverity.Complete => "every unresolved leaf is blocked",
            _ => string.Empty,
        };
    }

    internal static string FormatStatus(string value) =>
        string.Join(' ', value.Split(['_', '-'], StringSplitOptions.RemoveEmptyEntries)) switch
        {
            var text when text.Length == 0 => "Unknown",
            var text => char.ToUpperInvariant(text[0]) + text[1..],
        };
}

internal static class AgeFormatter
{
    public static string Relative(TimeSpan age)
    {
        if (age < TimeSpan.FromMinutes(1))
        {
            return "just now";
        }

        if (age < TimeSpan.FromHours(1))
        {
            return $"{Math.Max(1, (int)age.TotalMinutes)}m ago";
        }

        if (age < TimeSpan.FromDays(1))
        {
            return $"{Math.Max(1, (int)age.TotalHours)}h ago";
        }

        if (age < TimeSpan.FromDays(30))
        {
            return $"{Math.Max(1, (int)age.TotalDays)}d ago";
        }

        if (age < TimeSpan.FromDays(365))
        {
            return $"{Math.Max(1, (int)(age.TotalDays / 30))}mo ago";
        }

        return $"{Math.Max(1, (int)(age.TotalDays / 365))}y ago";
    }
}

internal static class Palette
{
    public static readonly IBrush Text = Brush.Parse("#E9F0F6");
    public static readonly IBrush Muted = Brush.Parse("#8EA0B3");
    public static readonly IBrush Active = Brush.Parse("#59D6A5");
    public static readonly IBrush ActiveBackground = Brush.Parse("#173A34");
    public static readonly IBrush Ready = Brush.Parse("#6EB7F2");
    public static readonly IBrush ReadyBackground = Brush.Parse("#18354D");
    public static readonly IBrush Blocked = Brush.Parse("#FF837A");
    public static readonly IBrush BlockedBackground = Brush.Parse("#4A292B");
    public static readonly IBrush Stale = Brush.Parse("#F2BA68");
    public static readonly IBrush StaleBackground = Brush.Parse("#433621");
    public static readonly IBrush Closed = Brush.Parse("#718094");
    public static readonly IBrush ClosedBackground = Brush.Parse("#252E39");

    public static (IBrush Foreground, IBrush Background) ForStatus(
        string status,
        BlockSeverity blockSeverity)
    {
        if (blockSeverity != BlockSeverity.None)
        {
            return (Blocked, BlockedBackground);
        }

        return status.ToLowerInvariant() switch
        {
            "in_progress" => (Active, ActiveBackground),
            "open" => (Ready, ReadyBackground),
            "closed" => (Closed, ClosedBackground),
            "deferred" => (Stale, StaleBackground),
            _ => (Muted, ClosedBackground),
        };
    }
}
