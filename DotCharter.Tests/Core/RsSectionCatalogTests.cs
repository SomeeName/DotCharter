using DotCharter.Services.Parsing.GuitarPro;

namespace DotCharter.Tests.Core;

/// <summary>
///     The section vocabulary Rocksmith renders. The game's
///     <c>getSectionUIName</c> is a CLOSED lookup with no fallback, so a name
///     outside this list ships as a section the player never sees — which is
///     exactly what happened when the owner hand-typed <c>Section='COUNT'</c>
///     (a real RS phrase name, not a section).
/// </summary>
public sealed class RsSectionCatalogTests
{
    [Theory]
    [InlineData("intro")]
    [InlineData("prechorus")]
    [InlineData("postchorus")]
    [InlineData("noguitar")]
    [InlineData("VERSE")]
    [InlineData("  chorus  ")]
    public void ARealSectionName_IsValid(string name) => Assert.True(RsSectionCatalog.IsValid(name));

    [Theory]
    [InlineData("COUNT")]      // a real RS PHRASE name — not a section
    [InlineData("riff1")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void AnythingElse_IsNot(string? name) => Assert.False(RsSectionCatalog.IsValid(name));

    [Fact]
    public void NormalizeLowercasesAKnownNameAndRejectsTheRest()
    {
        Assert.Equal("chorus", RsSectionCatalog.Normalize(" Chorus "));
        Assert.Null(RsSectionCatalog.Normalize("COUNT"));
    }

    [Fact]
    public void TheCatalogueIsTheOneTheGpImporterAccepts()
    {
        // A name the dialog offers but the importer rejects (or vice versa)
        // would mean "what you can author" and "what survives an import"
        // disagree — the split this catalogue exists to remove.
        foreach (var name in RsSectionCatalog.Names)
            Assert.True(GpMarkerImport.IsRsSectionName(name), $"'{name}' is offered but the importer rejects it.");

        Assert.False(GpMarkerImport.IsRsSectionName("COUNT"));
    }

    [Fact]
    public void EveryNameHasAHumanLabelAndNoDuplicatesExist()
    {
        Assert.Equal(RsSectionCatalog.Names.Count, RsSectionCatalog.Names.Distinct().Count());
        foreach (var name in RsSectionCatalog.Names)
            Assert.False(string.IsNullOrWhiteSpace(RsSectionCatalog.DisplayName(name)));

        Assert.Equal("Pre Chorus", RsSectionCatalog.DisplayName("prechorus"));
        Assert.Equal("Verse", RsSectionCatalog.DisplayName("verse"));
    }

    /// <summary>
    ///     Editor on Fire's <c>eof_rs_predefined_sections[]</c>
    ///     (<c>EOF_SRC/rs.c:31</c>), transcribed in its own order — which is the
    ///     order its picker lists them, and roughly the order a song is built.
    ///     The ORDER is asserted, not just the set, because this list is what the
    ///     dropdown shows and a charter coming from EOF is reading down it by
    ///     muscle memory.
    /// </summary>
    [Fact]
    public void TheCatalogueIsEofsTableInEofsOrder()
    {
        Assert.Equal(new[]
        {
            "noguitar", "intro", "preverse", "verse", "postvs", "modverse",
            "prechorus", "chorus", "postchorus", "modchorus", "hook",
            "prebrdg", "bridge", "postbrdg", "solo", "outro",
            "ambient", "breakdown", "interlude", "transition", "riff",
            "fadein", "fadeout", "buildup", "variation", "head",
            "modbridge", "melody", "vamp", "silence", "tapping"
        }, RsSectionCatalog.Names);
    }

    /// <summary>The labels are EOF's too, character for character — that is the
    ///     vocabulary a charter arriving from EOF already reads.</summary>
    [Theory]
    [InlineData("noguitar", "No Guitar")]
    [InlineData("preverse", "Pre Verse")]
    [InlineData("postvs", "Post Verse")]
    [InlineData("modverse", "Modulated Verse")]
    [InlineData("prechorus", "Pre Chorus")]
    [InlineData("postchorus", "Post Chorus")]
    [InlineData("modchorus", "Modulated Chorus")]
    [InlineData("prebrdg", "Pre Bridge")]
    [InlineData("postbrdg", "Post Bridge")]
    [InlineData("modbridge", "Modulated Bridge")]
    [InlineData("fadein", "Fade In")]
    [InlineData("fadeout", "Fade Out")]
    [InlineData("tapping", "Tapping")]
    public void TheLabelsAreEofs(string name, string expected) =>
        Assert.Equal(expected, RsSectionCatalog.DisplayName(name));
}
