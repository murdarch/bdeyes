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
    private readonly BdExecutableLocator _bdExecutableLocator;
    private readonly string? _initialWorkspace;
    private readonly List<BeadRowViewModel> _allRows = [];
    private readonly List<BeadRowViewModel> _rootRows = [];
    private readonly Dictionary<string, BeadRowViewModel> _rowsById =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _expandedIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _includedIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _settingsSaveSync = new();

    private BdWorkspaceSnapshot? _snapshot;
    private BeadAnalyzer? _analyzer;
    private CancellationTokenSource? _detailCancellation;
    private bool _initialized;
    private bool _rebuildingPersonFilters;
    private Task _settingsSaveTail = Task.CompletedTask;
    private string? _lastWorkspacePath;
    private string? _explicitBdExecutablePath;
    private string? _testedBdExecutable;
    private bool _bdExecutableDraftIsAutomatic;
    private bool _updatingBdExecutableDraft;

    public MainViewModel()
        : this(new BdClient(), new UserSettingsStore(), null)
    {
    }

    public MainViewModel(
        IBdClient bdClient,
        UserSettingsStore settingsStore,
        string? initialWorkspace,
        BdExecutableLocator? bdExecutableLocator = null)
    {
        _bdClient = bdClient;
        _settingsStore = settingsStore;
        _initialWorkspace = initialWorkspace;
        _bdExecutableLocator = bdExecutableLocator ?? new BdExecutableLocator();

        NavigationItems =
        [
            new NavigationItemViewModel(DashboardMode.Now, "Cooking now", "●"),
            new NavigationItemViewModel(DashboardMode.Blocked, "Blocked", "◆"),
            new NavigationItemViewModel(DashboardMode.Unclaimed, "Ready to claim", "○"),
            new NavigationItemViewModel(DashboardMode.Aging, "Aging", "◷"),
            new NavigationItemViewModel(DashboardMode.All, "All beads", "≡"),
            new NavigationItemViewModel(DashboardMode.Epics, "Epics", "▦"),
        ];
        AssigneeOptions.Add(new PersonFilterOption(
            PersonFilterKind.All,
            "All assignees",
            null,
            0));
        OwnerOptions.Add(new PersonFilterOption(
            PersonFilterKind.All,
            "All owners",
            null,
            0));
        SelectedAssigneeFilter = AssigneeOptions[0];
        SelectedOwnerFilter = OwnerOptions[0];

        SelectedNavigation = NavigationItems[0];
    }

    public ObservableCollection<NavigationItemViewModel> NavigationItems { get; }
    public ObservableCollection<PersonFilterOption> AssigneeOptions { get; } = [];

    public ObservableCollection<PersonFilterOption> OwnerOptions { get; } = [];

    public ObservableCollection<BeadRowViewModel> VisibleRows { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSnapshot))]
    [NotifyPropertyChangedFor(nameof(HasNoSnapshot))]
    [NotifyPropertyChangedFor(nameof(ShowFirstRunSurface))]
    [NotifyPropertyChangedFor(nameof(ShowWorkspaceSurface))]
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
    [NotifyPropertyChangedFor(nameof(ShowFirstRunSurface))]
    [NotifyPropertyChangedFor(nameof(ShowWorkspaceSurface))]
    public partial bool IsBdSettingsOpen { get; set; }

    [ObservableProperty]
    public partial string BdExecutableDraft { get; set; } = "bd";

    [ObservableProperty]
    public partial string BdExecutableSourceLabel { get; set; } = "Automatic discovery";

    [ObservableProperty]
    public partial string BdExecutableVersionLabel { get; set; } = "Not tested";

    [ObservableProperty]
    public partial string BdExecutableStatusMessage { get; set; } =
        "Test the selected executable before saving.";

    [ObservableProperty]
    public partial bool IsBdExecutableTesting { get; set; }

    [ObservableProperty]
    public partial bool BdExecutableTestSucceeded { get; set; }

    [ObservableProperty]
    public partial bool IsBdExecutableAutomatic { get; set; } = true;


    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial PersonFilterOption? SelectedAssigneeFilter { get; set; }

    [ObservableProperty]
    public partial PersonFilterOption? SelectedOwnerFilter { get; set; }

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
        "Active paths with their epic and parent context.";

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
    public bool ShowFirstRunSurface => HasNoSnapshot && !IsBdSettingsOpen;

    public bool ShowWorkspaceSurface => HasSnapshot && !IsBdSettingsOpen;


    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    public bool HasSelection => Detail is not null;

    private bool CanRefresh => _snapshot is not null && !IsLoading;
    private bool CanTestBdExecutable =>
        _bdClient is IConfigurableBdClient &&
        !IsLoading &&
        !IsBdExecutableTesting &&
        !string.IsNullOrWhiteSpace(BdExecutableDraft);

    private bool CanSaveBdSettings =>
        CanTestBdExecutable &&
        BdExecutableTestSucceeded &&
        _testedBdExecutable is not null &&
        SamePath(_testedBdExecutable, BdExecutableDraft);


    public async Task InitializeAsync()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        var settings = await _settingsStore.LoadAsync();
        _lastWorkspacePath = settings.LastWorkspace;
        _explicitBdExecutablePath = string.IsNullOrWhiteSpace(settings.BdExecutablePath)
            ? null
            : settings.BdExecutablePath;
        ApplyBdExecutableResolution(_bdExecutableLocator.Resolve(_explicitBdExecutablePath));
        var workspace = !string.IsNullOrWhiteSpace(_initialWorkspace)
            ? _initialWorkspace
            : settings.LastWorkspace;
        if (!string.IsNullOrWhiteSpace(workspace))
        {
            if (SamePath(workspace, settings.LastWorkspace))
            {
                _expandedIds.UnionWith(settings.ExpandedIssueIds ?? []);
            }

            await LoadWorkspaceCoreAsync(workspace, persist: false);
        }
    }

    public Task OpenWorkspaceAsync(string workspacePath)
    {
        if (_snapshot is null || !SamePath(_snapshot.WorkspacePath, workspacePath))
        {
            _expandedIds.Clear();
        }

        return LoadWorkspaceCoreAsync(workspacePath, persist: true);
    }

    public bool ExpandOrSelectFirstChild()
    {
        if (SelectedRow is not { } row)
        {
            return false;
        }

        var firstVisibleChild = row.Children.FirstOrDefault(child => _includedIds.Contains(child.Id));
        if (firstVisibleChild is null)
        {
            return false;
        }

        if (!row.IsExpanded)
        {
            SetExpanded(row, expanded: true);
        }
        else
        {
            SelectedRow = firstVisibleChild;
        }

        return true;
    }

    public bool CollapseOrSelectParent()
    {
        if (SelectedRow is not { } row)
        {
            return false;
        }

        if (row.IsExpanded)
        {
            SetExpanded(row, expanded: false);
            return true;
        }

        if (row.Parent is not null && _includedIds.Contains(row.Parent.Id))
        {
            SelectedRow = row.Parent;
            return true;
        }

        return false;
    }

    public bool ToggleSelectedExpansion()
    {
        if (SelectedRow is not { HasVisibleChildren: true } row)
        {
            return false;
        }

        ToggleExpansion(row);
        return true;
    }

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshAsync()
    {
        if (_snapshot is not null)
        {
            await LoadWorkspaceCoreAsync(_snapshot.WorkspacePath, persist: false);
        }
    }
    [RelayCommand]
    private void OpenBdSettings()
    {
        PrepareBdExecutableDraft(
            _bdExecutableLocator.Resolve(_explicitBdExecutablePath),
            automatic: _explicitBdExecutablePath is null);
        IsBdSettingsOpen = true;
    }

    [RelayCommand]
    private void CloseBdSettings()
    {
        PrepareBdExecutableDraft(
            _bdExecutableLocator.Resolve(_explicitBdExecutablePath),
            automatic: _explicitBdExecutablePath is null);
        IsBdSettingsOpen = false;
    }

    [RelayCommand]
    private void ResetBdExecutable()
    {
        PrepareBdExecutableDraft(
            _bdExecutableLocator.Resolve(null),
            automatic: true);
    }

    [RelayCommand(CanExecute = nameof(CanTestBdExecutable))]
    private async Task TestBdExecutableAsync()
    {
        if (_bdClient is not IConfigurableBdClient client)
        {
            BdExecutableStatusMessage = "This bdeyes client cannot change bd executables.";
            return;
        }

        var resolution = _bdExecutableDraftIsAutomatic
            ? _bdExecutableLocator.Resolve(null)
            : _bdExecutableLocator.ResolveExplicit(BdExecutableDraft);
        if (!resolution.IsFound)
        {
            _testedBdExecutable = null;
            BdExecutableTestSucceeded = false;
            BdExecutableVersionLabel = "Not found";
            BdExecutableSourceLabel = resolution.SourceLabel;
            BdExecutableStatusMessage = resolution.Warning ?? "Choose a bd executable.";
            return;
        }

        IsBdExecutableTesting = true;
        _testedBdExecutable = null;
        BdExecutableTestSucceeded = false;
        BdExecutableVersionLabel = "Testing…";
        BdExecutableStatusMessage = "Running bd --readonly version.";
        try
        {
            var version = await client.ProbeVersionAsync(resolution.Executable);
            _testedBdExecutable = resolution.Executable;
            SetBdExecutableDraft(resolution.Executable);
            BdExecutableSourceLabel = resolution.SourceLabel;
            BdExecutableVersionLabel = FormatVersion(version);
            BdExecutableStatusMessage = resolution.Warning ??
                "Validated without reading a workspace or credential.";
            BdExecutableTestSucceeded = true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            BdExecutableVersionLabel = "Validation failed";
            BdExecutableStatusMessage = FriendlyMessage(exception);
        }
        finally
        {
            IsBdExecutableTesting = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanSaveBdSettings))]
    private async Task SaveBdSettingsAsync()
    {
        if (_bdClient is not IConfigurableBdClient client ||
            _testedBdExecutable is null)
        {
            return;
        }

        client.ConfigureExecutable(_testedBdExecutable);
        _explicitBdExecutablePath = _bdExecutableDraftIsAutomatic
            ? null
            : _testedBdExecutable;
        await QueuePersistViewStateAsync();
        IsBdSettingsOpen = false;

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

    partial void OnIsLoadingChanged(bool value)
    {
        RefreshCommand.NotifyCanExecuteChanged();
        TestBdExecutableCommand.NotifyCanExecuteChanged();
        SaveBdSettingsCommand.NotifyCanExecuteChanged();
    }

    partial void OnBdExecutableDraftChanged(string value)
    {
        if (_updatingBdExecutableDraft)
        {
            return;
        }

        _bdExecutableDraftIsAutomatic = false;
        IsBdExecutableAutomatic = false;
        _testedBdExecutable = null;
        BdExecutableTestSucceeded = false;
        BdExecutableVersionLabel = "Not tested";
        BdExecutableStatusMessage = "Test the selected executable before saving.";
        TestBdExecutableCommand.NotifyCanExecuteChanged();
        SaveBdSettingsCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsBdExecutableTestingChanged(bool value)
    {
        TestBdExecutableCommand.NotifyCanExecuteChanged();
        SaveBdSettingsCommand.NotifyCanExecuteChanged();
    }

    partial void OnBdExecutableTestSucceededChanged(bool value) =>
        SaveBdSettingsCommand.NotifyCanExecuteChanged();


    partial void OnSearchTextChanged(string value) => ApplyFilter();
    partial void OnSelectedAssigneeFilterChanged(PersonFilterOption? value)
    {
        if (!_rebuildingPersonFilters)
        {
            ApplyFilter();
        }
    }

    partial void OnSelectedOwnerFilterChanged(PersonFilterOption? value)
    {
        if (!_rebuildingPersonFilters)
        {
            ApplyFilter();
        }
    }

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

        var detail = new BeadDetailViewModel(
            value,
            _analyzer,
            NavigateToIssue,
            RevealInOutline);
        Detail = detail;
        _detailCancellation = new CancellationTokenSource();
        _ = LoadDetailAsync(detail, _snapshot.WorkspacePath, _detailCancellation.Token);
    }

    private void ApplyBdExecutableResolution(BdExecutableResolution resolution)
    {
        if (_bdClient is IConfigurableBdClient client)
        {
            client.ConfigureExecutable(resolution.Executable);
        }

        PrepareBdExecutableDraft(
            resolution,
            automatic: _explicitBdExecutablePath is null);
    }

    private void PrepareBdExecutableDraft(
        BdExecutableResolution resolution,
        bool automatic)
    {
        _bdExecutableDraftIsAutomatic = automatic;
        IsBdExecutableAutomatic = automatic;
        _testedBdExecutable = null;
        SetBdExecutableDraft(resolution.Executable);
        BdExecutableSourceLabel = resolution.SourceLabel;
        BdExecutableVersionLabel = resolution.IsFound ? "Not tested" : "Not found";
        BdExecutableStatusMessage = resolution.Warning ??
            (resolution.IsFound
                ? "Detected. Test this executable before saving."
                : "Install bd or choose its executable.");
        BdExecutableTestSucceeded = false;
        TestBdExecutableCommand.NotifyCanExecuteChanged();
        SaveBdSettingsCommand.NotifyCanExecuteChanged();
    }

    private void SetBdExecutableDraft(string executable)
    {
        _updatingBdExecutableDraft = true;
        try
        {
            BdExecutableDraft = executable;
        }
        finally
        {
            _updatingBdExecutableDraft = false;
        }
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
                await QueuePersistViewStateAsync();
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
        _rootRows.Clear();
        _rowsById.Clear();

        foreach (var issue in snapshot.Issues)
        {
            var row = new BeadRowViewModel(
                _analyzer.Analyze(issue),
                _analyzer,
                ToggleExpansion);
            _allRows.Add(row);
            _rowsById[row.Id] = row;
        }

        foreach (var row in _allRows)
        {
            var parentIssue = _analyzer.ParentOf(row.Issue);
            if (parentIssue is not null &&
                _rowsById.TryGetValue(parentIssue.Id, out var parent))
            {
                row.AttachTo(parent);
            }
        }

        _rootRows.AddRange(_allRows.Where(row => row.Parent is null));
        _rootRows.Sort(CompareRows);
        foreach (var root in _rootRows)
        {
            root.SortChildren();
        }

        var reached = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in _rootRows)
        {
            AssignDepth(root, 0, reached);
        }

        foreach (var orphan in _allRows.Where(row => !reached.Contains(row.Id)).ToArray())
        {
            orphan.SetDepth(0);
            _rootRows.Add(orphan);
        }
        RebuildPersonFilters();

        _expandedIds.IntersectWith(_rowsById.Keys);
        WorkspaceName = new DirectoryInfo(snapshot.WorkspacePath).Name;
        WorkspacePath = snapshot.WorkspacePath;
        var unresolvedCount = _allRows.Count(row => !row.Facts.IsClosed);
        WorkspaceSummary = $"{snapshot.Issues.Count:N0} beads · {unresolvedCount:N0} unresolved";
        BdVersionLabel = FormatVersion(snapshot.BdVersion);
        _lastWorkspacePath = snapshot.WorkspacePath;
        if (_bdClient is IConfigurableBdClient client)
        {
            _testedBdExecutable = client.Executable;
            SetBdExecutableDraft(client.Executable);
            BdExecutableVersionLabel = BdVersionLabel;
            BdExecutableStatusMessage = "Connected through this bd executable.";
            BdExecutableTestSucceeded = true;
        }
        LastRefreshedLabel = $"refreshed {snapshot.LoadedAt.ToLocalTime():t}";
        SnapshotLoaded = true;

        UpdateCounts();
        ApplyFilter();

        SelectedRow =
            selectedId is not null &&
            _includedIds.Contains(selectedId) &&
            _rowsById.TryGetValue(selectedId, out var selected)
                ? selected
                : null;
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

    private void RebuildPersonFilters()
    {
        var assigneeOptions = BuildPersonFilterOptions(
            _allRows.Select(row => row.Issue.Assignee),
            "All assignees");
        var ownerOptions = BuildPersonFilterOptions(
            _allRows.Select(row => row.Issue.Owner),
            "All owners");
        var previousAssignee = SelectedAssigneeFilter;
        var previousOwner = SelectedOwnerFilter;
        var nextAssignee = assigneeOptions.FirstOrDefault(
            option => option.RepresentsSameSelection(previousAssignee)) ?? assigneeOptions[0];
        var nextOwner = ownerOptions.FirstOrDefault(
            option => option.RepresentsSameSelection(previousOwner)) ?? ownerOptions[0];

        _rebuildingPersonFilters = true;
        try
        {
            ReplaceOptions(AssigneeOptions, assigneeOptions);
            ReplaceOptions(OwnerOptions, ownerOptions);
            SelectedAssigneeFilter = nextAssignee;
            SelectedOwnerFilter = nextOwner;
        }
        finally
        {
            _rebuildingPersonFilters = false;
        }
    }

    private static IReadOnlyList<PersonFilterOption> BuildPersonFilterOptions(
        IEnumerable<string?> candidates,
        string allLabel)
    {
        var values = candidates.ToArray();
        var options = new List<PersonFilterOption>
        {
            new(PersonFilterKind.All, allLabel, null, values.Length),
        };
        var unassignedCount = values.Count(string.IsNullOrWhiteSpace);
        if (unassignedCount > 0)
        {
            options.Add(new PersonFilterOption(
                PersonFilterKind.Unassigned,
                "Unassigned",
                null,
                unassignedCount));
        }

        options.AddRange(
            values
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value!.Trim())
                .GroupBy(value => value, StringComparer.OrdinalIgnoreCase)
                .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
                .Select(group => new PersonFilterOption(
                    PersonFilterKind.Person,
                    group.Key,
                    group.Key,
                    group.Count())));
        return options;
    }

    private static void ReplaceOptions(
        ObservableCollection<PersonFilterOption> target,
        IEnumerable<PersonFilterOption> replacement)
    {
        target.Clear();
        foreach (var option in replacement)
        {
            target.Add(option);
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
        if (_analyzer is null)
        {
            VisibleRows.Clear();
            IsResultEmpty = true;
            ResultCountLabel = "0 beads";
            return;
        }

        var mode = SelectedNavigation?.Mode ?? DashboardMode.Now;
        var query = SearchText.Trim();
        var assigneeFilter = SelectedAssigneeFilter;
        var ownerFilter = SelectedOwnerFilter;
        var hasPersonFilter =
            assigneeFilter is { IsRestrictive: true } ||
            ownerFilter is { IsRestrictive: true };
        IEnumerable<BeadRowViewModel> directRows = mode switch
        {
            DashboardMode.Now => _allRows.Where(row => row.Facts.IsActive),
            DashboardMode.Blocked => _allRows.Where(row =>
                !row.Facts.IsClosed && row.Facts.BlockSeverity != BlockSeverity.None),
            DashboardMode.Unclaimed => _allRows.Where(row => row.Facts.IsUnclaimed),
            DashboardMode.Aging => _allRows.Where(row =>
                !row.Facts.IsClosed && row.Facts.IsStale),
            DashboardMode.Epics when query.Length == 0 => _allRows.Where(row =>
                row.IsEpic && !row.Facts.IsClosed),
            DashboardMode.Epics => _allRows.Where(BelongsToOpenEpic),
            _ => _allRows,
        };
        if (assigneeFilter is { IsRestrictive: true })
        {
            directRows = directRows.Where(row => assigneeFilter.Matches(row.Issue.Assignee));
        }

        if (ownerFilter is { IsRestrictive: true })
        {
            directRows = directRows.Where(row => ownerFilter.Matches(row.Issue.Owner));
        }

        if (query.Length > 0)
        {
            directRows = directRows.Where(row => row.Matches(query));
        }

        var direct = directRows.ToArray();
        var includeEpicSubtrees =
            mode == DashboardMode.Epics &&
            query.Length == 0 &&
            !hasPersonFilter;
        var projection = OutlineProjection.Create(
            direct.Select(row => row.Id),
            _analyzer,
            includeDescendantsOfMatches: includeEpicSubtrees);
        _includedIds.Clear();
        _includedIds.UnionWith(projection.IncludedIds);

        var focusedView =
            hasPersonFilter ||
            query.Length > 0 ||
            mode is DashboardMode.Now or
                DashboardMode.Blocked or
                DashboardMode.Unclaimed or
                DashboardMode.Aging;
        foreach (var row in _allRows)
        {
            row.IsDirectMatch = projection.DirectMatchIds.Contains(row.Id);
            var isRequiredEpicContext =
                mode == DashboardMode.Epics &&
                !row.IsDirectMatch &&
                projection.RequiredExpandedIds.Contains(row.Id);
            row.IsContextOnly =
                projection.IncludedIds.Contains(row.Id) &&
                ((focusedView && !row.IsDirectMatch) || isRequiredEpicContext);
            row.HasVisibleChildren = row.Children.Any(child => _includedIds.Contains(child.Id));
            row.IsExpanded =
                row.HasVisibleChildren &&
                (_expandedIds.Contains(row.Id) ||
                 (focusedView && projection.RequiredExpandedIds.Contains(row.Id)) ||
                 isRequiredEpicContext);
        }

        ProjectVisibleRows();
        var contextCount = projection.IncludedIds.Count - projection.DirectMatchIds.Count;
        ResultCountLabel = contextCount > 0
            ? $"{direct.Length:N0} match{(direct.Length == 1 ? string.Empty : "es")} · {contextCount:N0} context"
            : $"{direct.Length:N0} bead{(direct.Length == 1 ? string.Empty : "s")}";
        IsResultEmpty = direct.Length == 0;
    }

    private void ProjectVisibleRows()
    {
        var projected = new List<BeadRowViewModel>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in _rootRows)
        {
            AddVisible(root, visited, projected);
        }

        SynchronizeVisibleRows(projected);
        if (SelectedRow is not null && !projected.Contains(SelectedRow))
        {
            SelectedRow = null;
        }
    }

    private void AddVisible(
        BeadRowViewModel row,
        HashSet<string> visited,
        List<BeadRowViewModel> projected)
    {
        if (!_includedIds.Contains(row.Id) || !visited.Add(row.Id))
        {
            return;
        }

        projected.Add(row);
        if (!row.IsExpanded)
        {
            return;
        }

        foreach (var child in row.Children)
        {
            AddVisible(child, visited, projected);
        }
    }

    private void SynchronizeVisibleRows(IReadOnlyList<BeadRowViewModel> projected)
    {
        for (var index = 0; index < projected.Count; index++)
        {
            var row = projected[index];
            if (index < VisibleRows.Count && ReferenceEquals(VisibleRows[index], row))
            {
                continue;
            }

            var currentIndex = VisibleRows.IndexOf(row);
            if (currentIndex >= 0)
            {
                VisibleRows.Move(currentIndex, index);
            }
            else
            {
                VisibleRows.Insert(index, row);
            }
        }

        while (VisibleRows.Count > projected.Count)
        {
            VisibleRows.RemoveAt(VisibleRows.Count - 1);
        }
    }

    private void ToggleExpansion(BeadRowViewModel row)
    {
        if (!row.HasVisibleChildren)
        {
            return;
        }

        SetExpanded(row, !row.IsExpanded);
    }

    private void SetExpanded(BeadRowViewModel row, bool expanded)
    {
        row.IsExpanded = expanded;
        if (expanded)
        {
            _expandedIds.Add(row.Id);
        }
        else
        {
            _expandedIds.Remove(row.Id);
        }

        if (!expanded &&
            SelectedRow is not null &&
            IsDescendantOf(SelectedRow, row))
        {
            SelectedRow = row;
        }

        ProjectVisibleRows();
        _ = QueuePersistViewStateAsync();
    }

    private Task QueuePersistViewStateAsync()
    {
        var settings = new UserSettings(
            _lastWorkspacePath,
            _expandedIds.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            _explicitBdExecutablePath);
        lock (_settingsSaveSync)
        {
            _settingsSaveTail = SaveSettingsAfterAsync(_settingsSaveTail, settings);
            return _settingsSaveTail;
        }
    }

    private async Task SaveSettingsAfterAsync(Task precedingSave, UserSettings settings)
    {
        await precedingSave;
        try
        {
            await _settingsStore.SaveAsync(settings);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ErrorMessage = "bdeyes could not persist its local settings.";
        }
    }

    private void UpdateViewCopy(DashboardMode mode)
    {
        (ViewEyebrow, ViewTitle, ViewDescription, EmptyTitle, EmptyDescription) = mode switch
        {
            DashboardMode.Now => (
                "NOW",
                "What’s cooking",
                "Active paths with their epic and parent context.",
                "Nothing is cooking",
                "No beads are currently marked in progress."),
            DashboardMode.Blocked => (
                "FRICTION",
                "Where work stops",
                "Blocked leaves and the ancestor paths that contain them.",
                "No blocked paths",
                "Every unresolved path can move."),
            DashboardMode.Unclaimed => (
                "AVAILABLE",
                "Ready for a hand",
                "Ready leaves grouped beneath their larger work.",
                "Nothing ready to claim",
                "Available work is either claimed, blocked, or deferred."),
            DashboardMode.Aging => (
                "ATTENTION",
                "Quiet too long",
                $"Stale paths with {BeadAnalyzer.StaleAfter.TotalDays:0}+ days since activity.",
                "Nothing has gone quiet",
                "Every unresolved bead has recent activity."),
            DashboardMode.Epics => (
                "SHAPE",
                "The larger work",
                "Collapsible epic trees with progress and blockage signals.",
                "No open epics",
                "This workspace has no unresolved epics."),
            _ => (
                "LEDGER",
                "Every bead",
                "The authoritative containment outline, open and closed.",
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

        SearchText = string.Empty;
        ExpandAncestorPath(row);
        SelectNavigation(DashboardMode.All);
        SelectedRow = row;
    }

    private void RevealInOutline(string issueId)
    {
        if (!_rowsById.TryGetValue(issueId, out var row))
        {
            return;
        }

        SearchText = string.Empty;
        ExpandAncestorPath(row);
        _expandedIds.Add(row.Id);
        SelectNavigation(DashboardMode.All);
        SelectedRow = row;
        _ = QueuePersistViewStateAsync();
    }

    private void ExpandAncestorPath(BeadRowViewModel row)
    {
        var current = row.Parent;
        while (current is not null)
        {
            _expandedIds.Add(current.Id);
            current = current.Parent;
        }
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

    private bool BelongsToOpenEpic(BeadRowViewModel row)
    {
        if (row.IsEpic && !row.Facts.IsClosed)
        {
            return true;
        }

        var current = row.Parent;
        while (current is not null)
        {
            if (current.IsEpic && !current.Facts.IsClosed)
            {
                return true;
            }

            current = current.Parent;
        }

        return false;
    }

    private void SetNavigationCount(DashboardMode mode, int count) =>
        NavigationItems.First(item => item.Mode == mode).Count = count;

    private static bool IsDescendantOf(BeadRowViewModel candidate, BeadRowViewModel ancestor)
    {
        var current = candidate.Parent;
        while (current is not null)
        {
            if (ReferenceEquals(current, ancestor))
            {
                return true;
            }

            current = current.Parent;
        }

        return false;
    }


    private static void AssignDepth(
        BeadRowViewModel row,
        int depth,
        HashSet<string> reached)
    {
        if (!reached.Add(row.Id))
        {
            return;
        }

        row.SetDepth(depth);
        foreach (var child in row.Children)
        {
            AssignDepth(child, depth + 1, reached);
        }
    }

    private static int CompareRows(BeadRowViewModel left, BeadRowViewModel right)
    {
        var closed = left.Facts.IsClosed.CompareTo(right.Facts.IsClosed);
        if (closed != 0)
        {
            return closed;
        }

        var priority = left.Issue.Priority.CompareTo(right.Issue.Priority);
        if (priority != 0)
        {
            return priority;
        }

        var activity = Nullable.Compare(right.Issue.UpdatedAt, left.Issue.UpdatedAt);
        return activity != 0
            ? activity
            : StringComparer.OrdinalIgnoreCase.Compare(left.Id, right.Id);
    }

    private static bool SamePath(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), comparison);
    }

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
