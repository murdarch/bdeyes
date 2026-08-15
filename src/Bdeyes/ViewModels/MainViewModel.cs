using System.Collections.ObjectModel;
using Bdeyes.Models;
using Bdeyes.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Bdeyes.ViewModels;

public sealed partial class MainViewModel : ViewModelBase
{
    private readonly IBdClient _bdClient;
    private readonly UserSettingsStore _settingsStore;
    private readonly string? _initialWorkspace;
    private readonly List<BeadRowViewModel> _allRows = [];
    private readonly Dictionary<string, BeadRowViewModel> _rowsById =
        new(StringComparer.OrdinalIgnoreCase);

    private BdWorkspaceSnapshot? _snapshot;
    private BeadAnalyzer? _analyzer;
    private CancellationTokenSource? _detailCancellation;
    private bool _initialized;

    public MainViewModel()
        : this(new BdClient(), new UserSettingsStore(), null)
    {
    }

    public MainViewModel(
        IBdClient bdClient,
        UserSettingsStore settingsStore,
        string? initialWorkspace)
    {
        _bdClient = bdClient;
        _settingsStore = settingsStore;
        _initialWorkspace = initialWorkspace;

        NavigationItems =
        [
            new NavigationItemViewModel(DashboardMode.Now, "Cooking now", "●"),
            new NavigationItemViewModel(DashboardMode.Blocked, "Blocked", "◆"),
            new NavigationItemViewModel(DashboardMode.Unclaimed, "Ready to claim", "○"),
            new NavigationItemViewModel(DashboardMode.Aging, "Aging", "◷"),
            new NavigationItemViewModel(DashboardMode.All, "All beads", "≡"),
            new NavigationItemViewModel(DashboardMode.Epics, "Epics", "▦"),
        ];

        SelectedNavigation = NavigationItems[0];
    }

    public ObservableCollection<NavigationItemViewModel> NavigationItems { get; }

    public ObservableCollection<BeadRowViewModel> VisibleRows { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSnapshot))]
    [NotifyPropertyChangedFor(nameof(HasNoSnapshot))]
    public partial bool SnapshotLoaded { get; set; }

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string ErrorMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string WorkspaceName { get; set; } = "No workspace";

    [ObservableProperty]
    public partial string WorkspacePath { get; set; } = "Open a repository backed by Beads";

    [ObservableProperty]
    public partial string WorkspaceSummary { get; set; } = "Read-only by construction";

    [ObservableProperty]
    public partial string BdVersionLabel { get; set; } = "bd not connected";

    [ObservableProperty]
    public partial string LastRefreshedLabel { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial NavigationItemViewModel? SelectedNavigation { get; set; }

    [ObservableProperty]
    public partial BeadRowViewModel? SelectedRow { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    public partial BeadDetailViewModel? Detail { get; set; }

    [ObservableProperty]
    public partial string ViewEyebrow { get; set; } = "NOW";

    [ObservableProperty]
    public partial string ViewTitle { get; set; } = "What’s cooking";

    [ObservableProperty]
    public partial string ViewDescription { get; set; } =
        "Claimed work, freshest activity first.";

    [ObservableProperty]
    public partial string ResultCountLabel { get; set; } = "0 beads";

    [ObservableProperty]
    public partial string EmptyTitle { get; set; } = "Nothing is cooking";

    [ObservableProperty]
    public partial string EmptyDescription { get; set; } =
        "No beads are currently marked in progress.";

    [ObservableProperty]
    public partial bool IsResultEmpty { get; set; } = true;

    [ObservableProperty]
    public partial int ActiveCount { get; set; }

    [ObservableProperty]
    public partial int ReadyCount { get; set; }

    [ObservableProperty]
    public partial int BlockedCount { get; set; }

    [ObservableProperty]
    public partial int StaleCount { get; set; }

    public bool HasSnapshot => SnapshotLoaded;

    public bool HasNoSnapshot => !SnapshotLoaded;

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    public bool HasSelection => Detail is not null;

    private bool CanRefresh => _snapshot is not null && !IsLoading;

    public async Task InitializeAsync()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        var settings = await _settingsStore.LoadAsync();
        var workspace = !string.IsNullOrWhiteSpace(_initialWorkspace)
            ? _initialWorkspace
            : settings.LastWorkspace;
        if (!string.IsNullOrWhiteSpace(workspace))
        {
            await LoadWorkspaceCoreAsync(workspace, persist: false);
        }
    }

    public Task OpenWorkspaceAsync(string workspacePath) =>
        LoadWorkspaceCoreAsync(workspacePath, persist: true);

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshAsync()
    {
        if (_snapshot is not null)
        {
            await LoadWorkspaceCoreAsync(_snapshot.WorkspacePath, persist: false);
        }
    }

    [RelayCommand]
    private void DismissError() => ErrorMessage = string.Empty;

    [RelayCommand]
    private void CloseDetail() => SelectedRow = null;

    [RelayCommand]
    private void ShowNow() => SelectNavigation(DashboardMode.Now);

    [RelayCommand]
    private void ShowBlocked() => SelectNavigation(DashboardMode.Blocked);

    [RelayCommand]
    private void ShowUnclaimed() => SelectNavigation(DashboardMode.Unclaimed);

    [RelayCommand]
    private void ShowAging() => SelectNavigation(DashboardMode.Aging);

    partial void OnIsLoadingChanged(bool value) => RefreshCommand.NotifyCanExecuteChanged();

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    partial void OnSelectedNavigationChanged(NavigationItemViewModel? value)
    {
        UpdateViewCopy(value?.Mode ?? DashboardMode.Now);
        ApplyFilter();
    }

    partial void OnSelectedRowChanged(BeadRowViewModel? value)
    {
        _detailCancellation?.Cancel();
        _detailCancellation?.Dispose();
        _detailCancellation = null;

        if (value is null || _analyzer is null || _snapshot is null)
        {
            Detail = null;
            return;
        }

        var detail = new BeadDetailViewModel(value, _analyzer, NavigateToIssue);
        Detail = detail;
        _detailCancellation = new CancellationTokenSource();
        _ = LoadDetailAsync(detail, _snapshot.WorkspacePath, _detailCancellation.Token);
    }

    private async Task LoadWorkspaceCoreAsync(string workspacePath, bool persist)
    {
        IsLoading = true;
        ErrorMessage = string.Empty;

        try
        {
            var snapshot = await _bdClient.LoadWorkspaceAsync(workspacePath);
            ApplySnapshot(snapshot);
            if (persist)
            {
                await _settingsStore.SaveAsync(new UserSettings(snapshot.WorkspacePath));
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ErrorMessage = FriendlyMessage(exception);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void ApplySnapshot(BdWorkspaceSnapshot snapshot)
    {
        var selectedId = SelectedRow?.Id;
        _snapshot = snapshot;
        _analyzer = new BeadAnalyzer(snapshot.Issues, snapshot.LoadedAt);
        _allRows.Clear();
        _rowsById.Clear();

        foreach (var issue in snapshot.Issues)
        {
            var row = new BeadRowViewModel(_analyzer.Analyze(issue), _analyzer);
            _allRows.Add(row);
            _rowsById[row.Id] = row;
        }

        WorkspaceName = new DirectoryInfo(snapshot.WorkspacePath).Name;
        WorkspacePath = snapshot.WorkspacePath;
        var unresolvedCount = _allRows.Count(row => !row.Facts.IsClosed);
        WorkspaceSummary = $"{snapshot.Issues.Count:N0} beads · {unresolvedCount:N0} unresolved";
        BdVersionLabel = FormatVersion(snapshot.BdVersion);
        LastRefreshedLabel = $"refreshed {snapshot.LoadedAt.ToLocalTime():t}";
        SnapshotLoaded = true;

        UpdateCounts();
        ApplyFilter();

        if (selectedId is not null && _rowsById.TryGetValue(selectedId, out var selected))
        {
            SelectedRow = selected;
        }
        else
        {
            SelectedRow = null;
        }
    }

    private async Task LoadDetailAsync(
        BeadDetailViewModel target,
        string workspacePath,
        CancellationToken cancellationToken)
    {
        try
        {
            var detail = await _bdClient.LoadDetailAsync(workspacePath, target.Id, cancellationToken);
            if (ReferenceEquals(Detail, target))
            {
                target.ApplyLoadedDetail(detail);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            if (ReferenceEquals(Detail, target))
            {
                target.ApplyError(FriendlyMessage(exception));
            }
        }
    }

    private void UpdateCounts()
    {
        ActiveCount = _allRows.Count(row => row.Facts.IsActive);
        ReadyCount = _allRows.Count(row => row.Facts.IsUnclaimed);
        BlockedCount = _allRows.Count(row =>
            !row.Facts.IsClosed && row.Facts.BlockSeverity != BlockSeverity.None);
        StaleCount = _allRows.Count(row => !row.Facts.IsClosed && row.Facts.IsStale);

        SetNavigationCount(DashboardMode.Now, ActiveCount);
        SetNavigationCount(DashboardMode.Blocked, BlockedCount);
        SetNavigationCount(DashboardMode.Unclaimed, ReadyCount);
        SetNavigationCount(DashboardMode.Aging, StaleCount);
        SetNavigationCount(DashboardMode.All, _allRows.Count);
        SetNavigationCount(
            DashboardMode.Epics,
            _allRows.Count(row => row.IsEpic && !row.Facts.IsClosed));
    }

    private void ApplyFilter()
    {
        var mode = SelectedNavigation?.Mode ?? DashboardMode.Now;
        IEnumerable<BeadRowViewModel> rows = mode switch
        {
            DashboardMode.Now => _allRows
                .Where(row => row.Facts.IsActive)
                .OrderByDescending(row => row.Issue.UpdatedAt),
            DashboardMode.Blocked => _allRows
                .Where(row => !row.Facts.IsClosed && row.Facts.BlockSeverity != BlockSeverity.None)
                .OrderBy(row => BlockOrder(row.Facts.BlockSeverity))
                .ThenBy(row => row.Issue.Priority)
                .ThenByDescending(row => row.Issue.UpdatedAt),
            DashboardMode.Unclaimed => _allRows
                .Where(row => row.Facts.IsUnclaimed)
                .OrderBy(row => row.Issue.Priority)
                .ThenByDescending(row => row.Issue.UpdatedAt),
            DashboardMode.Aging => _allRows
                .Where(row => !row.Facts.IsClosed && row.Facts.IsStale)
                .OrderByDescending(row => row.Facts.ActivityAge)
                .ThenBy(row => row.Issue.Priority),
            DashboardMode.Epics => _allRows
                .Where(row => row.IsEpic && !row.Facts.IsClosed)
                .OrderByDescending(row => row.Issue.UpdatedAt),
            _ => _allRows
                .OrderBy(row => row.Facts.IsClosed)
                .ThenByDescending(row => row.Issue.UpdatedAt),
        };

        var query = SearchText.Trim();
        if (query.Length > 0)
        {
            rows = rows.Where(row => row.Matches(query));
        }

        var materialized = rows.ToArray();
        VisibleRows.Clear();
        foreach (var row in materialized)
        {
            VisibleRows.Add(row);
        }

        ResultCountLabel = $"{materialized.Length:N0} bead{(materialized.Length == 1 ? string.Empty : "s")}";
        IsResultEmpty = materialized.Length == 0;
    }

    private void UpdateViewCopy(DashboardMode mode)
    {
        (ViewEyebrow, ViewTitle, ViewDescription, EmptyTitle, EmptyDescription) = mode switch
        {
            DashboardMode.Now => (
                "NOW",
                "What’s cooking",
                "Claimed work, freshest activity first.",
                "Nothing is cooking",
                "No beads are currently marked in progress."),
            DashboardMode.Blocked => (
                "FRICTION",
                "Where work stops",
                "Direct blockers and epics whose paths are partly or completely blocked.",
                "No blocked paths",
                "Every unresolved path can move."),
            DashboardMode.Unclaimed => (
                "AVAILABLE",
                "Ready for a hand",
                "Open, unclaimed work with no active blocker.",
                "Nothing ready to claim",
                "Available work is either claimed, blocked, or deferred."),
            DashboardMode.Aging => (
                "ATTENTION",
                "Quiet too long",
                $"Unresolved beads without activity for {BeadAnalyzer.StaleAfter.TotalDays:0} days or more.",
                "Nothing has gone quiet",
                "Every unresolved bead has recent activity."),
            DashboardMode.Epics => (
                "SHAPE",
                "The larger work",
                "Open epics with child progress and aggregate blockage.",
                "No open epics",
                "This workspace has no unresolved epics."),
            _ => (
                "LEDGER",
                "Every bead",
                "Open and closed work, ordered by last activity.",
                "No beads found",
                "This workspace returned an empty ledger."),
        };
    }

    private void NavigateToIssue(string issueId)
    {
        if (!_rowsById.TryGetValue(issueId, out var row))
        {
            return;
        }

        SelectNavigation(DashboardMode.All);
        SelectedRow = row;
    }

    private void SelectNavigation(DashboardMode mode)
    {
        var navigation = NavigationItems.First(item => item.Mode == mode);
        if (ReferenceEquals(SelectedNavigation, navigation))
        {
            ApplyFilter();
        }
        else
        {
            SelectedNavigation = navigation;
        }
    }

    private void SetNavigationCount(DashboardMode mode, int count) =>
        NavigationItems.First(item => item.Mode == mode).Count = count;

    private static int BlockOrder(BlockSeverity severity) => severity switch
    {
        BlockSeverity.Complete => 0,
        BlockSeverity.Partial => 1,
        BlockSeverity.Direct => 2,
        _ => 3,
    };

    private static string FormatVersion(string value)
    {
        var version = value.Trim();
        if (version.StartsWith("bd version ", StringComparison.OrdinalIgnoreCase))
        {
            version = $"bd {version[11..]}";
        }

        var buildStart = version.IndexOf('(');
        return buildStart > 0 ? version[..buildStart].TrimEnd() : version;
    }

    private static string FriendlyMessage(Exception exception) =>
        exception switch
        {
            BdClientException => exception.Message,
            DirectoryNotFoundException => exception.Message,
            UnauthorizedAccessException => "bdeyes cannot read that workspace.",
            _ => $"Could not load this Beads workspace: {exception.Message}",
        };
}
