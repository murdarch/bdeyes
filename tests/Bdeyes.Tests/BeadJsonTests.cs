using System.Text.Json;
using Bdeyes.Models;

namespace Bdeyes.Tests;

public sealed class BeadJsonTests
{
    [Fact]
    public void CurrentBdFieldsDeserializeWithoutLosingGraphOrActivity()
    {
        const string json = """
            [
              {
                "id": "town-42",
                "title": "Make work visible",
                "status": "in_progress",
                "priority": 1,
                "issue_type": "feature",
                "assignee": "Justice",
                "owner": "justice@constellation.local",
                "labels": ["ui", "observability"],
                "updated_at": "2026-08-14T12:00:00Z",
                "dependencies": [
                  {
                    "issue_id": "town-42",
                    "depends_on_id": "town-1",
                    "type": "blocks"
                  }
                ],
                "comments": [
                  {
                    "id": "comment-1",
                    "issue_id": "town-42",
                    "author": "Conway",
                    "text": "This is the useful edge.",
                    "created_at": "2026-08-14T12:01:00Z"
                  }
                ]
              }
            ]
            """;

        var issue = Assert.Single(JsonSerializer.Deserialize<List<BeadIssue>>(json)!);

        Assert.Equal("town-42", issue.Id);
        Assert.Equal("Justice", issue.Assignee);
        Assert.Equal(["ui", "observability"], issue.Labels);
        Assert.Equal("town-1", Assert.Single(issue.Dependencies).DependsOnId);
        Assert.Equal("This is the useful edge.", Assert.Single(issue.Comments).Text);
        Assert.Equal(new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero), issue.UpdatedAt);
    }
}
