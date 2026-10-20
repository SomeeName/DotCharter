namespace DotCharter.Tests.Core;

/// <summary>
///     The Rocksmith EVENT vocabulary — the metronome ticks and crowd cues that
///     sit on a marker next to its section and phrase. Ported from Editor on
///     Fire's <c>eof_rs_predefined_events[]</c> (<c>EOF_SRC/rs.c:66</c>).
/// </summary>
public sealed class RsEventCatalogTests
{
    /// <summary>
    ///     EOF's table, transcribed. This is the point of the type: the codes are
    ///     a closed set the game understands, and a typo here ships a cue that
    ///     does nothing.
    /// </summary>
    [Fact]
    public void TheCatalogIsEofsTableVerbatim()
    {
        Assert.Equal(
            new[] { "B0", "B1", "e0", "e1", "e2", "E3", "D3", "E13" },
            RsEventCatalog.Events.Select(e => e.Code));

        Assert.All(RsEventCatalog.Events,
            e => Assert.False(string.IsNullOrWhiteSpace(e.DisplayName), $"{e.Code} has no label"));
    }

    /// <summary>
    ///     EOF matches EVENTS with <c>ustrcmp</c> and SECTIONS with
    ///     <c>ustricmp</c>, and this set is why: <c>e0</c>/<c>e1</c>/<c>e2</c> are
    ///     lower-case, <c>E3</c>/<c>E13</c> upper-case, and <c>E3</c> is a
    ///     different event from any <c>e3</c>.
    /// </summary>
    [Fact]
    public void CodesAreCaseSensitive()
    {
        Assert.True(RsEventCatalog.IsKnown("E3"));
        Assert.False(RsEventCatalog.IsKnown("e3"));
        Assert.True(RsEventCatalog.IsKnown("e0"));
        Assert.False(RsEventCatalog.IsKnown("E0"));
        Assert.False(RsEventCatalog.IsKnown("b0"));
        Assert.True(RsEventCatalog.IsKnown("B0"));
    }

    [Theory]
    [InlineData(null, new string[0])]
    [InlineData("", new string[0])]
    [InlineData("   ", new string[0])]
    [InlineData("B0", new[] { "B0" })]
    [InlineData("B0,B1", new[] { "B0", "B1" })]
    [InlineData(" B0 , B1 ", new[] { "B0", "B1" })]
    [InlineData("B0,,B1,", new[] { "B0", "B1" })]
    [InlineData("B0,B0", new[] { "B0" })]
    public void ParseIsTotalAndCanonical(string? stored, string[] expected) =>
        // The field is free text from someone else's exporter as often as ours,
        // so nothing here may throw.
        Assert.Equal(expected, RsEventCatalog.Parse(stored));

    [Fact]
    public void ToggleAddsAndRemovesWithoutDisturbingTheRest()
    {
        Assert.Equal("B0,e1", RsEventCatalog.Toggle("B0", "e1", true));
        Assert.Equal("B0", RsEventCatalog.Toggle("B0,e1", "e1", false));

        // Idempotent both ways — a checkbox re-asserting its state must not
        // duplicate the code or throw.
        Assert.Equal("B0", RsEventCatalog.Toggle("B0", "B0", true));
        Assert.Equal("B0", RsEventCatalog.Toggle("B0", "e1", false));
    }

    /// <summary>
    ///     A code this catalogue does not know belongs to whoever authored the
    ///     chart. Dropping it on the first edit is the same class of silent data
    ///     loss as an rscp field with a reader and no writer.
    /// </summary>
    [Fact]
    public void AForeignCodeSurvivesAToggle()
    {
        var result = RsEventCatalog.Toggle("B0,SOMETHING_ELSE", "e1", true);

        Assert.Contains("SOMETHING_ELSE", RsEventCatalog.Parse(result));
        Assert.Contains("B0", RsEventCatalog.Parse(result));
        Assert.Contains("e1", RsEventCatalog.Parse(result));
    }

    [Fact]
    public void JoinProducesTheStoredForm()
    {
        Assert.Equal("B0,e1", RsEventCatalog.Join(new[] { "B0", "e1" }));
        Assert.Equal("B0", RsEventCatalog.Join(new[] { " B0 ", "", "  " }));
        Assert.Equal("", RsEventCatalog.Join(Array.Empty<string>()));
    }

    [Fact]
    public void DisplayNameFallsBackToTheCodeItself()
    {
        Assert.Equal("High pitch tick (B0)", RsEventCatalog.DisplayName("B0"));
        // Shown as itself rather than hidden — an imported chart's own code is
        // still something the charter needs to see.
        Assert.Equal("WHATEVER", RsEventCatalog.DisplayName("WHATEVER"));
    }
}
