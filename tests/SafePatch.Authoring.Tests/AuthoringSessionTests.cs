using SafePatch.Authoring.Output;
using SafePatch.Authoring.Query;
using SafePatch.Authoring.Sources;
using SafePatch.Host;
using SafePatch.TestSupport;

namespace SafePatch.Authoring.Tests;

/// <summary>A server's session: results by handle, paging, export, and reloading the load order.</summary>
public sealed class AuthoringSessionTests : IDisposable
{
    private readonly TempFolder _temp = new("SafePatchSession-");
    private readonly string _pluginsFile;
    private readonly AuthoringSession _session;

    public AuthoringSessionTests()
    {
        (_, _pluginsFile) = ConflictFixture.Write(_temp.File("Data"));
        _session = new AuthoringSession(LoadOrderSnapshot.Open(_temp.File("Data"), _pluginsFile));
    }

    private string All(int? budget = null) => _session.Present(_session.Queries.FindRecords(new RecordFilter(["Weapon", "LeveledItem", "Ingestible"])), budget);

    [Fact]
    public void A_presented_result_is_kept_under_a_handle_to_page_through()
    {
        var first = All(budget: Budgeted.MinBudget);
        var handle = first.Split(", handle ")[1].Split(Environment.NewLine)[0];

        Assert.StartsWith("r", handle);
        Assert.Contains($"read_results(handle: \"{handle}\", offset: ", first);
        var shown = first.Split(Environment.NewLine).Count(l => l.Contains(":Skyrim.esm |") || l.Contains(".esp |"));
        var next = _session.ReadResults(handle, shown, Budgeted.MaxBudget);
        Assert.Contains($"Shown rows {shown + 1}–", next);
    }

    [Fact]
    public void Results_export_to_new_files_outside_the_load_order_only()
    {
        All();

        Assert.Contains("Wrote 10 rows of r1", _session.Export("r1", _temp.File(@"out\weapons.jsonl"), ExportFormat.Jsonl));
        Assert.Equal(10, File.ReadAllLines(_temp.File(@"out\weapons.jsonl")).Length);
        Assert.Throws<SafePatchException>(() => _session.Export("r1", _temp.File(@"Data\weapons.jsonl"), ExportFormat.Jsonl));
        Assert.Throws<SafePatchException>(() => _session.Export("r9", _temp.File("other.jsonl"), ExportFormat.Jsonl));
    }

    [Fact]
    public void Nothing_is_exported_into_an_MO2_instance_or_its_mods()
    {
        using var mo2Temp = new TempFolder();
        var mo2 = Mo2Fixture.Write(mo2Temp.Path);
        using var session = new AuthoringSession(LoadOrderSnapshot.Open(new Mo2ProfileSource(mo2.Instance)));
        session.Present(session.Queries.LoadOrder());

        Assert.Throws<SafePatchException>(() => session.Export("r1", Path.Combine(mo2.ModFolder(Mo2Fixture.CacoMod), "x.jsonl"), ExportFormat.Jsonl));
        Assert.Throws<SafePatchException>(() => session.Export("r1", Path.Combine(mo2.Instance, "x.jsonl"), ExportFormat.Jsonl));
        Assert.Throws<SafePatchException>(() => session.GuardOutput(Path.Combine(mo2.ProfileFolder, "patcher")));
    }

    [Fact]
    public void Reloading_reads_the_load_order_again_and_retires_earlier_handles()
    {
        All();
        File.WriteAllLines(_pluginsFile, [$"*{ConflictFixture.A}"]);

        Assert.StartsWith("Reloaded 2 plugins", _session.Reload());
        Assert.Contains("reloaded", Assert.Throws<SafePatchException>(() => _session.ReadResults("r1", 0)).Message);
        Assert.Equal(2, _session.Queries.LoadOrder().Rows.Count);
    }

    [Fact]
    public void Without_a_load_order_queries_say_how_to_start_with_one()
    {
        using var session = new AuthoringSession(null);

        Assert.Contains("--mo2", Assert.Throws<SafePatchException>(() => session.Queries).Message);
        Assert.Equal(ConflictKind.Itm, AuthoringSession.Option("ITM", ConflictKind.Conflict));
        Assert.Throws<SafePatchException>(() => AuthoringSession.Option("sometimes", ConflictKind.Conflict));
    }

    public void Dispose()
    {
        _session.Dispose();
        _temp.Dispose();
    }
}
