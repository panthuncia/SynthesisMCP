using SafePatch.Host;
using SafePatch.TestSupport;

namespace SafePatch.Authoring.Tests;

/// <summary>
/// The authoring operations over the standard conflict fixture: Skyrim.esm's list, CACO's override
/// adding a potion, and a later override that drops it. Test runs use the in-process worker; the
/// Sandbox-category test uses the real one.
/// </summary>
public sealed class AuthoringServiceTests : IDisposable
{
    private readonly HostRun _fixture = new();
    private readonly LoadOrderSnapshot _snapshot;
    private readonly AuthoringService _service;

    public AuthoringServiceTests()
    {
        _snapshot = LoadOrderSnapshot.Open(_fixture.DataFolder, _fixture.LoadOrderFile);
        _service = new AuthoringService(_snapshot, InProcessWorkerLauncher.Real());
    }

    [Fact]
    public void Lists_the_load_order_with_implicit_masters_first()
    {
        var plugins = _service.GetLoadOrder();

        Assert.Equal([PluginFixture.BaseMaster, PluginFixture.Caco, PluginFixture.Other], plugins.Select(p => p.ModKey));
        Assert.Equal([PluginFixture.BaseMaster], plugins[1].Masters);
    }

    [Fact]
    public void Gets_the_winning_record_by_EditorID_or_FormKey()
    {
        var byEditorId = _service.GetRecord(PluginFixture.ListEditorId);
        var byFormKey = _service.GetRecord(_fixture.Plugins.List.ToString());

        Assert.Equal(byEditorId, byFormKey);
        Assert.Equal(("LeveledItem", PluginFixture.Other), (byEditorId.Record.Type, byEditorId.Record.WinningPlugin));
        Assert.Contains(PluginFixture.ListEditorId, byEditorId.Text);
        Assert.Throws<SafePatchException>(() => _service.GetRecord("NoSuchRecord"));
    }

    [Fact]
    public void Shows_the_override_chain_and_what_each_plugin_changed()
    {
        var chain = _service.GetOverrideChain(PluginFixture.ListEditorId);

        Assert.Equal([PluginFixture.BaseMaster, PluginFixture.Caco, PluginFixture.Other], chain.Select(v => v.Plugin));
        Assert.Empty(chain[0].ChangedFields);
        Assert.Equal(["Entries"], chain[1].ChangedFields);
        Assert.Equal(["Entries"], chain[2].ChangedFields);
    }

    [Fact]
    public void Queries_winning_records_by_type_and_EditorID()
    {
        Assert.Equal([PluginFixture.ListEditorId], _service.QueryRecords("LeveledItem").Select(r => r.EditorId));
        Assert.Equal(["CACO_Potion"], _service.QueryRecords("Ingestible", "caco").Select(r => r.EditorId));
        Assert.Throws<SafePatchException>(() => _service.QueryRecords("NotARecordType"));
    }

    [Fact]
    public void Finds_winning_records_that_link_to_a_record()
    {
        // The winning list still has the sword, but the later override dropped CACO's potion.
        Assert.Contains(_service.FindReferences("IronSword"), r => r.EditorId == PluginFixture.ListEditorId);
        Assert.DoesNotContain(_service.FindReferences("CACO_Potion"), r => r.EditorId == PluginFixture.ListEditorId);
    }

    [Fact]
    public void Validates_programs_under_the_sandbox_policy()
    {
        Assert.True(AuthoringService.Validate(SamplePrograms.LeveledListMerge).Success);
        var result = AuthoringService.Validate(SamplePrograms.LeveledListMerge.Replace("patched.Entries!.Add(", "System.IO.File.Delete(\"x\"); patched.Entries!.Add("));
        Assert.Contains(result.Diagnostics, d => d.Code == "SP0004");
    }

    [Fact]
    public void A_test_run_reports_each_changed_field_before_and_after()
    {
        var result = _service.TestPatch(SamplePrograms.LeveledListMerge, new PatchScope(SamplePrograms.LeveledListMergeWritable), gameIni: "", cancel: TestContext.Current.CancellationToken);

        Assert.True(result.Accepted, result.Error);
        var change = Assert.Single(result.Changes);
        var field = Assert.Single(change.Fields);
        Assert.Equal("Entries", field.Field);
        Assert.DoesNotContain(_fixture.Plugins.CacoPotion.ToString(), field.Before);
        Assert.Contains(_fixture.Plugins.CacoPotion.ToString(), field.After);
    }

    [Fact]
    public void A_test_run_outside_the_scope_is_rejected_with_the_reason()
    {
        var result = _service.TestPatch(SamplePrograms.LeveledListMerge, new PatchScope(["LeveledItem.EditorID"]), gameIni: "", cancel: TestContext.Current.CancellationToken);

        Assert.False(result.Accepted);
        Assert.Contains("changing LeveledItem.Entries is not allowed", result.Error);
    }

    [Fact]
    public void A_program_that_does_not_compile_reports_diagnostics_without_running()
    {
        var result = _service.TestPatch("this is not C#", new PatchScope([]), gameIni: "", cancel: TestContext.Current.CancellationToken);

        Assert.False(result.Accepted);
        Assert.NotEmpty(result.Diagnostics);
    }

    [Fact]
    public void A_test_run_with_settings_uses_the_given_values()
    {
        var result = _service.TestPatch(SamplePrograms.NamedList, new PatchScope([], Creatable: ["LeveledItem"]),
            new SettingsInput(SamplePrograms.NamingSettings, """{ "EditorId": "LItemFromAuthoring" }"""), gameIni: "", cancel: TestContext.Current.CancellationToken);

        Assert.True(result.Accepted, result.Error);
        Assert.Equal("LItemFromAuthoring", Assert.Single(result.Changes).Change.EditorId);
    }

    [Fact]
    public void Packages_a_patcher_against_this_runtime()
    {
        var output = Path.Combine(_fixture.Root, "Packaged");

        var result = AuthoringService.Package(new PackageRequest("CacoMerge", SamplePrograms.LeveledListMerge, new PatchScope(SamplePrograms.LeveledListMergeWritable), output,
            Description: "Restores CACO entries."));

        Assert.True(result.Success);
        Assert.Contains($"Version=\"[{AuthoringService.RuntimeVersion}]\"", File.ReadAllText(Path.Combine(output, "CacoMerge", "CacoMerge.csproj")));
        Assert.True(File.Exists(Path.Combine(output, "CacoMerge", "SynthesisMeta.json")));
    }

    [Fact]
    [Trait("Category", "Sandbox")]
    public void A_test_run_uses_the_real_sandbox_by_default()
    {
        var result = new AuthoringService(_snapshot).TestPatch(SamplePrograms.LeveledListMerge, new PatchScope(SamplePrograms.LeveledListMergeWritable), gameIni: "", cancel: TestContext.Current.CancellationToken);

        Assert.True(result.Accepted, result.Error);
        Assert.Single(result.Changes);
        Assert.Contains("Restored 1 entries", result.Log);
    }

    public void Dispose()
    {
        _snapshot.Dispose();
        _fixture.Dispose();
    }
}
