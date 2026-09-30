namespace SafePatch.Authoring.Output;

/// <summary>Counts of a result's rows by one column, the largest first.</summary>
public sealed record Grouping(string By, IReadOnlyList<GroupCount> Counts);

public sealed record GroupCount(string Key, int Count);

/// <summary>
/// The answer to a query: a table of text cells. Detail views (a record, a comparison, a query's output) are a
/// single <see cref="LineColumn"/> of lines. Every result is rendered within a budget, paged through a handle and
/// exported the same way.
/// </summary>
/// <param name="Title">What was asked, e.g. <c>LeveledItem records</c>.</param>
/// <param name="Groups">Counts over all rows, shown before the rows so a cut-off result still summarises the whole.</param>
/// <param name="Notes">Lines shown first: warnings, such as a stale load order.</param>
/// <param name="Hint">How to narrow the result when it is cut off, e.g. <c>add type or plugin</c>.</param>
public sealed record ResultSet(
    string Title,
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyList<string?>> Rows,
    IReadOnlyList<Grouping>? Groups = null,
    IReadOnlyList<string>? Notes = null,
    string? Hint = null)
{
    public const string LineColumn = "Line";

    public TimeSpan Elapsed { get; init; }

    /// <summary>The snapshot the result was computed from; 0 when it needs none.</summary>
    public int Generation { get; init; }

    /// <summary>The query behind the result failed (a query program that did not compile or run); the rows say why.</summary>
    public bool Failed { get; init; }

    public bool IsLines => Columns is [LineColumn];

    public static ResultSet Lines(string title, IEnumerable<string> lines, IReadOnlyList<string>? notes = null, string? hint = null) =>
        new(title, [LineColumn], [.. lines.Select(l => (IReadOnlyList<string?>)[l])], Notes: notes, Hint: hint);

    /// <summary>Counts rows by a key taken from each, largest first.</summary>
    public static Grouping GroupBy(string by, IEnumerable<string?> keys) =>
        new(by, [.. keys.GroupBy(k => k ?? "(none)").Select(g => new GroupCount(g.Key, g.Count())).OrderByDescending(g => g.Count).ThenBy(g => g.Key, StringComparer.Ordinal)]);
}
