using System.Text.Json.Serialization;

namespace Bdeyes.Models;

public sealed record BdEventRecord
{
    [JsonPropertyName("seq")]
    public long Sequence { get; init; }

    [JsonPropertyName("ts")]
    public DateTimeOffset? Timestamp { get; init; }

    [JsonPropertyName("op")]
    public string Operation { get; init; } = string.Empty;

    [JsonPropertyName("issue_id")]
    public string IssueId { get; init; } = string.Empty;

    [JsonPropertyName("issue")]
    public BeadIssue? Issue { get; init; }

    [JsonPropertyName("dep")]
    public BdEventDependency? Dependency { get; init; }

    [JsonPropertyName("comment")]
    public BdEventComment? Comment { get; init; }
}

public sealed record BdEventDependency
{
    [JsonPropertyName("kind")]
    public string Kind { get; init; } = string.Empty;

    [JsonPropertyName("target")]
    public string Target { get; init; } = string.Empty;
}

public sealed record BdEventComment
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("author")]
    public string Author { get; init; } = string.Empty;

    [JsonPropertyName("text")]
    public string Text { get; init; } = string.Empty;

    [JsonPropertyName("created_at")]
    public DateTimeOffset? CreatedAt { get; init; }
}

public sealed record BdEventBaseline(bool IsAvailable, long Sequence, string? FallbackReason)
{
    public static BdEventBaseline Available(long sequence) => new(true, sequence, null);

    public static BdEventBaseline Unavailable(string reason) => new(false, 0, reason);
}
