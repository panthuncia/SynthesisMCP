using SafePatch.TestSupport;

namespace SafePatch.Compiler.Tests;

/// <summary>
/// Settings sources are compiled into the trusted patcher and instantiated by Synthesis's GUI, so
/// they may hold data only.
/// </summary>
public class SettingsPolicyTests
{
    private static string Settings(string members, string extra = "") => $$"""
        using System.Collections.Generic;
        using Mutagen.Bethesda.Plugins;
        using Mutagen.Bethesda.Skyrim;
        using Mutagen.Bethesda.Synthesis.Settings;

        namespace Naming;

        public enum Mode { Create, Skip }

        public class NamingSettings
        {
            {{members}}
        }
        {{extra}}
        """;

    private static CompileResult Compile(string settings) => PatchCompiler.Compile(SamplePrograms.NamedList, settings);

    [Fact]
    public void Accepts_the_sample_settings_and_reports_their_type()
    {
        var result = Compile(SamplePrograms.NamingSettings);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Equal("Naming.NamingSettings", result.SettingsType);
    }

    [Theory]
    [InlineData("""public Mode Mode { get; set; } = Mode.Create; public string EditorId { get; set; } = "x";""")]
    [InlineData("""public Mode Mode { get; init; } public string EditorId = ""; public double Chance = -0.5;""")]
    [InlineData("""
        public Mode Mode { get; set; } public string EditorId { get; set; } = "";
        public FormLink<INpcGetter> Npc { get; set; } = new FormLink<INpcGetter>(FormKey.Factory("000007:Skyrim.esm"));
        public Dictionary<Mode, List<int>> ByMode { get; set; } = new() { [Mode.Skip] = [1, 2] };
        public HashSet<ModKey> Mods { get; set; } = [ModKey.FromFileName("Skyrim.esm")];
        public Nested Inner { get; set; } = new();
        """, "public class Nested { [SynthesisOrder] public bool On { get; set; } = true; }")]
    public void Accepts_data_only_settings(string members, string extra = "")
    {
        var result = Compile(Settings(members, extra));
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
    }

    [Theory]
    [InlineData("public Mode Mode { get; set; } public string EditorId { get; set; } = \"\"; public static int Count;", "SP0303")]
    [InlineData("public Mode Mode { get; set; } public string EditorId { get { return \"\"; } }", "SP0303")]
    [InlineData("public Mode Mode { get; set; } public string EditorId { get; set; } = \"\"; public void Run() { }", "SP0303")]
    [InlineData("public Mode Mode { get; set; } public string EditorId { get; set; } = \"\"; public NamingSettings() { }", "SP0303")]
    [InlineData("public Mode Mode { get; set; } public string EditorId { get; set; } = System.IO.File.ReadAllText(\"x\");", "SP0305")]
    [InlineData("public Mode Mode { get; set; } public string EditorId { get; set; } = $\"{1}\";", "SP0305")]
    [InlineData("public Mode Mode { get; set; } public string EditorId { get; set; } = \"\"; public System.IO.FileInfo File { get; set; } = null!;", "SP0304")]
    [InlineData("public Mode Mode { get; set; } public string EditorId { get; set; } = \"\"; public object Anything { get; set; } = 1;", "SP0304")]
    [InlineData("public Mode Mode { get; set; } public string EditorId { get; set; } = \"\"; [System.Obsolete] public int Old { get; set; }", "SP0306")]
    [InlineData("public Mode Mode { get; set; } public string EditorId { get; set; } = \"\"; public System.Lazy<int> Later { get; set; } = new(() => 1);", "SP0304")]
    public void Rejects_code_in_settings(string members, string code)
    {
        var result = Compile(Settings(members));

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, d => d.Code == code);
    }

    [Theory]
    [InlineData("public static class Helper { public static int Run() => 1; }", "SP0302")]
    [InlineData("public struct Point { public int X; }", "SP0300")]
    [InlineData("public class Derived : System.Collections.Generic.List<int> { }", "SP0302")]
    [InlineData("internal class Hidden { }", "SP0302")]
    public void Rejects_declarations_other_than_data_classes_and_enums(string extra, string code)
    {
        var result = Compile(Settings("public Mode Mode { get; set; } public string EditorId { get; set; } = \"\";", extra));

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, d => d.Code == code);
    }

    [Fact]
    public void Requires_the_program_to_receive_the_settings()
    {
        var result = PatchCompiler.Compile(SamplePrograms.LeveledListMerge, SamplePrograms.NamingSettings);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, d => d.Code == "SP0310");
    }
}
