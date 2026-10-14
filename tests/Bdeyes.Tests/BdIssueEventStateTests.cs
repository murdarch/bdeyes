using Bdeyes.Models;
using Bdeyes.Services;

namespace Bdeyes.Tests;

public sealed class BdIssueEventStateTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 10, 14, 1, 45, 0, TimeSpan.Zero);

    [Fact]
    public void OrderedUpdatePreservesGraphDataAndDuplicateSequenceIsIgnored()
    {
        var original = Issue("child") with
        {
            Parent = "parent",
            DependencyCount = 1,
            Dependencies =
            [
                Dependency("child", "parent", "parent-child"),
            ],
        };
        var state = new BdIssueEventState([Issue("parent"), original], sequence: 10);
        var update = Event(11, "update", Issue("child") with
        {
            Title = "renamed",
            Status = "in_progress",
            Assignee = "Justice",
        });

        Assert.Equal(BdEventApplyResult.Applied, state.Apply(update));
        Assert.Equal(BdEventApplyResult.Duplicate, state.Apply(update));

        var child = state.Issues.Single(issue => issue.Id == "child");
        Assert.Equal("renamed", child.Title);
        Assert.Equal("in_progress", child.Status);
        Assert.Equal("Justice", child.Assignee);
        Assert.Equal("parent", child.Parent);
        Assert.Equal("parent", Assert.Single(child.Dependencies).DependsOnId);
        Assert.Equal(11, state.Sequence);
    }

    [Fact]
    public void CreateCloseAndDeleteReplayToTheFinalState()
    {
        var state = new BdIssueEventState([], sequence: 20);
        var created = Issue("new");

        Assert.Equal(BdEventApplyResult.Applied, state.Apply(Event(21, "create", created)));
        Assert.Equal(
            BdEventApplyResult.Applied,
            state.Apply(Event(22, "close", created with
            {
                Status = "closed",
                ClosedAt = Now,
                CloseReason = "complete",
            })));
        Assert.Equal(
            "closed",
            Assert.Single(state.Issues).Status);

        Assert.Equal(
            BdEventApplyResult.Applied,
            state.Apply(new BdEventRecord
            {
                Sequence = 23,
                Operation = "delete",
                IssueId = "new",
                Issue = null,
            }));
        Assert.Empty(state.Issues);
    }

    [Fact]
    public void DependencyEventsMaintainHierarchyAndCounts()
    {
        var state = new BdIssueEventState(
            [Issue("parent"), Issue("child")],
            sequence: 30);
        var add = new BdEventRecord
        {
            Sequence = 31,
            Operation = "dep_add",
            IssueId = "child",
            Issue = Issue("child"),
            Dependency = new BdEventDependency
            {
                Kind = "parent-child",
                Target = "parent",
            },
        };

        Assert.Equal(BdEventApplyResult.Applied, state.Apply(add));
        var parent = state.Issues.Single(issue => issue.Id == "parent");
        var child = state.Issues.Single(issue => issue.Id == "child");
        Assert.Equal("parent", child.Parent);
        Assert.Equal(1, child.DependencyCount);
        Assert.Equal(1, parent.DependentCount);

        Assert.Equal(
            BdEventApplyResult.Applied,
            state.Apply(add with
            {
                Sequence = 32,
                Operation = "dep_remove",
                Issue = child,
            }));
        parent = state.Issues.Single(issue => issue.Id == "parent");
        child = state.Issues.Single(issue => issue.Id == "child");
        Assert.Null(child.Parent);
        Assert.Empty(child.Dependencies);
        Assert.Equal(0, child.DependencyCount);
        Assert.Equal(0, parent.DependentCount);
    }

    [Fact]
    public void CommentEventAddsReplayableCommentWithoutDroppingDependencies()
    {
        var original = Issue("child") with
        {
            Dependencies =
            [
                Dependency("child", "blocker", "blocks"),
            ],
        };
        var state = new BdIssueEventState(
            [Issue("blocker"), original],
            sequence: 40);
        var comment = new BdEventRecord
        {
            Sequence = 41,
            Operation = "comment",
            IssueId = "child",
            Issue = Issue("child") with { Title = "with a comment" },
            Comment = new BdEventComment
            {
                Id = "comment-1",
                Author = "Justice",
                Text = "reviewed",
                CreatedAt = Now,
            },
        };

        Assert.Equal(BdEventApplyResult.Applied, state.Apply(comment));
        var child = state.Issues.Single(issue => issue.Id == "child");
        Assert.Equal("with a comment", child.Title);
        Assert.Equal("blocker", Assert.Single(child.Dependencies).DependsOnId);
        var replayed = Assert.Single(child.Comments);
        Assert.Equal("comment-1", replayed.Id);
        Assert.Equal("reviewed", replayed.Text);
    }

    [Fact]
    public void GapAndUnknownOperationDemandAFullRebaseline()
    {
        var state = new BdIssueEventState([Issue("one")], sequence: 50);

        Assert.Equal(
            BdEventApplyResult.Rebaseline,
            state.Apply(Event(52, "update", Issue("one"))));
        Assert.Equal(50, state.Sequence);
        Assert.Equal(
            BdEventApplyResult.Rebaseline,
            state.Apply(Event(51, "future_op", Issue("one"))));
        Assert.Equal(50, state.Sequence);
    }

    [Fact]
    public void EventJsonParserAcceptsRecordsAndRejectsStreamErrors()
    {
        const string recordJson =
            "{\"seq\":7,\"op\":\"update\",\"issue_id\":\"town-7\",\"issue\":{\"id\":\"town-7\",\"title\":\"changed\",\"status\":\"open\",\"priority\":2,\"issue_type\":\"task\",\"created_at\":\"2026-10-14T01:00:00Z\",\"updated_at\":\"2026-10-14T01:01:00Z\"}}";

        Assert.True(BdClient.TryParseEventLine(recordJson, out var record));
        Assert.Equal(7, record.Sequence);
        Assert.Equal("changed", record.Issue?.Title);
        Assert.False(BdClient.TryParseEventLine(
            "{\"code\":\"events_journal_truncated\",\"head\":12}",
            out _));
        Assert.False(BdClient.TryParseEventLine("not json", out _));
    }

    private static BdEventRecord Event(long sequence, string operation, BeadIssue issue) =>
        new()
        {
            Sequence = sequence,
            Operation = operation,
            IssueId = issue.Id,
            Issue = issue,
        };

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

    private static BeadDependency Dependency(
        string issueId,
        string targetId,
        string type) =>
        new()
        {
            IssueId = issueId,
            DependsOnId = targetId,
            Type = type,
        };
}
