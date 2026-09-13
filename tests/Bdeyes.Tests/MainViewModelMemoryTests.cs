using Bdeyes.Models;
using Bdeyes.Services;
using Bdeyes.ViewModels;

namespace Bdeyes.Tests;

public sealed class MainViewModelMemoryTests
{
    [Fact]
    public async Task DedicatedMemoryViewLoadsLazilyAndSearchesKeysAndValues()
    {
        var settingsPath = TemporarySettingsPath();
        try
        {
            var client = new StubBdClient
            {
                Memories =
                [
                    new BdMemoryEntry("z-last", "Semaphore repair notes"),
                    new BdMemoryEntry("a-first", "Quiet opening"),
                ],
            };
            var viewModel = CreateViewModel(client, settingsPath);

            await viewModel.InitializeAsync();

            Assert.Equal(0, client.MemoryLoadCount);
            Assert.True(viewModel.ShowBeadSurface);

            await viewModel.ShowMemoriesCommand.ExecuteAsync(null);

            Assert.True(viewModel.IsMemoryMode);
            Assert.True(viewModel.ShowMemorySurface);
            Assert.False(viewModel.ShowBeadSurface);
            Assert.Equal(1, client.MemoryLoadCount);
            Assert.Equal(2, viewModel.MemoryCount);
            Assert.Equal(["a-first", "z-last"], viewModel.VisibleMemories.Select(row => row.Key));

            viewModel.MemorySearchText = "semaphore";

            var match = Assert.Single(viewModel.VisibleMemories);
            Assert.Equal("z-last", match.Key);
            viewModel.SelectedMemory = match;
            Assert.True(viewModel.HasMemorySelection);
            Assert.True(viewModel.HasSelection);

            viewModel.CloseDetailCommand.Execute(null);

            Assert.Null(viewModel.SelectedMemory);
            Assert.False(viewModel.HasSelection);

            viewModel.SearchText = "town-1";
            viewModel.SelectedNavigation = viewModel.NavigationItems.Single(item =>
                item.Mode == DashboardMode.All);

            Assert.False(viewModel.IsMemoryMode);
            Assert.Equal("town-1", viewModel.SearchText);
            Assert.Equal("semaphore", viewModel.MemorySearchText);

            await viewModel.ShowMemoriesCommand.ExecuteAsync(null);

            Assert.Equal(1, client.MemoryLoadCount);
            Assert.Equal("semaphore", viewModel.MemorySearchText);
            Assert.Equal("z-last", Assert.Single(viewModel.VisibleMemories).Key);
        }
        finally
        {
            DeleteSettingsDirectory(settingsPath);
        }
    }

    [Fact]
    public async Task RefreshReloadsOnlyTheActiveSurface()
    {
        var settingsPath = TemporarySettingsPath();
        try
        {
            var client = new StubBdClient
            {
                Memories = [new BdMemoryEntry("before", "old")],
            };
            var viewModel = CreateViewModel(client, settingsPath);
            await viewModel.InitializeAsync();
            await viewModel.ShowMemoriesCommand.ExecuteAsync(null);
            client.Memories = [new BdMemoryEntry("after", "new")];

            await viewModel.RefreshCommand.ExecuteAsync(null);

            Assert.Equal(1, client.WorkspaceLoadCount);
            Assert.Equal(2, client.MemoryLoadCount);
            Assert.Equal("after", Assert.Single(viewModel.VisibleMemories).Key);

            viewModel.SelectedNavigation = viewModel.NavigationItems.Single(item =>
                item.Mode == DashboardMode.All);
            await viewModel.RefreshCommand.ExecuteAsync(null);

            Assert.Equal(2, client.WorkspaceLoadCount);
            Assert.Equal(2, client.MemoryLoadCount);
        }
        finally
        {
            DeleteSettingsDirectory(settingsPath);
        }
    }

    [Fact]
    public async Task MemoryFailureLeavesIssueSnapshotUsable()
    {
        var settingsPath = TemporarySettingsPath();
        try
        {
            var client = new StubBdClient
            {
                MemoryException = new BdClientException("memory catalog unavailable"),
            };
            var viewModel = CreateViewModel(client, settingsPath);
            await viewModel.InitializeAsync();

            await viewModel.ShowMemoriesCommand.ExecuteAsync(null);

            Assert.True(viewModel.SnapshotLoaded);
            Assert.Equal(1, viewModel.ActiveCount);
            Assert.Empty(viewModel.VisibleMemories);
            Assert.Contains("memory catalog unavailable", viewModel.ErrorMessage);

            viewModel.SelectedNavigation = viewModel.NavigationItems.Single(item =>
                item.Mode == DashboardMode.Now);

            Assert.True(viewModel.ShowBeadSurface);
            Assert.Equal("town-1", Assert.Single(viewModel.VisibleRows).Id);
        }
        finally
        {
            DeleteSettingsDirectory(settingsPath);
        }
    }

    private static MainViewModel CreateViewModel(StubBdClient client, string settingsPath) =>
        new(
            client,
            new UserSettingsStore(settingsPath),
            "C:/workspace");

    private static string TemporarySettingsPath() =>
        Path.Combine(
            Path.GetTempPath(),
            $"bdeyes-memory-tests-{Guid.NewGuid():N}",
            "settings.json");

    private static void DeleteSettingsDirectory(string settingsPath)
    {
        var directory = Path.GetDirectoryName(settingsPath);
        if (directory is not null && Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class StubBdClient : IBdClient
    {
        private static readonly DateTimeOffset Now = new(2026, 9, 13, 12, 0, 0, TimeSpan.Zero);

        public IReadOnlyList<BdMemoryEntry> Memories { get; set; } = [];

        public Exception? MemoryException { get; set; }

        public int WorkspaceLoadCount { get; private set; }

        public int MemoryLoadCount { get; private set; }

        public Task<BdWorkspaceSnapshot> LoadWorkspaceAsync(
            string workspacePath,
            CancellationToken cancellationToken = default)
        {
            WorkspaceLoadCount++;
            IReadOnlyList<BeadIssue> issues =
            [
                new BeadIssue
                {
                    Id = "town-1",
                    Title = "Keep issue browsing alive",
                    Status = "in_progress",
                    CreatedAt = Now.AddHours(-2),
                    UpdatedAt = Now,
                },
            ];
            return Task.FromResult(new BdWorkspaceSnapshot(
                workspacePath,
                "bd 1.2.2",
                Now,
                issues,
                new WorkspaceContentRevision(issues.Count, (ulong)WorkspaceLoadCount)));
        }

        public Task<IReadOnlyList<BdMemoryEntry>> LoadMemoriesAsync(
            string workspacePath,
            CancellationToken cancellationToken = default)
        {
            MemoryLoadCount++;
            return MemoryException is null
                ? Task.FromResult(Memories)
                : Task.FromException<IReadOnlyList<BdMemoryEntry>>(MemoryException);
        }

        public Task<BeadIssue> LoadDetailAsync(
            string workspacePath,
            string issueId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new BeadIssue { Id = issueId, Title = issueId });
    }
}
