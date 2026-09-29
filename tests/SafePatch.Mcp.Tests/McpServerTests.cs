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
        "get_load_order", "get_record", "get_override_chain", "query_records", "find_references",
        "validate_patch", "test_patch", "package_synthesis_patcher",
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

    private async Task<(bool IsError, JsonElement Result, string Text)> Call(string tool, Dictionary<string, object?> arguments)
    {
        var result = await _client.CallToolAsync(tool, arguments, cancellationToken: TestContext.Current.CancellationToken);
        var text = string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text));
        var json = result.IsError == true ? default : JsonDocument.Parse(text).RootElement;
        return (result.IsError == true, json, text);
    }

    [Fact]
    public async Task Lists_exactly_the_authoring_tools()
    {
        var tools = await _client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(Tools.Order(), tools.Select(t => t.Name).Order());
    }

    [Fact]
    public async Task Every_tool_can_be_called()
    {
        var program = SamplePrograms.LeveledListMerge;
        var writable = SamplePrograms.LeveledListMergeWritable;

        var (_, plugins, _) = await Call("get_load_order", []);
        Assert.Equal(3, plugins.GetArrayLength());

        var (_, record, _) = await Call("get_record", new() { ["id"] = PluginFixture.ListEditorId });
        Assert.Equal(PluginFixture.Other, record.GetProperty("record").GetProperty("winningPlugin").GetString());

        var (_, chain, _) = await Call("get_override_chain", new() { ["id"] = PluginFixture.ListEditorId });
        Assert.Equal(3, chain.GetArrayLength());

        var (_, lists, _) = await Call("query_records", new() { ["type"] = "LeveledItem" });
        Assert.Equal(1, lists.GetArrayLength());

        var (_, references, _) = await Call("find_references", new() { ["id"] = "IronSword" });
        // The list, and the fixture's placed references whose base is the sword.
        Assert.Contains(references.EnumerateArray(), r => r.GetProperty("editorId").GetString() == PluginFixture.ListEditorId);

        var (_, validation, _) = await Call("validate_patch", new() { ["source"] = program });
        Assert.True(validation.GetProperty("success").GetBoolean());

        var (_, test, _) = await Call("test_patch", new() { ["source"] = program, ["writable"] = writable });
        Assert.True(test.GetProperty("accepted").GetBoolean(), test.ToString());
        Assert.Equal("Entries", test.GetProperty("changes")[0].GetProperty("fields")[0].GetProperty("field").GetString());

        var output = Path.Combine(_fixture.Root, "Packaged");
        var (_, package, _) = await Call("package_synthesis_patcher", new() { ["name"] = "CacoMerge", ["source"] = program, ["outputDirectory"] = output, ["writable"] = writable });
        Assert.True(package.GetProperty("success").GetBoolean());
        Assert.True(File.Exists(Path.Combine(output, "REVIEW.md")));
    }

    [Fact]
    public async Task Errors_come_back_as_tool_errors_with_the_reason()
    {
        var (isError, _, text) = await Call("get_record", new() { ["id"] = "NoSuchRecord" });

        Assert.True(isError);
        Assert.Contains("No record NoSuchRecord", text);
    }

    [Fact]
    public async Task A_patcher_cannot_be_packaged_into_the_load_orders_data_folder()
    {
        var (isError, _, text) = await Call("package_synthesis_patcher", new()
        {
            ["name"] = "Sneaky", ["source"] = SamplePrograms.LeveledListMerge,
            ["outputDirectory"] = Path.Combine(_fixture.DataFolder, "Sneaky"), ["writable"] = SamplePrograms.LeveledListMergeWritable,
        });

        Assert.True(isError);
        Assert.Contains("Data folder", text);
        Assert.False(Directory.Exists(Path.Combine(_fixture.DataFolder, "Sneaky")));
    }

    public async ValueTask DisposeAsync()
    {
        await _client.DisposeAsync();
        _fixture.Dispose();
    }
}
