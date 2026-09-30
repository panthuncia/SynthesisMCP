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

    /// <summary>Prints each winning leveled list, and tries to add a record, which must be discarded.</summary>
    private const string ListQuery = """
        using System;
        using Mutagen.Bethesda;
        using Mutagen.Bethesda.Skyrim;
        using Mutagen.Bethesda.Synthesis;
        public static class ListQuery
        {
            public static void RunPatch(IPatcherState<ISkyrimMod, ISkyrimModGetter> state)
            {
                foreach (var list in state.LoadOrder.PriorityOrder.LeveledItem().WinningOverrides())
                    Console.WriteLine($"{list.EditorID}: {list.Entries?.Count ?? 0} entries");
                for (var i = 0; i < 3; i++) state.PatchMod.LeveledItems.AddNew($"QueryTried{i}");
            }
        }
        """;

    [Fact]
    public void A_query_that_changes_the_patch_is_not_rejected_because_its_output_is_never_read()
    {
        var result = _service.RunQuery(ListQuery, cancel: TestContext.Current.CancellationToken);

        Assert.False(result.Failed, string.Join("\n", result.Rows.Select(r => r[0])));
        Assert.Equal(_snapshot.Generation, result.Generation);
    }

    [Fact]
    public void A_query_outside_the_sandbox_policy_is_rejected_before_it_runs()
    {
        var result = _service.RunQuery(ListQuery.Replace("for (var i = 0;", "System.IO.File.Delete(\"x\"); for (var i = 0;"), cancel: TestContext.Current.CancellationToken);

        Assert.True(result.Failed);
        Assert.Contains(result.Rows, r => r[0]!.StartsWith("SP0004", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Category", "Sandbox")]
    public void A_sandboxed_query_returns_what_it_printed_as_lines()
    {
        var result = new AuthoringService(_snapshot).RunQuery(ListQuery, cancel: TestContext.Current.CancellationToken);

        Assert.False(result.Failed, string.Join("\n", result.Notes ?? []));
        Assert.Equal([$"{PluginFixture.ListEditorId}: 3 entries"], result.Rows.Select(r => r[0]));
    }

    [Fact]
    [Trait("Category", "Sandbox")]
    public void A_query_that_throws_reports_the_failure_and_what_it_printed_first()
    {
        var failing = ListQuery.Replace("for (var i = 0;", "throw new InvalidOperationException(\"stop\"); for (var i = 0;");

        var result = new AuthoringService(_snapshot).RunQuery(failing, cancel: TestContext.Current.CancellationToken);

        Assert.True(result.Failed);
        Assert.Contains(result.Notes!, n => n.StartsWith("The query failed", StringComparison.Ordinal));
        Assert.Contains([$"{PluginFixture.ListEditorId}: 3 entries"], result.Rows);
    }

    public void Dispose()
    {
        _snapshot.Dispose();
        _fixture.Dispose();
    }
}
