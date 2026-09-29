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

    private (int Exit, JsonElement Json, string Error) Run(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = CliApp.Run(args, output, error, InProcessWorkerLauncher.Real());
        var text = output.ToString();
        return (exit, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement, error.ToString());
    }

    private string[] LoadOrder => ["--data", _fixture.DataFolder, "--plugins", _fixture.LoadOrderFile];

    [Theory]
    [InlineData]
    [InlineData("frobnicate", "--data", "x", "--plugins", "y")]
    [InlineData("record")]
    [InlineData("query", "LeveledItem", "--limit")]
    public void Usage_errors_exit_with_2_and_print_the_usage(params string[] args)
    {
        var (exit, _, error) = Run(args);

        Assert.Equal(2, exit);
        Assert.Contains("safepatch <command>", error);
    }

    [Fact]
    public void Load_order_and_records_are_printed_as_json()
    {
        var (exit, plugins, _) = Run(["load-order", .. LoadOrder]);
        Assert.Equal(0, exit);
        Assert.Equal(3, plugins.GetArrayLength());

        var (_, chain, _) = Run(["chain", PluginFixture.ListEditorId, .. LoadOrder]);
        Assert.Equal(PluginFixture.Other, chain[2].GetProperty("plugin").GetString());
    }

    [Fact]
    public void Test_reports_what_the_in_process_run_reports()
    {
        using var snapshot = LoadOrderSnapshot.Open(_fixture.DataFolder, _fixture.LoadOrderFile);
        var expected = new AuthoringService(snapshot, InProcessWorkerLauncher.Real())
            .TestPatch(SamplePrograms.LeveledListMerge, new PatchScope(SamplePrograms.LeveledListMergeWritable), cancel: TestContext.Current.CancellationToken);

        var (exit, json, error) = Run(["test", _program, "--writable", "LeveledItem.Entries", .. LoadOrder]);

        Assert.True(exit == 0, error);
        Assert.True(json.GetProperty("accepted").GetBoolean());
        var change = Assert.Single(json.GetProperty("changes").EnumerateArray());
        Assert.Equal(expected.Changes[0].Change.FormKey, change.GetProperty("change").GetProperty("formKey").GetString());
        Assert.Equal(expected.Changes[0].Fields[0].After, change.GetProperty("fields")[0].GetProperty("after").GetString());
    }

    [Fact]
    public void A_rejected_test_exits_with_1_and_says_why()
    {
        var (exit, json, _) = Run(["test", _program, "--writable", "LeveledItem.EditorID", .. LoadOrder]);

        Assert.Equal(1, exit);
        Assert.Contains("changing LeveledItem.Entries is not allowed", json.GetProperty("error").GetString());
    }

    [Fact]
    public void Validate_reports_policy_diagnostics()
    {
        var bad = Path.Combine(_fixture.Root, "bad.cs");
        File.WriteAllText(bad, SamplePrograms.LeveledListMerge.Replace("patched.Entries!.Add(", "System.IO.File.Delete(\"x\"); patched.Entries!.Add("));

        var (exit, json, _) = Run("validate", bad);

        Assert.Equal(1, exit);
        Assert.Contains(json.GetProperty("diagnostics").EnumerateArray(), d => d.GetProperty("code").GetString() == "SP0004");
    }

    [Fact]
    public void Package_writes_a_patcher_repository()
    {
        var output = Path.Combine(_fixture.Root, "Out");

        var (exit, json, error) = Run("package", _program, "--name", "CacoMerge", "--out", output, "--writable", "LeveledItem.Entries",
            "--description", "Restores CACO entries.", "--requires", PluginFixture.Caco);

        Assert.True(exit == 0, error);
        Assert.True(json.GetProperty("success").GetBoolean());
        Assert.Contains(PluginFixture.Caco, File.ReadAllText(Path.Combine(output, "CacoMerge", "SynthesisMeta.json")));
    }

    public void Dispose() => _fixture.Dispose();
}
