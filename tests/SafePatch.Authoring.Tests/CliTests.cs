using System.Text.Json;
using SafePatch.Cli;
using SafePatch.TestSupport;

namespace SafePatch.Authoring.Tests;

/// <summary>The <c>safepatch</c> commands over the conflict fixture, as an agent or a user would call them.</summary>
public sealed class CliTests : IDisposable
{
    private readonly HostRun _fixture = new();
    private readonly string _program;

    public CliTests()
    {
        _program = Path.Combine(_fixture.Root, "merge.cs");
        File.WriteAllText(_program, SamplePrograms.LeveledListMerge);
    }

    private (int Exit, string Output, string Error) Run(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = CliApp.Run(args, output, error, InProcessWorkerLauncher.Real());
        return (exit, output.ToString(), error.ToString());
    }

    private (int Exit, JsonElement Json, string Error) RunJson(params string[] args)
    {
        var (exit, output, error) = Run(args);
        return (exit, output.Length == 0 ? default : JsonDocument.Parse(output).RootElement, error);
    }

    private string[] LoadOrder => ["--data", _fixture.DataFolder, "--plugins", _fixture.LoadOrderFile];

    [Theory]
    [InlineData]
    [InlineData("frobnicate", "--data", "x", "--plugins", "y")]
    [InlineData("record")]
    [InlineData("find", "--types")]
    public void Usage_errors_exit_with_2_and_print_the_usage(params string[] args)
    {
        var (exit, _, error) = Run(args);

        Assert.Equal(2, exit);
        Assert.Contains("safepatch <command>", error);
    }

    [Fact]
    public void Queries_print_text_in_full_or_JSON()
    {
        var (exit, text, _) = Run(["load-order", .. LoadOrder]);
        Assert.Equal(0, exit);
        Assert.Contains("Load order: 3 rows", text);
        Assert.Contains($"2 | {PluginFixture.Other} | PLUGIN", text);

        var (_, json, _) = RunJson(["find", "--types", "LeveledItem,Weapon", "--json", .. LoadOrder]);
        Assert.Equal(2, json.GetProperty("rows").GetArrayLength());
        Assert.Equal("FormKey", json.GetProperty("columns")[0].GetString());
    }

    [Fact]
    public void Each_query_command_answers_from_the_load_order()
    {
        Assert.Contains("Entries (3):", Run(["record", PluginFixture.ListEditorId, .. LoadOrder]).Output);
        Assert.Contains("CONFLICT, the winner loses", Run(["compare", PluginFixture.ListEditorId, .. LoadOrder]).Output);
        Assert.Contains($"LeveledItem.Entries: {PluginFixture.Caco} → {PluginFixture.Other} | 1",
            Run(["conflicts", "--types", "LeveledItem", .. LoadOrder]).Output);
        Assert.Contains(PluginFixture.ListEditorId, Run(["refs", "IronSword", "--direction", "in", "--types", "LeveledItem", .. LoadOrder]).Output);
        Assert.Contains("No loose file", Run(["asset", @"meshes\none.nif", .. LoadOrder]).Output);
        Assert.Contains("Entries[].Data.Reference | link → Item", Run(["type", "LeveledItem", .. LoadOrder]).Output);
    }

    [Fact]
    public void A_budget_summarises_and_an_offset_continues()
    {
        var (_, text, _) = Run(["type", "Npc", "--budget", "1000", .. LoadOrder]);

        Assert.True(text.Length <= 1_100, text);
        Assert.Matches(@"Next: --offset \d+", text);
        Assert.Contains("Shown rows 11–", Run(["type", "Npc", "--budget", "1000", "--offset", "10", .. LoadOrder]).Output);
    }

    [Fact]
    public void Results_can_be_written_to_a_new_file_but_never_into_the_Data_folder()
    {
        var file = Path.Combine(_fixture.Root, "records.csv");

        var (exit, text, error) = Run(["find", "--types", "Weapon", "--out", file, "--format", "csv", .. LoadOrder]);

        Assert.True(exit == 0, error);
        Assert.Contains("Wrote 1 rows", text);
        Assert.StartsWith("FormKey,Type,EditorID", File.ReadAllText(file));
        Assert.Equal(1, Run(["find", "--out", Path.Combine(_fixture.DataFolder, "x.csv"), .. LoadOrder]).Exit);
    }

    [Fact]
    public void An_MO2_profile_is_read_with_mo2_and_profile()
    {
        using var temp = new TempFolder();
        var mo2 = Mo2Fixture.Write(temp.Path);

        var (exit, text, error) = Run("load-order", "--mo2", mo2.Instance, "--profile", Mo2Fixture.Profile);

        Assert.True(exit == 0, error);
        Assert.Contains($"From MO2 profile {Mo2Fixture.Profile}", text);
        Assert.Equal(2, Run("load-order", "--mo2", mo2.Instance, "--data", _fixture.DataFolder).Exit);
    }

    [Fact]
    public void A_query_program_outside_the_policy_exits_with_1()
    {
        var query = Path.Combine(_fixture.Root, "query.cs");
        File.WriteAllText(query, SamplePrograms.LeveledListMerge.Replace("patched.Entries!.Add(", "System.IO.File.Delete(\"x\"); patched.Entries!.Add("));

        var (exit, text, _) = Run(["query", query, .. LoadOrder]);

        Assert.Equal(1, exit);
        Assert.Contains("SP0004", text);
    }

    [Fact]
    public void A_worker_memory_limit_that_is_not_MiB_in_range_exits_with_1()
    {
        var query = Path.Combine(_fixture.Root, "query.cs");
        File.WriteAllText(query, SamplePrograms.LeveledListMerge);

        var (exit, _, error) = Run(["query", query, "--worker-memory", "lots", .. LoadOrder]);

        Assert.Equal(1, exit);
        Assert.Contains("--worker-memory", error);
    }

    [Fact]
    public void Test_reports_what_the_in_process_run_reports()
    {
        using var snapshot = LoadOrderSnapshot.Open(_fixture.DataFolder, _fixture.LoadOrderFile);
        var expected = new AuthoringService(snapshot, InProcessWorkerLauncher.Real())
            .TestPatch(SamplePrograms.LeveledListMerge, new PatchScope(SamplePrograms.LeveledListMergeWritable), cancel: TestContext.Current.CancellationToken);

        var (exit, json, error) = RunJson(["test", _program, "--writable", "LeveledItem.Entries", .. LoadOrder]);

        Assert.True(exit == 0, error);
        Assert.True(json.GetProperty("accepted").GetBoolean());
        var change = Assert.Single(json.GetProperty("changes").EnumerateArray());
        Assert.Equal(expected.Changes[0].Change.FormKey, change.GetProperty("change").GetProperty("formKey").GetString());
        Assert.Equal(expected.Changes[0].Fields[0].After, change.GetProperty("fields")[0].GetProperty("after").GetString());
    }

    [Fact]
    public void A_rejected_test_exits_with_1_and_says_why()
    {
        var (exit, json, _) = RunJson(["test", _program, "--writable", "LeveledItem.EditorID", .. LoadOrder]);

        Assert.Equal(1, exit);
        Assert.Contains("changing LeveledItem.Entries is not allowed", json.GetProperty("error").GetString());
    }

    [Fact]
    public void Validate_reports_policy_diagnostics()
    {
        var bad = Path.Combine(_fixture.Root, "bad.cs");
        File.WriteAllText(bad, SamplePrograms.LeveledListMerge.Replace("patched.Entries!.Add(", "System.IO.File.Delete(\"x\"); patched.Entries!.Add("));

        var (exit, json, _) = RunJson("validate", bad);

        Assert.Equal(1, exit);
        Assert.Contains(json.GetProperty("diagnostics").EnumerateArray(), d => d.GetProperty("code").GetString() == "SP0004");
    }

    [Fact]
    public void Package_writes_a_patcher_repository()
    {
        var output = Path.Combine(_fixture.Root, "Out");

        var (exit, json, error) = RunJson("package", _program, "--name", "CacoMerge", "--out", output, "--writable", "LeveledItem.Entries",
            "--description", "Restores CACO entries.", "--requires", PluginFixture.Caco);

        Assert.True(exit == 0, error);
        Assert.True(json.GetProperty("success").GetBoolean());
        Assert.Contains(PluginFixture.Caco, File.ReadAllText(Path.Combine(output, "CacoMerge", "SynthesisMeta.json")));
    }

    public void Dispose() => _fixture.Dispose();
}
