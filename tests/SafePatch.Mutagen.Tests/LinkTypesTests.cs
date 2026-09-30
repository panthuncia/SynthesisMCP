using Mutagen.Bethesda.Skyrim;

namespace SafePatch.Mutagen.Tests;

public sealed class LinkTypesTests
{
    [Fact]
    public void A_type_links_only_to_what_its_fields_declare()
    {
        Assert.True(LinkTypes.MayLinkTo(typeof(Landscape), typeof(LandscapeTexture)));
        Assert.False(LinkTypes.MayLinkTo(typeof(Landscape), typeof(Weapon)));
        // Entries link to items, which a weapon is.
        Assert.True(LinkTypes.MayLinkTo(typeof(LeveledItem), typeof(Weapon)));
        Assert.False(LinkTypes.MayLinkTo(typeof(LandscapeTexture), typeof(Weapon)));
    }

    [Fact]
    public void A_field_of_a_base_type_links_to_what_any_of_its_subclasses_can()
    {
        // An entry's owner (an abstract owner target) can be an untyped owner, whose link may be to any record.
        Assert.True(LinkTypes.MayLinkTo(typeof(LeveledItem), typeof(Quest)));
        Assert.Contains(typeof(ISkyrimMajorRecordGetter), LinkTypes.Of(typeof(LeveledItem)).Types);
        Assert.DoesNotContain(typeof(ISkyrimMajorRecordGetter), LinkTypes.Of(typeof(Landscape)).Types);
    }

    [Fact]
    public void Child_records_links_are_not_their_parents()
    {
        // A cell's placed objects link to weapons; the cell's own fields do not.
        Assert.False(LinkTypes.MayLinkTo(typeof(Cell), typeof(Weapon)));
        Assert.True(LinkTypes.MayLinkTo(typeof(PlacedObject), typeof(Weapon)));
        Assert.True(LinkTypes.MayLinkTo(typeof(Cell), typeof(Faction)));
    }
}
