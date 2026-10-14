using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Bdeyes.Models;
using Bdeyes.Services;
using Bdeyes.ViewModels;

namespace Bdeyes.Tests;

public sealed class MainViewModelEventTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 10, 14, 2, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task LiveCommentRefreshesSelectedInspectorWithoutAnotherSnapshotPoll()
    {
        var settingsPath = TemporarySettingsPath();
        var original = Issue("mine") with
        {
            Comments = [Comment("old", Now.AddMinutes(-1))],
        };
        var client = new StreamingBdClient([original], BdEventBaseline.Available(10));
        var viewModel = new MainViewModel(
            client,
            new UserSettingsStore(settingsPath),
            "C:/workspace");

        try
        {
            await viewModel.InitializeAsync();
            await client.StreamStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            viewModel.SelectedNavigation = viewModel.NavigationItems.Single(item =>
                item.Mode == DashboardMode.All);
            viewModel.SelectedRow = Assert.Single(viewModel.VisibleRows);
            Assert.Equal("old", Assert.Single(viewModel.Detail!.Comments).Text);

            var updated = original with
            {
                Title = "updated title",
                UpdatedAt = Now.AddMinutes(1),
                Comments =
                [
                    Comment("new", Now),
                    Comment("old", Now.AddMinutes(-1)),
                ],
            };
            client.Issues = [updated];
            await client.Events.Writer.WriteAsync(new BdEventRecord
            {
                Sequence = 11,
                Operation = "comment",
                IssueId = "mine",
                Issue = updated with { Comments = [] },
                Comment = new BdEventComment
                {
                    Id = "new",
                    Author = "Justice",
                    Text = "new",
                    CreatedAt = Now,
                },
            });

            await WaitUntilAsync(() =>
                viewModel.SelectedRow?.Issue.Title == "updated title" &&
                viewModel.Detail?.Comments.FirstOrDefault()?.Text == "new");

            Assert.True(viewModel.IsLiveUpdating);
            Assert.Equal(1, client.WorkspaceLoadCount);
            Assert.Equal("mine", viewModel.SelectedRow?.Id);
            Assert.StartsWith("live · updated", viewModel.LastRefreshedLabel);
        }
        finally
        {
            await viewModel.StopLiveUpdatesAsync();
            DeleteSettingsDirectory(settingsPath);
        }
    }

    [Fact]
    public async Task SequenceGapRebaselinesOnceAndFallsBackToPolling()
    {
        var settingsPath = TemporarySettingsPath();
        var original = Issue("mine");
        var client = new StreamingBdClient([original], BdEventBaseline.Available(20));
        var viewModel = new MainViewModel(
            client,
            new UserSettingsStore(settingsPath),
            "C:/workspace");

        try
        {
            await viewModel.InitializeAsync();
            await client.StreamStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            viewModel.SelectedNavigation = viewModel.NavigationItems.Single(item =>
                item.Mode == DashboardMode.All);
            var refreshed = original with
            {
                Title = "snapshot recovery",
                UpdatedAt = Now.AddMinutes(1),
            };
            client.Issues = [refreshed];
            await client.Events.Writer.WriteAsync(new BdEventRecord
            {
                Sequence = 22,
                Operation = "update",
                IssueId = "mine",
                Issue = refreshed,
            });

            await WaitUntilAsync(() =>
                client.WorkspaceLoadCount == 2 &&
                !viewModel.IsLiveUpdating);

            Assert.Equal("snapshot recovery", Assert.Single(viewModel.VisibleRows).Issue.Title);
            Assert.StartsWith("polling fallback · refreshed", viewModel.LastRefreshedLabel);
        }
        finally
        {
            await viewModel.StopLiveUpdatesAsync();
            DeleteSettingsDirectory(settingsPath);
        }
    }

    [Fact]
    public async Task UnavailableJournalKeepsTheExistingPollingMode()
    {
        var settingsPath = TemporarySettingsPath();
        var client = new StreamingBdClient(
            [Issue("mine")],
            BdEventBaseline.Unavailable("The events command is unavailable."));
        var viewModel = new MainViewModel(
            client,
            new UserSettingsStore(settingsPath),
            "C:/workspace");

        try
        {
            await viewModel.InitializeAsync();

            Assert.False(viewModel.IsLiveUpdating);
            Assert.Equal(1, client.WorkspaceLoadCount);
            Assert.StartsWith("polling · refreshed", viewModel.LastRefreshedLabel);
            Assert.False(client.StreamStarted.Task.IsCompleted);
        }
        finally
        {
            await viewModel.StopLiveUpdatesAsync();
            DeleteSettingsDirectory(settingsPath);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException("The live-update condition was not observed.");
            }

            await Task.Delay(10);
        }
    }

    private static BeadIssue Issue(string id) =>
        new()
        {
            Id = id,
            Title = id,
            Status = "open",
            Priority = 2,
            IssueType = "task",
            CreatedAt = Now.AddDays(-1),
            UpdatedAt = Now,
        };

    private static BeadComment Comment(string text, DateTimeOffset createdAt) =>
        new()
        {
            Id = text,
            IssueId = "mine",
            Author = "Justice",
            Text = text,
            CreatedAt = createdAt,
        };

    private static string TemporarySettingsPath() =>
        Path.Combine(
            Path.GetTempPath(),
            $"bdeyes-events-tests-{Guid.NewGuid():N}",
            "settings.json");

    private static void DeleteSettingsDirectory(string settingsPath)
    {
        var directory = Path.GetDirectoryName(settingsPath);
        if (directory is not null && Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class StreamingBdClient(
        IReadOnlyList<BeadIssue> issues,
        BdEventBaseline baseline) : IBdClient, IBdEventClient
    {
        private IReadOnlyList<BeadIssue> _issues = issues;
        private ulong _revision = 1;

        public Channel<BdEventRecord> Events { get; } =
            Channel.CreateUnbounded<BdEventRecord>();

        public TaskCompletionSource<bool> StreamStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int WorkspaceLoadCount { get; private set; }

        public IReadOnlyList<BeadIssue> Issues
        {
            get => _issues;
            set
            {
                _issues = value;
                _revision++;
            }
        }

        public Task<BdWorkspaceSnapshot> LoadWorkspaceAsync(
            string workspacePath,
            CancellationToken cancellationToken = default)
        {
            WorkspaceLoadCount++;
            return Task.FromResult(new BdWorkspaceSnapshot(
                workspacePath,
                "bd 1.3.0",
                Now.AddTicks(WorkspaceLoadCount),
                Issues,
                new WorkspaceContentRevision(Issues.Count, _revision)));
        }

        public Task<IReadOnlyList<BdMemoryEntry>> LoadMemoriesAsync(
            string workspacePath,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<BdMemoryEntry>>([]);

        public Task<BeadIssue> LoadDetailAsync(
            string workspacePath,
            string issueId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Issues.Single(issue => issue.Id == issueId));

        public Task<BdEventBaseline> ReadEventBaselineAsync(
            string workspacePath,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(baseline);

        public async IAsyncEnumerable<BdEventRecord> WatchEventsAsync(
            string workspacePath,
            long sequence,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            StreamStarted.TrySetResult(true);
            while (await Events.Reader.WaitToReadAsync(cancellationToken))
            {
                while (Events.Reader.TryRead(out var record))
                {
                    yield return record;
                }
            }
        }
    }
}
