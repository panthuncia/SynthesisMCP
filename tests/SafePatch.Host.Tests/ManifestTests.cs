using SafePatch.TestSupport;

namespace SafePatch.Host.Tests;

public class ManifestTests
{
    private const string Hash = "0000000000000000000000000000000000000000000000000000000000000000";

    private static string Json(string writable = """["LeveledItem.Entries"]""", string creatable = "[]", int schema = Manifest.CurrentSchemaVersion, string hash = Hash) =>
        $$"""{"schemaVersion":{{schema}},"name":"P","programSha256":"{{hash}}","writable":{{writable}},"creatable":{{creatable}},"maxRecords":10}""";

    [Fact]
    public void Parses_a_valid_manifest()
    {
        var manifest = Manifest.Parse(Json());
        Assert.Equal("P", manifest.Name);
        Assert.Equal(["LeveledItem.Entries"], manifest.Writable);
    }

    [Theory]
    [InlineData(1, Hash)]
    [InlineData(Manifest.CurrentSchemaVersion, "abc")]
    [InlineData(Manifest.CurrentSchemaVersion, "000000000000000000000000000000000000000000000000000000000000000G")]
    public void Rejects_bad_schema_or_hash(int schema, string hash) =>
        Assert.Throws<SafePatchException>(() => Manifest.Parse(Json(schema: schema, hash: hash)));

    [Fact]
    public void A_schema_mismatch_names_both_versions()
    {
        var e = Assert.Throws<SafePatchException>(() => Manifest.Parse(Json(schema: Manifest.CurrentSchemaVersion + 1)));

        Assert.Contains($"schema {Manifest.CurrentSchemaVersion + 1} is not supported", e.Message);
        Assert.Contains($"reads schema {Manifest.CurrentSchemaVersion}", e.Message);
    }

    [Theory]
    [InlineData("""["LeveledItem"]""")]
    [InlineData("""["LeveledItem.Entries.Data"]""")]
    [InlineData("""["Leveled Item.*"]""")]
    [InlineData("""["*.Entries"]""")]
    public void Rejects_malformed_field_patterns(string writable) =>
        Assert.Throws<SafePatchException>(() => Manifest.Parse(Json(writable: writable)));

    [Fact]
    public void Parses_settings()
    {
        var manifest = Manifest.Parse(Json().Replace("}", ""","settings":{"type":"Naming.NamingSettings","path":"settings.json"}}"""));
        Assert.Equal(new ManifestSettings("Naming.NamingSettings", "settings.json"), manifest.Settings);
    }

    [Theory]
    [InlineData("Naming.NamingSettings", "../settings.json")]
    [InlineData("Naming.NamingSettings", @"C:\settings.json")]
    [InlineData("Naming.NamingSettings", "settings.txt")]
    [InlineData("Naming.NamingSettings", ".json")]
    [InlineData("Naming.Naming Settings", "settings.json")]
    [InlineData("Naming.<Settings>", "settings.json")]
    public void Rejects_settings_that_are_not_a_type_and_a_plain_file_name(string type, string path) =>
        Assert.Throws<SafePatchException>(() => Manifest.Parse(Json().Replace("}", $$$""","settings":{"type":"{{{type}}}","path":"{{{path}}}"}}""")));

    [Fact]
    public void Rejects_unknown_members() =>
        Assert.Throws<SafePatchException>(() => Manifest.Parse(Json().Replace("\"maxRecords\"", "\"postBuild\":\"cmd\",\"maxRecords\"")));

    [Theory]
    [InlineData("LeveledItem", "Entries", true)]
    [InlineData("LeveledItem", "EditorID", false)]
    [InlineData("Npc", "Entries", false)]
    public void Field_patterns_match_type_and_field(string type, string field, bool expected)
    {
        var policy = PatchPolicy.PublisherDefault.NarrowTo(Manifest.Parse(Json()));
        Assert.Equal(expected, policy.CanWrite(type, field));
    }

    [Fact]
    public void Type_wildcards_cover_every_field_of_the_type()
    {
        var policy = PatchPolicy.PublisherDefault.NarrowTo(Manifest.Parse(Json(writable: """["LeveledItem.*"]""")));
        Assert.True(policy.CanWrite("LeveledItem", "EditorID"));
        Assert.False(policy.CanWrite("Npc", "EditorID"));
    }

    [Fact]
    public void Policy_can_be_narrowed_but_not_widened()
    {
        var publisher = PatchPolicy.PublisherDefault with { Writable = [FieldPattern.Parse("LeveledItem.*")], Creatable = ["LeveledItem"] };

        var narrowed = publisher.NarrowTo(Manifest.Parse(Json()));
        Assert.Equal(10, narrowed.MaxRecords);
        Assert.False(narrowed.CanCreate("LeveledItem"));

        Assert.Throws<SafePatchException>(() => publisher.NarrowTo(Manifest.Parse(Json(writable: """["Npc.Name"]"""))));
        Assert.Throws<SafePatchException>(() => publisher.NarrowTo(Manifest.Parse(Json(writable: """["*"]"""))));
        Assert.Throws<SafePatchException>(() => publisher.NarrowTo(Manifest.Parse(Json(creatable: """["Npc"]"""))));
    }

    [Fact]
    public void Removal_is_granted_only_for_the_types_the_manifest_lists()
    {
        static Manifest WithRemovable(string removable) =>
            Manifest.Parse(Json().Replace("\"maxRecords\"", $"\"removable\":{removable},\"maxRecords\"", StringComparison.Ordinal));

        Assert.False(PatchPolicy.PublisherDefault.NarrowTo(Manifest.Parse(Json())).CanRemove("LeveledItem"));
        var narrowed = PatchPolicy.PublisherDefault.NarrowTo(WithRemovable("""["PlacedObject"]"""));
        Assert.True(narrowed.CanRemove("PlacedObject"));
        Assert.False(narrowed.CanRemove("Cell"));

        var publisher = PatchPolicy.PublisherDefault with { Removable = ["PlacedObject"] };
        Assert.Throws<SafePatchException>(() => publisher.NarrowTo(WithRemovable("""["Cell"]""")));
        Assert.Throws<SafePatchException>(() => publisher.NarrowTo(WithRemovable("""["*"]""")));
        Assert.Throws<SafePatchException>(() => WithRemovable("""["Placed Object"]"""));
    }

    [Fact]
    public void Preflight_rejects_a_program_that_does_not_match_the_manifest()
    {
        var package = TestPrograms.Package([1, 2, 3]);
        Assert.Throws<SafePatchException>(() => Preflight.Verify(package.Manifest.ToJson(), [1, 2, 4], PatchPolicy.PublisherDefault));
    }
}
