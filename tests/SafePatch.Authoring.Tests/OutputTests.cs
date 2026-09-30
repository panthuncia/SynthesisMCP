using SafePatch.Authoring.Output;
using SafePatch.Host;
using SafePatch.TestSupport;

namespace SafePatch.Authoring.Tests;

/// <summary>Results reach an agent's context only within a budget, summarised, with a way to the rest.</summary>
public sealed class OutputTests
{
    private static ResultSet Large(int rows = 5_000, int generation = 1) =>
        new ResultSet("LeveledItem records", ["FormKey", "Type", "EditorID"],
            [.. Enumerable.Range(0, rows).Select(i => (IReadOnlyList<string?>)[$"{i:X6}:Test.esp", i % 3 == 0 ? "Npc" : "LeveledItem", $"Record{i}"])],
            [ResultSet.GroupBy("type", Enumerable.Range(0, rows).Select(i => i % 3 == 0 ? "Npc" : "LeveledItem"))],
            Hint: "add type or plugin") { Generation = generation };

    [Fact]
    public void A_large_result_stays_within_its_budget_and_still_summarises_every_row()
    {
        var text = Budgeted.Render(Large(), budget: Budgeted.DefaultBudget, handle: "r1", continuation: "read_results(handle: \"r1\", offset: {0})");

        Assert.True(text.Length <= Budgeted.DefaultBudget, $"{text.Length} characters");
        Assert.StartsWith("LeveledItem records: 5,000 rows, handle r1", text);
        Assert.Contains("By type: LeveledItem 3,333 · Npc 1,667", text);
        Assert.Matches(@"Shown rows 1–\d+ of 5,000\. Next: read_results\(handle: ""r1"", offset: \d+\)\. Or narrow it: add type or plugin\.", text);
    }

    [Fact]
    public void Paging_returns_every_row_exactly_once()
    {
        var set = Large();
        var seen = new List<string>();
        var offset = 0;
        while (offset < set.Rows.Count)
        {
            var page = Budgeted.Render(set, offset, budget: 3_000, continuation: "{0}");
            var rows = page.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Contains(":Test.esp |")).ToList();
            Assert.NotEmpty(rows);
            seen.AddRange(rows);
            offset += rows.Count;
        }

        Assert.Equal(set.Rows.Select(r => string.Join(" | ", r)), seen);
    }

    [Fact]
    public void A_small_result_is_returned_whole_with_no_paging()
    {
        var text = Budgeted.Render(Large(rows: 3));

        Assert.Equal(4 + 1 + 1, text.Split('\n').Length); // title, groups, header, 3 rows
        Assert.DoesNotContain("Shown", text);
    }

    [Fact]
    public void Long_cells_are_cut_saying_how_much_is_left()
    {
        var set = ResultSet.Lines("Record", [new string('x', Budgeted.MaxLine + 50)]);

        Assert.EndsWith("… (+50 chars)", Budgeted.Render(set, budget: Budgeted.MaxBudget));
    }

    [Fact]
    public void Handles_keep_the_latest_results_and_refuse_ones_from_an_earlier_load_order()
    {
        var store = new ResultStore(capacity: 2);
        var first = store.Add(Large(generation: 1));
        var second = store.Add(Large(generation: 1));
        Assert.Same(store.Get(second, 1), store.Get(second, 1));

        store.Add(Large(generation: 1));
        Assert.Contains("only the last 2", Assert.Throws<SafePatchException>(() => store.Get(first, 1)).Message);
        Assert.Contains("reloaded", Assert.Throws<SafePatchException>(() => store.Get(second, 2)).Message);
    }

    [Fact]
    public void A_result_exports_as_JSON_lines_or_CSV_and_never_replaces_a_file()
    {
        using var temp = new TempFolder();
        var set = new ResultSet("T", ["A", "B"], [["1", "x,\"y\""], ["2", null]]);

        var (jsonl, _) = ResultExport.Write(set, temp.File("r.jsonl"), ExportFormat.Jsonl);
        var (csv, _) = ResultExport.Write(set, temp.File("r.csv"), ExportFormat.Csv);

        Assert.Equal(["""{"A":"1","B":"x,\"y\""}""", """{"A":"2","B":null}"""], File.ReadAllLines(jsonl));
        Assert.Equal(["A,B", "1,\"x,\"\"y\"\"\"", "2,"], File.ReadAllLines(csv));
        Assert.Throws<SafePatchException>(() => ResultExport.Write(set, csv, ExportFormat.Csv));
    }
}
