using Avalonia;
using Avalonia.Media;
using Bdeyes.Models;
using Bdeyes.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Bdeyes.ViewModels;

public sealed partial class BeadRowViewModel : ViewModelBase
{
    private const double IndentStep = 17;

    public BeadRowViewModel(
        BeadFacts facts,
        BeadAnalyzer analyzer,
        Action<BeadRowViewModel> toggleExpansion)
    {
        Facts = facts;
        Issue = facts.Issue;
        ParentTitle = analyzer.ParentTitle(Issue);
        StatusLabel = FormatStatus(Issue.Status);
        TypeLabel = FormatStatus(Issue.IssueType);
        PriorityLabel = $"P{Issue.Priority}";
        UpdatedLabel = AgeFormatter.Relative(facts.ActivityAge);
        ActivityLabel = $"updated {UpdatedLabel}";
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
            : $"{facts.ClosedChildCount}/{facts.Children.Count} closed";
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
        SignalLabel = BuildSignalLabel(facts, ChildProgress);
        SignalTooltip = string.IsNullOrWhiteSpace(BlockerSummary) ? SignalLabel : BlockerSummary;
        SignalBrush = facts.BlockSeverity != BlockSeverity.None
            ? Palette.Blocked
            : facts.IsActive
                ? Palette.Active
                : facts.IsStale
                    ? Palette.Stale
                    : facts.IsReady
                        ? Palette.Ready
                        : Palette.Muted;
        ToggleExpansionCommand = new RelayCommand(() => toggleExpansion(this));
    }

    public BeadIssue Issue { get; }

    public BeadFacts Facts { get; }

    public List<BeadRowViewModel> Children { get; } = [];

    public BeadRowViewModel? Parent { get; private set; }

    public string Id => Issue.Id;

    public string Title => Issue.Title;

    public string StatusLabel { get; }

    public string TypeLabel { get; }

    public string PriorityLabel { get; }

    public string UpdatedLabel { get; }

    public string ActivityLabel { get; }

    public string ActivityTooltip { get; }

    public string AssigneeLabel { get; }

    public string? ParentTitle { get; }

    public string BlockLabel { get; }

    public string BlockerSummary { get; }

    public string ChildProgress { get; }

    public string SignalLabel { get; }

    public string SignalTooltip { get; }

    public string SearchCorpus { get; }

    public IBrush StatusBrush { get; }

    public IBrush StatusBackground { get; }

    public IBrush SignalBrush { get; }

    public IRelayCommand ToggleExpansionCommand { get; }

    public int Depth { get; private set; }

    public Thickness IndentMargin => new(Depth * IndentStep, 0, 0, 0);

    public Thickness GuideMargin => new(Math.Max(8, (Depth * IndentStep) - 8), 0, 0, 0);

    public bool HasParent => Parent is not null;

    public bool HasBlockState => Facts.BlockSeverity != BlockSeverity.None;

    public bool HasBlockerSummary => !string.IsNullOrWhiteSpace(BlockerSummary);

    public bool HasChildren => Children.Count > 0;

    public bool IsEpic => string.Equals(Issue.IssueType, "epic", StringComparison.OrdinalIgnoreCase);

    public bool IsClosed => Facts.IsClosed;

    public bool ShowContextBadge => IsContextOnly;

    public string ExpandGlyph => IsExpanded ? "⌄" : "›";

    public string DisclosureAutomationName => $"{(IsExpanded ? "Collapse" : "Expand")} {Id}";

    public string HierarchyAutomationHelp
    {
        get
        {
            var location = Parent is null
                ? "Top-level outline item"
                : $"Level {Depth + 1}, child of {Parent.Id}";
            var disclosure = HasVisibleChildren
                ? IsExpanded ? "Expanded" : "Collapsed"
                : "No visible children";
            return $"{location}. {disclosure}. {StatusLabel}; {AssigneeLabel}; {SignalLabel}.";
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExpandGlyph))]
    [NotifyPropertyChangedFor(nameof(DisclosureAutomationName))]
    [NotifyPropertyChangedFor(nameof(HierarchyAutomationHelp))]
    public partial bool IsExpanded { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowContextBadge))]
    public partial bool IsContextOnly { get; set; }

    [ObservableProperty]
    public partial bool IsDirectMatch { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HierarchyAutomationHelp))]
    public partial bool HasVisibleChildren { get; set; }

    public bool Matches(string query) => SearchCorpus.Contains(query, StringComparison.OrdinalIgnoreCase);

    public override string ToString() => $"{Id}: {Title}";

    internal void AttachTo(BeadRowViewModel parent)
    {
        Parent = parent;
        parent.Children.Add(this);
        OnPropertyChanged(nameof(HasParent));
        OnPropertyChanged(nameof(HierarchyAutomationHelp));
    }

    internal void SetDepth(int depth)
    {
        Depth = depth;
        OnPropertyChanged(nameof(IndentMargin));
        OnPropertyChanged(nameof(GuideMargin));
        OnPropertyChanged(nameof(HierarchyAutomationHelp));
    }

    internal void SortChildren()
    {
        Children.Sort(static (left, right) =>
        {
            var priority = left.Issue.Priority.CompareTo(right.Issue.Priority);
            if (priority != 0)
            {
                return priority;
            }

            var activity = Nullable.Compare(right.Issue.UpdatedAt, left.Issue.UpdatedAt);
            return activity != 0
                ? activity
                : StringComparer.OrdinalIgnoreCase.Compare(left.Id, right.Id);
        });

        foreach (var child in Children)
        {
            child.SortChildren();
        }
    }

    private static string BuildSignalLabel(BeadFacts facts, string childProgress)
    {
        if (facts.ActiveBlockerIds.Count > 0)
        {
            var suffix = facts.ActiveBlockerIds.Count > 1
                ? $" +{facts.ActiveBlockerIds.Count - 1}"
                : string.Empty;
            return $"← {facts.ActiveBlockerIds[0]}{suffix}";
        }

        return facts.BlockSeverity switch
        {
            BlockSeverity.Partial => $"partial · {childProgress}",
            BlockSeverity.Complete => "fully blocked",
            _ when facts.IsActive => "active",
            _ when !string.IsNullOrWhiteSpace(childProgress) => childProgress,
            _ when facts.IsUnclaimed => "ready",
            _ when facts.IsClosed => "closed",
            _ => FormatStatus(facts.Issue.Status),
        };
    }

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
