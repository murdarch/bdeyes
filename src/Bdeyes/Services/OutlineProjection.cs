namespace Bdeyes.Services;

public sealed record OutlineProjectionResult(
    IReadOnlySet<string> DirectMatchIds,
    IReadOnlySet<string> IncludedIds,
    IReadOnlySet<string> RequiredExpandedIds);

public static class OutlineProjection
{
    public static OutlineProjectionResult Create(
        IEnumerable<string> directMatchIds,
        BeadAnalyzer analyzer,
        bool includeDescendantsOfMatches = false)
    {
        var direct = directMatchIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var included = new HashSet<string>(direct, StringComparer.OrdinalIgnoreCase);
        var requiredExpanded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var id in direct)
        {
            foreach (var ancestor in analyzer.AncestorsOf(id))
            {
                included.Add(ancestor.Id);
                requiredExpanded.Add(ancestor.Id);
            }

            if (!includeDescendantsOfMatches)
            {
                continue;
            }

            foreach (var descendant in analyzer.DescendantsOf(id))
            {
                included.Add(descendant.Id);
            }
        }

        return new OutlineProjectionResult(direct, included, requiredExpanded);
    }
}
