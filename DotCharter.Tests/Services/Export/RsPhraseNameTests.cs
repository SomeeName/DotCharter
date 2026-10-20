using DotCharter.Services.Export.Rocksmith;
using DotCharter.Services.Export.Sng;
using DotCharter.Services.Parsing.Xml.Song;

namespace DotCharter.Tests.Services.Export;

/// <summary>
///     #322: Rocksmith only accepts phrase names in <c>[a-zA-Z0-9 _#]</c>, and the
///     chart is allowed to hold anything (a ja-JP or de-DE charter's names
///     included). The legal spelling is made at the EXPORT boundary — the RS-XML
///     writer and the SNG builder (whose names the psarc manifest copies) — by
///     <see cref="RsPhraseName" />, and must be legal, unique, and must not lose
///     the <c>PhraseInfo</c> (solo flag, max difficulty) keyed by the authored name.
/// </summary>
public sealed class RsPhraseNameTests
{
    [Theory]
    [InlineData("Solo (fast)", "Solo fast")]
    [InlineData("Übergang", "Ubergang")]
    [InlineData("Straße", "Strasse")]
    [InlineData("Refrain – 2", "Refrain 2")]
    [InlineData("Verse 1 - Intro", "Verse 1 Intro")]
    [InlineData("Chorus@#%", "Chorus#")]
    [InlineData("Don't stop!", "Dont stop")]
    [InlineData("サビ", "phrase3")]
    [InlineData("", "phrase3")]
    public void Sanitize_SpellsAnIllegalNameLegally(string name, string expected) =>
        Assert.Equal(expected, RsPhraseName.Sanitize(name, 3));

    /// <summary>An imported Rocksmith chart must round-trip byte-identically, so a
    /// legal name is never "tidied" — not even its double space.</summary>
    [Theory]
    [InlineData("COUNT")]
    [InlineData("Verse  1")]
    [InlineData("riff_2#")]
    public void Sanitize_LeavesALegalNameAlone(string name) => Assert.Equal(name, RsPhraseName.Sanitize(name, 0));

    [Fact]
    public void SanitizeAll_KeepsTheResultUnique_AndLetsALegalNameKeepItsSpelling()
    {
        var exported = RsPhraseName.SanitizeAll(["Riff!", "Riff?", "Riff", "サビ", "phrase3"]);

        Assert.Equal(["Riff 2", "Riff 3", "Riff", "phrase3 2", "phrase3"], exported);
    }

    [Fact]
    public void SanitizeAll_JudgesUniquenessOnTheCutName_TheSngCanHold()
    {
        var a = new string('A', 40) + "!";
        var b = new string('A', 40) + "?";

        var exported = RsPhraseName.SanitizeAll([a, b], RsPhraseName.SngMaxLength);

        Assert.All(exported, n => Assert.True(n.Length <= RsPhraseName.SngMaxLength, n));
        AssertLegalAndUnique(exported);
    }

    // ── the two writers ──────────────────────────────────────────────────────

    private static readonly string[] AuthoredNames = ["COUNT", "Solo (fast)", "サビ", "Riff!", "Riff?", "Riff", "END"];

    private static ChartProject ProjectWithAuthoredNames()
    {
        var arrangement = new Arrangement { Name = "Lead", ArrangementType = ArrangementType.Lead, StringCount = 6 };
        for (var i = 0; i < 6; i++) arrangement.TuningPitches.Add(new TuningOffset(0));
        arrangement.Phrases.AddRange(AuthoredNames);
        for (var i = 0; i < AuthoredNames.Length; i++)
            arrangement.PhraseIterations.Add(new PhraseIteration(new FractionalPosition(i * 4), i));
        arrangement.PhraseInfos["Solo (fast)"] = new PhraseInfo(0, true);

        var level = new ChartLevel { DifficultyIndex = 0 };
        level.Notes.Add(new GuitarNote { Position = new FractionalPosition(5), StringIndex = 0, Fret = 3 });
        level.Anchors.Add(new FretAnchor(new FractionalPosition(0), 1));
        arrangement.Levels.Add(level);

        var project = new ChartProject();
        project.Arrangements.Add(arrangement);
        for (var i = 0; i < 40; i++)
            project.Beats.Add(new BeatMarker { Time = i * 500.0, Bpm = 120, IsMeasure = i % 4 == 0 });
        return project;
    }

    [Fact]
    public void TheRsXml_WritesLegalUniquePhraseNames_AndKeepsTheSoloFlag()
    {
        var doc = new RsXmlSongWriter().Write(ProjectWithAuthoredNames());

        var phrases = doc.Descendants("phrases").Single().Elements("phrase").ToList();
        var names = phrases.Select(p => p.Attribute("name")!.Value).ToList();

        Assert.Equal(AuthoredNames.Length, names.Count);
        AssertLegalAndUnique(names);
        Assert.Equal("Solo fast", names[1]);
        Assert.Equal("1", phrases[1].Attribute("solo")?.Value);
        // Ids are positional, so the iterations still point at the same phrases.
        Assert.Equal(["COUNT", "END"], [names[0], names[^1]]);
    }

    [Fact]
    public void TheSng_WritesLegalUniquePhraseNames_AndKeepsTheSoloFlag()
    {
        var project = ProjectWithAuthoredNames();

        var sng = SngFromDomainMapper.BuildInstrumental(project.Arrangements[0], project.Beats, 20_000).Sng;

        var names = sng.Phrases.Select(p => p.Name).ToList();
        Assert.Equal(AuthoredNames.Length, names.Count);
        AssertLegalAndUnique(names);
        Assert.All(names, n => Assert.True(n.Length <= RsPhraseName.SngMaxLength, n));
        Assert.Equal("Solo fast", names[1]);
        Assert.Equal((sbyte)1, sng.Phrases[1].Solo);
    }

    /// <summary>The model is the charter's: exporting reads it, never rewrites it.</summary>
    [Fact]
    public void Exporting_DoesNotRewriteTheChart()
    {
        var project = ProjectWithAuthoredNames();

        _ = new RsXmlSongWriter().Write(project);
        _ = SngFromDomainMapper.BuildInstrumental(project.Arrangements[0], project.Beats, 20_000);

        Assert.Equal(AuthoredNames, project.Arrangements[0].Phrases);
    }

    private static void AssertLegalAndUnique(IReadOnlyList<string> names)
    {
        Assert.All(names, n => Assert.True(RsPhraseName.IsLegal(n), $"illegal phrase name \"{n}\""));
        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }
}
