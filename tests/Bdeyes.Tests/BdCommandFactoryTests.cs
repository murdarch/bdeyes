using Bdeyes.Services;

namespace Bdeyes.Tests;

public sealed class BdCommandFactoryTests
{
    [Fact]
    public void EveryCommandEntersBdReadOnlyMode()
    {
        var commands = new[]
        {
            BdCommandFactory.ListIssues("C:/workspace with spaces"),
            BdCommandFactory.ShowIssue("C:/workspace with spaces", "town-42"),
            BdCommandFactory.Version("C:/workspace with spaces"),
        };

        Assert.All(commands, command => Assert.Equal("--readonly", command[0]));
        Assert.All(commands, command => Assert.Equal("C:/workspace with spaces", command[2]));
    }

    [Fact]
    public void SnapshotCommandRequestsCompleteFlatJson()
    {
        var command = BdCommandFactory.ListIssues("/workspace");

        Assert.Equal(
            ["--readonly", "-C", "/workspace", "list", "--all", "--limit", "0", "--flat", "--json"],
            command);
    }

    [Fact]
    public void DetailCommandRequestsConversationAndReverseLinks()
    {
        var command = BdCommandFactory.ShowIssue("/workspace", "town-42");

        Assert.Contains("--include-comments", command);
        Assert.Contains("--include-dependents", command);
        Assert.Equal("town-42", command[4]);
    }
}
