namespace Bdeyes.ViewModels;

public enum PersonFilterKind
{
    All,
    Unassigned,
    Person,
}

public sealed record PersonFilterOption(
    PersonFilterKind Kind,
    string Label,
    string? Value,
    int Count)
{
    public bool IsRestrictive => Kind != PersonFilterKind.All;

    public string AutomationName =>
        $"{Label}, {Count:N0} bead{(Count == 1 ? string.Empty : "s")}";

    public bool Matches(string? candidate) => Kind switch
    {
        PersonFilterKind.All => true,
        PersonFilterKind.Unassigned => string.IsNullOrWhiteSpace(candidate),
        _ => string.Equals(Value, candidate?.Trim(), StringComparison.OrdinalIgnoreCase),
    };

    public bool RepresentsSameSelection(PersonFilterOption? other) =>
        other is not null &&
        Kind == other.Kind &&
        (Kind != PersonFilterKind.Person ||
         string.Equals(Value, other.Value, StringComparison.OrdinalIgnoreCase));

    public override string ToString() => AutomationName;
}
