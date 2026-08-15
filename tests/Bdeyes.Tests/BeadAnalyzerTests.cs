using Bdeyes.Models;
using Bdeyes.Services;

namespace Bdeyes.Tests;

public sealed class BeadAnalyzerTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void OpenDependencyBlocksUntilTheDependencyCloses()
    {
        var blocker = Issue("work-1", status: "open");
        var waiting = Issue(
            "work-2",
            dependencies:
            [
                new BeadDependency
                {
                    IssueId = "work-2",
                    DependsOnId = "work-1",
                    Type = "blocks",
                },
            ]);

        var blockedFacts = new BeadAnalyzer([blocker, waiting], Now).Analyze(waiting);
        var closedBlocker = blocker with { Status = "closed" };
        var readyFacts = new BeadAnalyzer([closedBlocker, waiting], Now).Analyze(waiting);

        Assert.Equal(BlockSeverity.Direct, blockedFacts.BlockSeverity);
        Assert.False(blockedFacts.IsReady);
        Assert.Equal(["work-1"], blockedFacts.ActiveBlockerIds);
        Assert.Equal(BlockSeverity.None, readyFacts.BlockSeverity);
        Assert.True(readyFacts.IsReady);
    }

    [Fact]
    public void EpicReportsPartialAndCompleteBlockedPaths()
    {
        var epic = Issue("epic", type: "epic");
        var blocker = Issue("gate");
        var blockedChild = Issue(
            "child-a",
            parent: "epic",
            dependencies:
            [
                new BeadDependency
                {
                    IssueId = "child-a",
                    DependsOnId = "gate",
                    Type = "blocks",
                },
            ]);
        var movableChild = Issue("child-b", parent: "epic");

        var partial = new BeadAnalyzer([epic, blocker, blockedChild, movableChild], Now)
            .Analyze(epic);
        var secondBlockedChild = blockedChild with
        {
            Id = "child-b",
            Dependencies =
            [
                new BeadDependency
                {
                    IssueId = "child-b",
                    DependsOnId = "gate",
                    Type = "blocks",
                },
            ],
        };
        var complete = new BeadAnalyzer([epic, blocker, blockedChild, secondBlockedChild], Now)
            .Analyze(epic);

        Assert.Equal(BlockSeverity.Partial, partial.BlockSeverity);
        Assert.Equal(BlockSeverity.Complete, complete.BlockSeverity);
    }

    [Fact]
    public void ReadyUnclaimedAndStaleAreSeparateFacts()
    {
        var quiet = Issue("quiet", updatedAt: Now.AddDays(-8));
        var claimed = Issue("claimed", assignee: "Justice", updatedAt: Now.AddHours(-1));
        var analyzer = new BeadAnalyzer([quiet, claimed], Now);

        var quietFacts = analyzer.Analyze(quiet);
        var claimedFacts = analyzer.Analyze(claimed);

        Assert.True(quietFacts.IsReady);
        Assert.True(quietFacts.IsUnclaimed);
        Assert.True(quietFacts.IsStale);
        Assert.True(claimedFacts.IsReady);
        Assert.False(claimedFacts.IsUnclaimed);
        Assert.False(claimedFacts.IsStale);
    }

    [Fact]
    public void ParentChildEdgesAreAuthoritativeAndDottedIdsAreOnlyNames()
    {
        var epic = Issue("town");
        var linked = Issue(
            "town.1",
            dependencies:
            [
                new BeadDependency
                {
                    IssueId = "town.1",
                    DependsOnId = "town",
                    Type = "parent-child",
                },
            ]);
        var dottedButUnlinked = Issue("town.2");
        var analyzer = new BeadAnalyzer([epic, linked, dottedButUnlinked], Now);

        Assert.Equal(linked, Assert.Single(analyzer.ChildrenOf(epic.Id)));
        Assert.Equal(epic, analyzer.ParentOf(linked));
        Assert.Null(analyzer.ParentOf(dottedButUnlinked));
        Assert.Equal([epic], analyzer.AncestorsOf(linked.Id));
    }

    [Fact]
    public void CyclicParentsArePrunedIntoOneConsistentHierarchy()
    {
        var a = Issue("a", parent: "b");
        var b = Issue("b", parent: "a");
        var analyzer = new BeadAnalyzer([a, b], Now);

        Assert.Equal(b, analyzer.ParentOf(a));
        Assert.Null(analyzer.ParentOf(b));
        Assert.Equal([a], analyzer.ChildrenOf(b.Id));
        Assert.Empty(analyzer.ChildrenOf(a.Id));
        Assert.Equal([b], analyzer.AncestorsOf(a.Id));
        Assert.Equal([a], analyzer.DescendantsOf(b.Id));
        Assert.Empty(analyzer.Analyze(a).Children);
    }

    private static BeadIssue Issue(
        string id,
        string status = "open",
        string type = "task",
        string? parent = null,
        string? assignee = null,
        DateTimeOffset? updatedAt = null,
        IReadOnlyList<BeadDependency>? dependencies = null) =>
        new()
        {
            Id = id,
            Title = id,
            Status = status,
            IssueType = type,
            Parent = parent,
            Assignee = assignee,
            CreatedAt = Now.AddDays(-10),
            UpdatedAt = updatedAt ?? Now,
            Dependencies = dependencies ?? [],
        };
}
