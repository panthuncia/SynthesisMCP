using System.Globalization;
using System.Text;

namespace SafePatch.Authoring.Output;

/// <summary>
/// Renders a result as compact text within a character budget, so an agent's context never takes a dump. When
/// everything fits, it is all there. Otherwise the header still gives the total and the groups over every row,
/// then as many rows as fit, then the exact next call. Nothing is cut off without saying so.
/// </summary>
public static class Budgeted
{
    public const int DefaultBudget = 8_000;
    public const int MaxBudget = 60_000;
    public const int MinBudget = 1_000;

    /// <summary>The longest table cell or line shown; longer ones are cut, saying how much is left.</summary>
    public const int MaxCell = 400;
    public const int MaxLine = 2_000;
    private const int MaxGroupKeys = 10;

    public static int Clamp(int? budget) => Math.Clamp(budget ?? DefaultBudget, MinBudget, MaxBudget);

    /// <param name="handle">The result's handle, if it was kept for paging.</param>
    /// <param name="continuation">How to ask for the rows from an offset, e.g. <c>read_results(handle: "r3", offset: {0})</c>.</param>
    public static string Render(ResultSet set, int offset = 0, int budget = DefaultBudget, string? handle = null, string? continuation = null)
    {
        var text = new StringBuilder();
        var total = set.Rows.Count;
        offset = Math.Clamp(offset, 0, total);

        var unit = set.IsLines ? "line" : "row";
        text.Append(CultureInfo.InvariantCulture, $"{set.Title}: {Count(total, unit)}");
        if (set.Elapsed > TimeSpan.Zero) text.Append(CultureInfo.InvariantCulture, $" ({set.Elapsed.TotalSeconds:0.0} s)");
        if (handle is not null) text.Append(CultureInfo.InvariantCulture, $", handle {handle}");
        text.AppendLine();
        foreach (var note in set.Notes ?? []) text.AppendLine(note);

        if (offset == 0 && total > 1)
        {
            foreach (var group in set.Groups ?? [])
            {
                if (group.Counts.Count == 0) continue;
                var shown = group.Counts.Take(MaxGroupKeys).Select(g => $"{g.Key} {g.Count:N0}");
                var more = group.Counts.Count > MaxGroupKeys ? $" (+{group.Counts.Count - MaxGroupKeys} more)" : "";
                text.AppendLine(CultureInfo.InvariantCulture, $"By {group.By}: {string.Join(" · ", shown)}{more}");
            }
        }
        if (!set.IsLines && total > 0) text.AppendLine(string.Join(" | ", set.Columns));

        // Keep room for the footer; always show at least one row, so paging moves forward.
        var footerRoom = 200 + (continuation?.Length ?? 0) + (set.Hint?.Length ?? 0);
        var end = offset;
        while (end < total)
        {
            var row = Row(set, set.Rows[end]);
            if (end > offset && text.Length + row.Length + footerRoom > budget) break;
            text.AppendLine(row);
            end++;
        }

        if (end < total || offset > 0)
        {
            text.Append(CultureInfo.InvariantCulture, $"Shown {unit}s {offset + 1:N0}–{end:N0} of {total:N0}.");
            if (end < total && continuation is not null) text.Append(" Next: ").Append(string.Format(CultureInfo.InvariantCulture, continuation, end)).Append('.');
            if (end < total && set.Hint is not null) text.Append(" Or narrow it: ").Append(set.Hint).Append('.');
            text.AppendLine();
        }
        return text.ToString().TrimEnd();
    }

    private static string Row(ResultSet set, IReadOnlyList<string?> cells) =>
        set.IsLines ? Cut(cells[0] ?? "", MaxLine) : string.Join(" | ", cells.Select(c => Cut((c ?? "").ReplaceLineEndings(" "), MaxCell)));

    /// <summary>Text cut to <paramref name="max"/> characters, saying how many more there were.</summary>
    public static string Cut(string text, int max) => text.Length <= max ? text : text[..max] + $"… (+{text.Length - max:N0} chars)";

    private static string Count(int n, string unit) => $"{n:N0} {unit}{(n == 1 ? "" : "s")}";
}
