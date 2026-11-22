using DotCharter.Core.Domain.Vocals;
using DotCharter.Services.Export.Sng;
using DotCharter.Services.Parsing.Xml.Vocals;

namespace DotCharter.Tests.Services.Export;

/// <summary>
///     The vocals XML and the vocals SNG carry the same lyric tokens for every chart: one rule
///     for which syllables are written, which one ends the line and how a marker is appended.
///     Compared per syllable time, because the package lists its vocals by time while the XML
///     keeps each line together (the export check blocks a chart where the two orders differ).
/// </summary>
public sealed class VocalsWriterTokenParityTests
{
    public static TheoryData<string> Charts => [.. ChartsByName.Keys];

    [Theory]
    [MemberData(nameof(Charts))]
    public void The_sng_and_the_xml_write_the_same_lyric_tokens(string chart)
    {
        var project = Project(ChartsByName[chart]());

        Assert.Equal(XmlTokens(project), SngTokens(project));
    }

    [Fact]
    public void A_line_end_whose_text_ends_in_a_plus_keeps_its_text_in_the_sng()
    {
        var project = Project(ChartsByName["last ends in plus, no flag"]());

        Assert.Equal(["x", "a++", "y+"], SngTokens(project).Select(t => t.Lyric));
    }

    [Fact]
    public void A_blank_last_syllable_hands_the_line_end_to_the_last_written_one_in_both_files()
    {
        var project = Project(ChartsByName["blank last syllable"]());

        Assert.Equal(["a+", "y+"], XmlTokens(project).Select(t => t.Lyric));
    }

    /// <summary>A blank syllable is not written; the line end it carries goes to the written one before
    /// it in that line, or nowhere when the line has none (an empty line is not written).</summary>
    [Theory]
    [InlineData("blank syllable ending a line inside a phrase", new[] { "a+", "b+" })]
    [InlineData("blank syllable ending an empty line", new[] { "b+" })]
    public void A_blank_syllable_that_ends_a_line_hands_its_line_end_back_in_both_files(string chart, string[] expected)
    {
        var project = Project(ChartsByName[chart]());

        Assert.Equal(expected, XmlTokens(project).Select(t => t.Lyric));
        Assert.Equal(expected, SngTokens(project).Select(t => t.Lyric));
    }

    private static readonly Dictionary<string, Func<LyricPhrase[]>> ChartsByName = new()
    {
        ["last ends in plus, no flag"] = () => [Unflagged(Line(Syllable(1, "x"), Syllable(2, "a+"))), Line(Syllable(6, "y"))],
        ["last ends in hyphen, no flag"] = () => [Unflagged(Line(Syllable(1, "x"), Syllable(2, "go-"))), Line(Syllable(6, "y"))],
        ["blank last syllable"] = () => [Line(Syllable(2, "a"), Syllable(4, " ")), Line(Syllable(6, "y"))],
        ["blank middle syllable"] = () => [Line(Syllable(2, "a"), Syllable(3, ""), Syllable(4, "b"))],
        ["out of order inside a line"] = () => [Line(Syllable(4, "b"), Syllable(2, "a")), Line(Syllable(6, "y"))],
        ["word part at the line end"] = () => [Line(Syllable(2, "hel", wordPart: true)), Line(Syllable(4, "lo"))],
        ["word part ending in plus inside a line"] = () => [Line(Syllable(2, "a+", wordPart: true), Syllable(4, "b"))],
        ["word part ending in hyphen inside a line"] = () => [Line(Syllable(2, "a-", wordPart: true), Syllable(4, "b"))],
        ["trailing space"] = () => [Line(Syllable(2, "a ", wordPart: true), Syllable(4, "b "))],
        ["phrase end flag inside a line"] = () => [Flagged(Line(Syllable(2, "a"), Syllable(4, "b")), 0)],
        ["phrase end flag unset everywhere"] = () => [Unflagged(Line(Syllable(2, "a"), Syllable(4, "b")))],
        ["blank syllable ending a line inside a phrase"] = () => [Flagged(Line(Syllable(2, "a"), Syllable(3, ""), Syllable(4, "b")), 1)],
        ["blank syllable ending an empty line"] = () => [Flagged(Line(Syllable(2, ""), Syllable(4, "b")), 0)]
    };

    private static List<(int TimeMs, string Lyric)> XmlTokens(ChartProject project)
        => [.. new RsXmlVocalsWriter().Write(project).Root!.Elements("vocal")
            .Select(v => ((int)Math.Round(double.Parse(v.Attribute("time")!.Value, System.Globalization.CultureInfo.InvariantCulture) * 1000),
                v.Attribute("lyric")!.Value))
            .OrderBy(t => t.Item1).ThenBy(t => t.Item2, StringComparer.Ordinal)];

    private static List<(int TimeMs, string Lyric)> SngTokens(ChartProject project)
        => [.. SngFromDomainMapper.BuildVocals(project.VocalTracks[0], project.Beats, DateTime.UnixEpoch).Vocals
            .Select(v => ((int)Math.Round(v.Time * 1000.0), v.Lyric))
            .OrderBy(t => t.Item1).ThenBy(t => t.Item2, StringComparer.Ordinal)];

    private static ChartProject Project(IEnumerable<LyricPhrase> phrases)
    {
        var project = new ChartProject();
        foreach (var beat in Enumerable.Range(0, 32).Select(i => new BeatMarker { Time = i * 500, Bpm = 120, IsMeasure = i % 4 == 0 }))
            project.Beats.Add(beat);
        var track = new VocalTrack { Name = "Vocals" };
        foreach (var phrase in phrases) track.Phrases.Add(phrase);
        project.VocalTracks.Add(track);
        return project;
    }

    private static LyricPhrase Line(params LyricEvent[] lyrics)
    {
        var phrase = new LyricPhrase
        {
            Position = lyrics.Min(l => l.Position), EndPosition = lyrics.Max(l => l.EndPosition)
        };
        foreach (var lyric in lyrics) phrase.Lyrics.Add(lyric);
        phrase.Lyrics[^1].IsPhraseEnd = true;
        return phrase;
    }

    private static LyricPhrase Unflagged(LyricPhrase phrase)
    {
        foreach (var lyric in phrase.Lyrics) lyric.IsPhraseEnd = false;
        return phrase;
    }

    private static LyricPhrase Flagged(LyricPhrase phrase, int index)
    {
        phrase.Lyrics[index].IsPhraseEnd = true;
        return phrase;
    }

    private static LyricEvent Syllable(int beat, string text, bool wordPart = false) => new()
    {
        Position = new FractionalPosition(beat), EndPosition = new FractionalPosition(beat + 1),
        Text = text, IsWordPart = wordPart, Note = 60
    };
}
