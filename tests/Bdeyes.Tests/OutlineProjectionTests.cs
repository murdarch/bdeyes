using Bdeyes.Models;
using Bdeyes.Services;

namespace Bdeyes.Tests;

public sealed class OutlineProjectionTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 15, 1, 0, 0, TimeSpan.Zero);

    [Fact]
    public void MatchingLeafRetainsAndExpandsItsAncestorPath()
    {
        var epic = Issue("epic");
        var parent = Issue("task", "epic");
        var match = Issue("leaf", "task");
        var sibling = Issue("sibling", "epic");
        var analyzer = new BeadAnalyzer([epic, parent, match, sibling], Now);

        var projection = OutlineProjection.Create([match.Id], analyzer);

        Assert.True(projection.DirectMatchIds.SetEquals([match.Id]));
        Assert.True(projection.IncludedIds.SetEquals([epic.Id, parent.Id, match.Id]));
        Assert.True(projection.RequiredExpandedIds.SetEquals([epic.Id, parent.Id]));
        Assert.DoesNotContain(sibling.Id, projection.IncludedIds);
    }

    [Fact]
    public void MatchingEpicCanIncludeItsWholeSubtree()
    {
        var epic = Issue("epic");
        var child = Issue("child", "epic");
        var grandchild = Issue("grandchild", "child");
        var analyzer = new BeadAnalyzer([epic, child, grandchild], Now);

        var projection = OutlineProjection.Create(
            [epic.Id],
            analyzer,
            includeDescendantsOfMatches: true);

        Assert.True(projection.IncludedIds.SetEquals([epic.Id, child.Id, grandchild.Id]));
    }

    private static BeadIssue Issue(string id, string? parent = null) => new()
    {
        Id = id,
        Title = id,
        Status = "open",
        IssueType = id == "epic" ? "epic" : "task",
        Parent = parent,
        CreatedAt = Now,
        UpdatedAt = Now,
    };
}
