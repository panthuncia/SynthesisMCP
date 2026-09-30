using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using SafePatch.TestSupport;

namespace SafePatch.Mcp.Tests;

/// <summary>
/// Starts the real <c>safepatch-mcp</c> server over stdio on the conflict fixture and calls every tool
/// through the official SDK's client, as an MCP host would. Test runs use the server's real sandbox.
/// </summary>
[Trait("Category", "Sandbox")]
public sealed class McpServerTests : IAsyncLifetime
{
    private static readonly string[] Tools =
    [
        "load_order", "find_records", "get_record", "compare_record", "find_conflicts", "references", "find_asset", "describe_type",
        "run_query", "read_results", "export_results", "reload", "validate_patch", "test_patch", "package_synthesis_patcher",
    ];

    private readonly HostRun _fixture = new();
    private McpClient _client = null!;

    public async ValueTask InitializeAsync()
    {
        var server = Path.Combine(AppContext.BaseDirectory, "safepatch-mcp.dll");
        _client = await McpClient.CreateAsync(
            new StdioClientTransport(new StdioClientTransportOptions
            {
                Command = "dotnet",
                Arguments = [server, "--data", _fixture.DataFolder, "--plugins", _fixture.LoadOrderFile],
                Name = "safepatch",
            }),
            cancellationToken: TestContext.Current.CancellationToken);
    }

    private async Task<(bool IsError, string Text)> Call(string tool, Dictionary<string, object?> arguments)
    {
        var result = await _client.CallToolAsync(tool, arguments, cancellationToken: TestContext.Current.CancellationToken);
        return (result.IsError == true, string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text)));
    }

    private async Task<string> Text(string tool, Dictionary<string, object?> arguments)
    {
        var (isError, text) = await Call(tool, arguments);
        Assert.False(isError, text);
        return text;
    }

    private async Task<JsonElement> Json(string tool, Dictionary<string, object?> arguments) => JsonDocument.Parse(await Text(tool, arguments)).RootElement;

    [Fact]
    public async Task Lists_exactly_the_authoring_tools_and_explains_how_to_use_them()
    {
        var tools = await _client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(Tools.Order(), tools.Select(t => t.Name).Order());
        Assert.Contains("find_conflicts", _client.ServerInstructions);
        Assert.All(tools.Where(t => t.Name is not ("export_results" or "package_synthesis_patcher")), t => Assert.True(t.ProtocolTool.Annotations?.ReadOnlyHint, t.Name));
    }

    [Fact]
    public async Task Every_load_order_tool_answers_as_budgeted_text()
    {
        Assert.Contains($"| {PluginFixture.Other} | PLUGIN", await Text("load_order", []));
        Assert.Contains("1 new", await Text("load_order", new() { ["plugin"] = PluginFixture.Caco }));
        Assert.Contains(PluginFixture.ListEditorId, await Text("find_records", new() { ["types"] = new[] { "LeveledItem" }, ["minVersions"] = 3 }));
        Assert.Contains("Entries (3):", await Text("get_record", new() { ["id"] = PluginFixture.ListEditorId }));
        Assert.Contains("CONFLICT", await Text("compare_record", new() { ["id"] = PluginFixture.ListEditorId, ["fields"] = new[] { "Entries" } }));
        Assert.Contains($"LeveledItem.Entries: {PluginFixture.Caco} → {PluginFixture.Other}", await Text("find_conflicts", new() { ["types"] = new[] { "LeveledItem" } }));
        Assert.Contains(PluginFixture.ListEditorId, await Text("references", new() { ["id"] = "IronSword", ["direction"] = "in", ["types"] = new[] { "LeveledItem" } }));
        Assert.Contains("No loose file", await Text("find_asset", new() { ["path"] = @"meshes\none.nif" }));
        Assert.Contains("Entries[].Data.Reference", await Text("describe_type", new() { ["type"] = "LeveledItem" }));
    }

    [Fact]
    public async Task Large_results_page_through_handles_and_export_to_files()
    {
        var first = await Text("describe_type", new() { ["type"] = "Npc", ["budget"] = 1000 });
        var handle = first.Split(", handle ")[1].Split('\n')[0].Trim();
        Assert.True(first.Length <= 1000, first);

        Assert.Contains("Shown rows 21–", await Text("read_results", new() { ["handle"] = handle, ["offset"] = 20 }));
        var file = Path.Combine(_fixture.Root, "npc.jsonl");
        Assert.Contains("Wrote", await Text("export_results", new() { ["handle"] = handle, ["path"] = file }));
        Assert.True(File.ReadAllLines(file).Length > 20);

        var resource = await _client.ReadResourceAsync($"safepatch://results/{handle}", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("Npc fields", ((TextResourceContents)resource.Contents[0]).Text);

        Assert.StartsWith("Reloaded 3 plugins", await Text("reload", []));
        var (isError, text) = await Call("read_results", new() { ["handle"] = handle });
        Assert.True(isError);
        Assert.Contains("reloaded", text);
    }

    [Fact]
    public async Task A_record_can_be_read_as_a_resource()
    {
        var templates = await _client.ListResourceTemplatesAsync(cancellationToken: TestContext.Current.CancellationToken);
        var record = await _client.ReadResourceAsync($"safepatch://record/{PluginFixture.ListEditorId}", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains(templates, t => t.UriTemplate == "safepatch://record/{id}");
        Assert.Contains("Entries (3):", ((TextResourceContents)record.Contents[0]).Text);
    }

    [Fact]
    public async Task Queries_and_programs_run_in_the_sandbox()
    {
        var program = SamplePrograms.LeveledListMerge;
        var writable = SamplePrograms.LeveledListMergeWritable;
        const string query = """
            using System;
            using Mutagen.Bethesda;
            using Mutagen.Bethesda.Skyrim;
            using Mutagen.Bethesda.Synthesis;
            public static class Count
            {
                public static void RunPatch(IPatcherState<ISkyrimMod, ISkyrimModGetter> state)
                {
                    foreach (var list in state.LoadOrder.PriorityOrder.LeveledItem().WinningOverrides())
                        Console.WriteLine($"{list.EditorID} has {list.Entries?.Count ?? 0}");
                }
            }
            """;

        Assert.Contains($"{PluginFixture.ListEditorId} has 3", await Text("run_query", new() { ["source"] = query }));
        Assert.True((await Json("validate_patch", new() { ["source"] = program })).GetProperty("success").GetBoolean());

        var test = await Json("test_patch", new() { ["source"] = program, ["writable"] = writable });
        Assert.True(test.GetProperty("accepted").GetBoolean(), test.ToString());
        Assert.Equal("Entries", test.GetProperty("changes")[0].GetProperty("fields")[0].GetProperty("field").GetString());

        var output = Path.Combine(_fixture.Root, "Packaged");
        var package = await Json("package_synthesis_patcher", new() { ["name"] = "CacoMerge", ["source"] = program, ["outputDirectory"] = output, ["writable"] = writable });
        Assert.True(package.GetProperty("success").GetBoolean());
        Assert.True(File.Exists(Path.Combine(output, "REVIEW.md")));
    }

    [Fact]
    public async Task Errors_come_back_as_tool_errors_with_the_reason()
    {
        var (isError, text) = await Call("get_record", new() { ["id"] = "NoSuchRecord" });
        Assert.True(isError);
        Assert.Contains("No record NoSuchRecord", text);

        (isError, text) = await Call("find_conflicts", []);
        Assert.True(isError);
        Assert.Contains("needs types or plugin", text);
    }

    [Fact]
    public async Task Nothing_is_written_into_the_load_orders_folders()
    {
        var (isError, text) = await Call("package_synthesis_patcher", new()
        {
            ["name"] = "Sneaky", ["source"] = SamplePrograms.LeveledListMerge,
            ["outputDirectory"] = Path.Combine(_fixture.DataFolder, "Sneaky"), ["writable"] = SamplePrograms.LeveledListMergeWritable,
        });
        Assert.True(isError);
        Assert.Contains("inside the load order's folders", text);
        Assert.False(Directory.Exists(Path.Combine(_fixture.DataFolder, "Sneaky")));

        await Text("load_order", []);
        (isError, _) = await Call("export_results", new() { ["handle"] = "r1", ["path"] = Path.Combine(_fixture.DataFolder, "x.jsonl") });
        Assert.True(isError);
        Assert.False(File.Exists(Path.Combine(_fixture.DataFolder, "x.jsonl")));
    }

    public async ValueTask DisposeAsync()
    {
        await _client.DisposeAsync();
        _fixture.Dispose();
    }
}
