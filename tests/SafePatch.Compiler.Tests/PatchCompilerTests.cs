using SafePatch.Host;
using SafePatch.TestSupport;

namespace SafePatch.Compiler.Tests;

public class PatchCompilerTests
{
    private static string Program(string body, string members = "") => $$"""
        using System;
        using System.Collections.Generic;
        using System.Linq;
        using Mutagen.Bethesda;
        using Mutagen.Bethesda.Plugins;
        using Mutagen.Bethesda.Skyrim;
        using Mutagen.Bethesda.Synthesis;

        public static class Patcher
        {
            public static void RunPatch(IPatcherState<ISkyrimMod, ISkyrimModGetter> state)
            {
                {{body}}
            }
            {{members}}
        }
        """;

    [Fact]
    public void Compiles_the_native_merge_program_deterministically()
    {
        var first = PatchCompiler.Compile(SamplePrograms.LeveledListMerge);
        var second = PatchCompiler.Compile(SamplePrograms.LeveledListMerge);

        Assert.True(first.Success, string.Join(Environment.NewLine, first.Diagnostics));
        Assert.Equal(first.Sha256, second.Sha256);
    }

    [Theory]
    // Typical Synthesis patcher code: winning overrides, link cache, overrides, new records, console logging.
    [InlineData("""
        foreach (var npc in state.LoadOrder.PriorityOrder.Npc().WinningOverrides())
        {
            if (npc.Configuration.Flags.HasFlag(NpcConfiguration.Flag.Essential)) continue;
            var copy = state.PatchMod.Npcs.GetOrAddAsOverride(npc);
            copy.Configuration.Flags |= NpcConfiguration.Flag.Protected;
        }
        """)]
    [InlineData("""
        var armor = state.LinkCache.Resolve<IArmorGetter>(FormKey.Factory("012E49:Skyrim.esm"));
        var list = state.PatchMod.LeveledItems.AddNew("LItemNew");
        list.Entries = [new LeveledItemEntry { Data = new LeveledItemEntryData { Level = 1, Count = 1, Reference = armor.ToLink() } }];
        Console.WriteLine($"Added {list.EditorID} with {list.Entries.Count} entries");
        """)]
    [InlineData("""
        var byMod = state.LoadOrder.PriorityOrder.WinningOverrides<IWeaponGetter>()
            .GroupBy(w => w.FormKey.ModKey).ToDictionary(g => g.Key, g => g.Count());
        var seen = new HashSet<FormKey>();
        Visit(FormKey.Null, seen);
        """, "static void Visit(FormKey key, HashSet<FormKey> seen) { if (!seen.Add(key)) return; }")]
    // Reading a stream the asset provider hands out.
    [InlineData("""
        if (state.AssetProvider.TryGetStream(new Mutagen.Bethesda.Assets.DataRelativePath("meshes/x.nif"), out var stream))
        {
            using (stream)
            {
                var text = new System.IO.StreamReader(stream).ReadToEnd();
                stream.Position = 0;
                var header = new System.IO.BinaryReader(stream).ReadUInt32();
                Console.WriteLine(text.Length + header);
            }
        }
        """)]
    // Collection expressions: the compiler builds lists through CollectionsMarshal.
    [InlineData("""
        List<int> levels = [1, 2, 3];
        List<int> more = [.. levels, 4];
        int[] array = [5, 6];
        HashSet<string> names = ["a", "b"];
        IReadOnlyList<FormKey> keys = [FormKey.Null];
        Console.WriteLine(more.Count + array.Length + names.Count + keys.Count);
        """)]
    public void Allows_ordinary_patcher_code(string body, string members = "")
    {
        var result = PatchCompiler.Compile(Program(body, members));
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
    }

    [Fact]
    public void Allows_an_async_patch_function()
    {
        var result = PatchCompiler.Compile("""
            using System.Threading.Tasks;
            using Mutagen.Bethesda.Skyrim;
            using Mutagen.Bethesda.Synthesis;
            public static class P { public static async Task RunPatch(IPatcherState<ISkyrimMod, ISkyrimModGetter> state) => await Task.Yield(); }
            """);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
    }

    [Theory]
    [InlineData("SP0004", "System.IO.File.ReadAllText(\"C:/secret.txt\");")]
    [InlineData("SP0004", "var v = Environment.GetEnvironmentVariable(\"PATH\");")]
    [InlineData("SP0004", "var r = new Random().Next();")]
    [InlineData("SP0004", "var now = DateTime.Now;")]
    [InlineData("SP0004", "var o = System.Activator.CreateInstance<object>();")]
    [InlineData("SP0004", "new System.Threading.Thread(() => { }).Start();")]
    [InlineData("SP0004", "Func<int> f = () => 1; var m = f.Method;")]
    [InlineData("SP0004", "var p = System.Diagnostics.Process.Start(\"cmd.exe\");")]
    [InlineData("SP0004", "var c = new System.Net.Sockets.TcpClient();")]
    [InlineData("SP0004", "var d = AppDomain.CurrentDomain;")]
    [InlineData("SP0004", "System.IO.Abstractions.IFileSystem fs = new System.IO.Abstractions.FileSystem();")]
    [InlineData("SP0005", "var t = state.GetType();")]
    [InlineData("SP0005", "var r = new System.IO.StreamReader(\"C:/secret.txt\");")]
    [InlineData("SP0004", "var f = new System.IO.FileStream(\"C:/secret.txt\", System.IO.FileMode.Open);")]
    [InlineData("SP0004", "var w = new System.IO.StreamWriter(new System.IO.MemoryStream());")]
    [InlineData("SP0001", "var t = typeof(object);")]
    [InlineData("SP0001", "Span<int> s = stackalloc int[4];")]
    public void Rejects_forbidden_apis(string expectedCode, string body)
    {
        var result = PatchCompiler.Compile(Program(body));
        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, d => d.Code == expectedCode);
    }

    [Theory]
    [InlineData("SP0001", "[System.Runtime.CompilerServices.ModuleInitializer] internal static void Init() { }")]
    [InlineData("SP0002", "private sealed class Evil : System.Attribute { }")]
    [InlineData("SP0002", "private sealed class Watcher : System.IDisposable { public void Dispose() { } }")]
    public void Rejects_forbidden_declarations(string expectedCode, string members)
    {
        var result = PatchCompiler.Compile(Program("", members));
        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, d => d.Code == expectedCode);
    }

    [Theory]
    [InlineData("public static class P { }")]
    [InlineData("public static class P { public static void RunPatch(IPatcherState<ISkyrimMod, ISkyrimModGetter> s) { } } public static class Q { public static void RunPatch(IPatcherState<ISkyrimMod, ISkyrimModGetter> s) { } }")]
    [InlineData("public static class P { public static int RunPatch(IPatcherState<ISkyrimMod, ISkyrimModGetter> s) => 0; }")]
    [InlineData("static class P { public static void RunPatch(IPatcherState<ISkyrimMod, ISkyrimModGetter> s) { } }")]
    public void Requires_exactly_one_native_patch_function(string source)
    {
        var result = PatchCompiler.Compile("using Mutagen.Bethesda.Skyrim; using Mutagen.Bethesda.Synthesis;\n" + source);
        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, d => d.Code == "SP0200");
    }

    [Theory]
    [InlineData("unsafe { int x = 1; int* p = &x; }", "")]
    [InlineData("dynamic d = 1; d.Foo();", "")]
    [InlineData("", "[System.Runtime.InteropServices.DllImport(\"kernel32\")] static extern int GetTickCount();")]
    [InlineData("#r \"System.IO.dll\"", "")]
    [InlineData("this is not C#", "")]
    public void Rejects_programs_that_do_not_compile_or_break_policy(string body, string members)
    {
        var result = PatchCompiler.Compile(Program(body, members));
        Assert.False(result.Success);
        Assert.NotEmpty(result.Diagnostics);
    }

    [Fact]
    public void Compiled_program_runs_end_to_end()
    {
        var compiled = PatchCompiler.Compile(SamplePrograms.LeveledListMerge);
        using var run = new HostRun();
        var report = new PatchSession(InProcessWorkerLauncher.Real(), run.Committer())
            .Run(TestPrograms.Package(compiled.Assembly!), run.Inputs(), TestContext.Current.CancellationToken);

        Assert.Single(report.Changes);
    }
}
