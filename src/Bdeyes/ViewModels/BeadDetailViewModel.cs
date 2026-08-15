using System.Collections.ObjectModel;
using Avalonia.Media;
using Bdeyes.Models;
using Bdeyes.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Bdeyes.ViewModels;

public sealed partial class BeadDetailViewModel : ViewModelBase
{
    private readonly BeadAnalyzer _analyzer;
    private readonly Action<string> _openIssue;

    public BeadDetailViewModel(
        BeadRowViewModel row,
        BeadAnalyzer analyzer,
        Action<string> openIssue)
    {
        Row = row;
        Issue = row.Issue;
        _analyzer = analyzer;
        _openIssue = openIssue;

        Children = new ObservableCollection<RelatedBeadViewModel>(
            row.Facts.Children.Select(child => Related(child, "child")));
        Blockers = new ObservableCollection<RelatedBeadViewModel>(
            row.Facts.ActiveBlockerIds.Select(id =>
            {
                var blocker = analyzer.Find(id);
                return blocker is null
                    ? new RelatedBeadViewModel(id, "Unavailable in this snapshot", "blocking", Palette.Blocked, () => openIssue(id))
                    : Related(blocker, "blocking");
            }));
        Links = new ObservableCollection<RelatedBeadViewModel>(
            Issue.Dependencies
                .Where(dependency => !string.Equals(dependency.Type, "parent-child", StringComparison.OrdinalIgnoreCase))
                .Where(dependency => !row.Facts.ActiveBlockerIds.Contains(dependency.DependsOnId, StringComparer.OrdinalIgnoreCase))
                .Select(dependency =>
                {
                    var target = analyzer.Find(dependency.DependsOnId);
                    return target is null
                        ? new RelatedBeadViewModel(
                            dependency.DependsOnId,
                            "Unavailable in this snapshot",
                            dependency.Type,
                            Palette.Muted,
                            () => openIssue(dependency.DependsOnId))
                        : Related(target, dependency.Type);
                }));

        Description = Issue.Description ?? string.Empty;
        Design = Issue.Design ?? string.Empty;
        AcceptanceCriteria = Issue.AcceptanceCriteria ?? string.Empty;
        Notes = Issue.Notes ?? string.Empty;
        CloseReason = Issue.CloseReason ?? string.Empty;
        LabelsText = string.Join("  ·  ", Issue.Labels ?? []);
    }

    public BeadRowViewModel Row { get; }

    public BeadIssue Issue { get; }

    public string Id => Issue.Id;

    public string Title => Issue.Title;

    public string StatusLabel => Row.StatusLabel;

    public string TypeLabel => Row.TypeLabel;

    public string PriorityLabel => Row.PriorityLabel;

    public string ActivityLabel => Row.ActivityLabel;

    public string AssigneeLabel => string.IsNullOrWhiteSpace(Issue.Assignee) ? "Unclaimed" : Issue.Assignee!;

    public string OwnerLabel => string.IsNullOrWhiteSpace(Issue.Owner) ? "No owner" : Issue.Owner!;

    public string CreatedLabel => Issue.CreatedAt?.ToLocalTime().ToString("g") ?? "Unknown";

    public string UpdatedLabel => Issue.UpdatedAt?.ToLocalTime().ToString("g") ?? "Unknown";

    public string Description { get; }

    public string Design { get; }

    public string AcceptanceCriteria { get; }

    public string Notes { get; }

    public string CloseReason { get; }

    public string LabelsText { get; }

    public IBrush StatusBrush => Row.StatusBrush;

    public IBrush StatusBackground => Row.StatusBackground;

    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);

    public bool HasDesign => !string.IsNullOrWhiteSpace(Design);

    public bool HasAcceptanceCriteria => !string.IsNullOrWhiteSpace(AcceptanceCriteria);

    public bool HasNotes => !string.IsNullOrWhiteSpace(Notes);

    public bool HasCloseReason => !string.IsNullOrWhiteSpace(CloseReason);

    public bool HasLabels => !string.IsNullOrWhiteSpace(LabelsText);

    public bool HasChildren => Children.Count > 0;

    public bool HasBlockers => Blockers.Count > 0;

    public bool HasLinks => Links.Count > 0;

    public bool HasComments => Comments.Count > 0;

    public bool HasDependents => Dependents.Count > 0;

    public ObservableCollection<RelatedBeadViewModel> Children { get; }

    public ObservableCollection<RelatedBeadViewModel> Blockers { get; }

    public ObservableCollection<RelatedBeadViewModel> Links { get; }

    public ObservableCollection<RelatedBeadViewModel> Dependents { get; } = [];

    public ObservableCollection<CommentViewModel> Comments { get; } = [];

    [ObservableProperty]
    public partial bool IsLoading { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string ErrorMessage { get; set; } = string.Empty;

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    public void ApplyLoadedDetail(BeadIssue detail)
    {
        Comments.Clear();
        foreach (var comment in detail.Comments.OrderByDescending(comment => comment.CreatedAt))
        {
            Comments.Add(new CommentViewModel(
                comment.Author,
                comment.Text,
                comment.CreatedAt?.ToLocalTime().ToString("g") ?? "Unknown"));
        }

        Dependents.Clear();
        foreach (var dependent in detail.Dependents.OrderBy(dependent => dependent.Priority).ThenBy(dependent => dependent.Title))
        {
            Dependents.Add(new RelatedBeadViewModel(
                dependent.Id,
                dependent.Title,
                dependent.DependencyType,
                Palette.ForStatus(dependent.Status, BlockSeverity.None).Foreground,
                () => _openIssue(dependent.Id)));
        }

        IsLoading = false;
        OnPropertyChanged(nameof(HasComments));
        OnPropertyChanged(nameof(HasDependents));
    }

    public void ApplyError(string message)
    {
        ErrorMessage = message;
        IsLoading = false;
    }

    private RelatedBeadViewModel Related(BeadIssue issue, string relationship) =>
        new(
            issue.Id,
            issue.Title,
            relationship,
            Palette.ForStatus(issue.Status, BlockSeverity.None).Foreground,
            () => _openIssue(issue.Id));
}

public sealed class RelatedBeadViewModel
{
    public RelatedBeadViewModel(
        string id,
        string title,
        string relationship,
        IBrush accent,
        Action open)
    {
        Id = id;
        Title = title;
        Relationship = BeadRowViewModel.FormatStatus(relationship);
        Accent = accent;
        OpenCommand = new RelayCommand(open);
    }

    public string Id { get; }

    public string Title { get; }

    public string Relationship { get; }

    public IBrush Accent { get; }

    public IRelayCommand OpenCommand { get; }

    public string AutomationName => $"{Relationship} {Id}: {Title}";

    public override string ToString() => AutomationName;
}

public sealed record CommentViewModel(string Author, string Text, string CreatedLabel);
