using System.Text.Json.Serialization;

namespace Bdeyes.Models;

public sealed record BeadIssue
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("design")]
    public string? Design { get; init; }

    [JsonPropertyName("acceptance_criteria")]
    public string? AcceptanceCriteria { get; init; }

    [JsonPropertyName("notes")]
    public string? Notes { get; init; }

    [JsonPropertyName("status")]
    public string Status { get; init; } = "open";

    [JsonPropertyName("priority")]
    public int Priority { get; init; } = 2;

    [JsonPropertyName("issue_type")]
    public string IssueType { get; init; } = "task";

    [JsonPropertyName("assignee")]
    public string? Assignee { get; init; }

    [JsonPropertyName("owner")]
    public string? Owner { get; init; }

    [JsonPropertyName("created_by")]
    public string? CreatedBy { get; init; }

    [JsonPropertyName("parent")]
    public string? Parent { get; init; }

    [JsonPropertyName("created_at")]
    public DateTimeOffset? CreatedAt { get; init; }

    [JsonPropertyName("updated_at")]
    public DateTimeOffset? UpdatedAt { get; init; }

    [JsonPropertyName("started_at")]
    public DateTimeOffset? StartedAt { get; init; }

    [JsonPropertyName("closed_at")]
    public DateTimeOffset? ClosedAt { get; init; }

    [JsonPropertyName("defer_until")]
    public DateTimeOffset? DeferUntil { get; init; }

    [JsonPropertyName("close_reason")]
    public string? CloseReason { get; init; }

    [JsonPropertyName("labels")]
    public IReadOnlyList<string> Labels { get; init; } = [];

    [JsonPropertyName("dependencies")]
    public IReadOnlyList<BeadDependency> Dependencies { get; init; } = [];

    [JsonPropertyName("dependents")]
    public IReadOnlyList<BeadDependent> Dependents { get; init; } = [];

    [JsonPropertyName("comments")]
    public IReadOnlyList<BeadComment> Comments { get; init; } = [];

    [JsonPropertyName("dependency_count")]
    public int DependencyCount { get; init; }

    [JsonPropertyName("dependent_count")]
    public int DependentCount { get; init; }

    [JsonPropertyName("comment_count")]
    public int CommentCount { get; init; }
}

public sealed record BeadDependency
{
    [JsonPropertyName("issue_id")]
    public string IssueId { get; init; } = string.Empty;

    [JsonPropertyName("depends_on_id")]
    public string DependsOnId { get; init; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;

    [JsonPropertyName("created_at")]
    public DateTimeOffset? CreatedAt { get; init; }

    [JsonPropertyName("created_by")]
    public string? CreatedBy { get; init; }
}

public sealed record BeadDependent
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;

    [JsonPropertyName("priority")]
    public int Priority { get; init; }

    [JsonPropertyName("issue_type")]
    public string IssueType { get; init; } = string.Empty;

    [JsonPropertyName("dependency_type")]
    public string DependencyType { get; init; } = string.Empty;

    [JsonPropertyName("created_at")]
    public DateTimeOffset? CreatedAt { get; init; }

    [JsonPropertyName("updated_at")]
    public DateTimeOffset? UpdatedAt { get; init; }
}

public sealed record BeadComment
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("issue_id")]
    public string IssueId { get; init; } = string.Empty;

    [JsonPropertyName("author")]
    public string Author { get; init; } = string.Empty;

    [JsonPropertyName("text")]
    public string Text { get; init; } = string.Empty;

    [JsonPropertyName("created_at")]
    public DateTimeOffset? CreatedAt { get; init; }
}
