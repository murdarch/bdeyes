using Bdeyes.Models;
using Bdeyes.Services;
using Bdeyes.ViewModels;

namespace Bdeyes.Tests;

public sealed class MainViewModelOutlineTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 15, 1, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task OutlinePreservesContextAndSupportsProgressiveDisclosure()
    {
        var issues = Hierarchy();
        var settingsPath = TemporarySettingsPath();
        try
        {
            var viewModel = new MainViewModel(
                new StubBdClient(issues),
                new UserSettingsStore(settingsPath),
                "C:/workspace");

            await viewModel.InitializeAsync();

            Assert.Equal(["epic", "child", "grandchild"], viewModel.VisibleRows.Select(row => row.Id));
            Assert.True(viewModel.VisibleRows.Single(row => row.Id == "epic").IsContextOnly);
            Assert.True(viewModel.VisibleRows.Single(row => row.Id == "child").IsContextOnly);
            Assert.True(viewModel.VisibleRows.Single(row => row.Id == "grandchild").IsDirectMatch);

            viewModel.SelectedNavigation = Navigation(viewModel, DashboardMode.All);

            Assert.Contains(viewModel.VisibleRows, row => row.Id == "epic");
            Assert.Contains(viewModel.VisibleRows, row => row.Id == "peer");
            Assert.DoesNotContain(viewModel.VisibleRows, row => row.Id == "child");

            var epic = viewModel.VisibleRows.Single(row => row.Id == "epic");
            viewModel.SelectedRow = epic;
            Assert.True(viewModel.ExpandOrSelectFirstChild());
            Assert.Contains(viewModel.VisibleRows, row => row.Id == "child");
            Assert.DoesNotContain(viewModel.VisibleRows, row => row.Id == "grandchild");

            Assert.True(viewModel.ExpandOrSelectFirstChild());
            var child = Assert.IsType<BeadRowViewModel>(viewModel.SelectedRow);
            Assert.Equal("child", child.Id);
            Assert.True(viewModel.ExpandOrSelectFirstChild());
            var grandchild = viewModel.VisibleRows.Single(row => row.Id == "grandchild");
            viewModel.SelectedRow = grandchild;
            child.ToggleExpansionCommand.Execute(null);
            Assert.DoesNotContain(viewModel.VisibleRows, row => row.Id == "grandchild");
            Assert.Equal("child", viewModel.SelectedRow?.Id);
            Assert.True(viewModel.CollapseOrSelectParent());
            Assert.Equal("epic", viewModel.SelectedRow?.Id);

            viewModel.SearchText = "grandchild";

            Assert.Equal(["epic", "child", "grandchild"], viewModel.VisibleRows.Select(row => row.Id));
            Assert.Equal("1 match · 2 context", viewModel.ResultCountLabel);
            await viewModel.OpenWorkspaceAsync("C:/workspace");
        }
        finally
        {
            DeleteSettingsDirectory(settingsPath);
        }
    }

    [Fact]
    public async Task SavedExpansionIsRestoredForTheSameWorkspace()
    {
        var settingsPath = TemporarySettingsPath();
        try
        {
            var settings = new UserSettingsStore(settingsPath);
            await settings.SaveAsync(new UserSettings("C:/workspace", ["epic"]));
            var viewModel = new MainViewModel(
                new StubBdClient(Hierarchy()),
                settings,
                "C:/workspace");

            await viewModel.InitializeAsync();
            viewModel.SelectedNavigation = Navigation(viewModel, DashboardMode.All);

            Assert.Contains(viewModel.VisibleRows, row => row.Id == "child");
            Assert.DoesNotContain(viewModel.VisibleRows, row => row.Id == "grandchild");
        }
        finally
        {
            DeleteSettingsDirectory(settingsPath);
        }
    }

    [Fact]
    public async Task EpicsExpandOnlyTheNonmatchingPathToNestedEpics()
    {
        var issues = new[]
        {
            Issue("container"),
            Issue("nested-epic", type: "epic", parent: "container"),
            Issue("nested-leaf", parent: "nested-epic"),
            Issue("root-epic", type: "epic"),
        };
        var settingsPath = TemporarySettingsPath();
        try
        {
            var viewModel = new MainViewModel(
                new StubBdClient(issues),
                new UserSettingsStore(settingsPath),
                "C:/workspace");

            await viewModel.InitializeAsync();
            viewModel.SelectedNavigation = Navigation(viewModel, DashboardMode.Epics);

            Assert.Equal(
                ["container", "nested-epic", "root-epic"],
                viewModel.VisibleRows.Select(row => row.Id));
            var container = viewModel.VisibleRows.Single(row => row.Id == "container");
            Assert.True(container.IsContextOnly);
            Assert.True(container.IsExpanded);
            Assert.False(viewModel.VisibleRows.Single(row => row.Id == "nested-epic").IsExpanded);
            Assert.DoesNotContain(viewModel.VisibleRows, row => row.Id == "nested-leaf");
        }
        finally
        {
            DeleteSettingsDirectory(settingsPath);
        }
    }

    [Fact]
    public async Task WorkspacePersistenceFinishesWithTheNewestWorkspaceState()
    {
        var settingsPath = TemporarySettingsPath();
        try
        {
            var settings = new UserSettingsStore(settingsPath);
            var viewModel = new MainViewModel(
                new StubBdClient(Hierarchy()),
                settings,
                "C:/workspace-one");

            await viewModel.InitializeAsync();
            viewModel.SelectedNavigation = Navigation(viewModel, DashboardMode.All);
            var epic = viewModel.VisibleRows.Single(row => row.Id == "epic");
            epic.ToggleExpansionCommand.Execute(null);
            epic.ToggleExpansionCommand.Execute(null);
            epic.ToggleExpansionCommand.Execute(null);

            await viewModel.OpenWorkspaceAsync("C:/workspace-two");

            var saved = await settings.LoadAsync();
            Assert.Equal("C:/workspace-two", saved.LastWorkspace);
            Assert.Empty(saved.ExpandedIssueIds ?? []);
        }
        finally
        {
            DeleteSettingsDirectory(settingsPath);
        }
    }

    [Fact]
    public void InspectorBreadcrumbFollowsAuthoritativeContainment()
    {
        var issues = Hierarchy();
        var analyzer = new BeadAnalyzer(issues, Now);
        var leaf = issues.Single(issue => issue.Id == "grandchild");
        var row = new BeadRowViewModel(analyzer.Analyze(leaf), analyzer, _ => { });

        var detail = new BeadDetailViewModel(row, analyzer, _ => { }, _ => { });

        Assert.Equal(["epic", "child", "grandchild"], detail.Breadcrumbs.Select(item => item.Id));
        Assert.False(detail.Breadcrumbs[^1].CanOpen);
    }

    private static NavigationItemViewModel Navigation(MainViewModel viewModel, DashboardMode mode) =>
        viewModel.NavigationItems.Single(item => item.Mode == mode);

    private static IReadOnlyList<BeadIssue> Hierarchy() =>
    [
        Issue("epic", type: "epic"),
        Issue("child", parent: "epic"),
        Issue("grandchild", status: "in_progress", parent: "child", assignee: "Justice"),
        Issue("peer"),
    ];

    private static BeadIssue Issue(
        string id,
        string status = "open",
        string type = "task",
        string? parent = null,
        string? assignee = null) =>
        new()
        {
            Id = id,
            Title = id,
            Status = status,
            IssueType = type,
            Parent = parent,
            Assignee = assignee,
            CreatedAt = Now.AddDays(-2),
            UpdatedAt = Now,
        };

    private static string TemporarySettingsPath() =>
        Path.Combine(
            Path.GetTempPath(),
            $"bdeyes-tests-{Guid.NewGuid():N}",
            "settings.json");

    private static void DeleteSettingsDirectory(string settingsPath)
    {
        var directory = Path.GetDirectoryName(settingsPath);
        if (directory is not null && Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class StubBdClient(IReadOnlyList<BeadIssue> issues) : IBdClient
    {
        public Task<BdWorkspaceSnapshot> LoadWorkspaceAsync(
            string requestedWorkspacePath,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new BdWorkspaceSnapshot(requestedWorkspacePath, "bd 1.1.2", Now, issues));

        public Task<BeadIssue> LoadDetailAsync(
            string requestedWorkspacePath,
            string issueId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(issues.Single(issue => issue.Id == issueId));
    }
}
