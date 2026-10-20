using System.Xml.Linq;
using DotCharter.Core.Domain.Vocals;
using DotCharter.Services.Parsing.Rscp;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Parsing.Rscp;

/// <summary>
///     Vocal/lyric save-time data loss. The rscharter wire has no phrase element
///     at all — a phrase exists only as the run of &lt;vocal&gt; entries up to a
///     PHRASE_END — so <see cref="RscpVocalsParser" /> rebuilds every boundary,
///     and every phrase SPAN, by derivation. Two things that derivation cannot
///     survive, both silent on save:
///     <list type="bullet">
///         <item>
///             A syllable that is BOTH a word part and the last of its phrase.
///             There is one legacy <c>flag</c> attribute and WORD_PART took it,
///             so no terminator was written and the phrase never closed on
///             reload: it swallowed the following phrase, dropping the phrase
///             COUNT and the swallowed phrase's own Position/EndPosition.
///         </item>
///         <item>
///             A phrase whose span is not exactly its first lyric's start and
///             its last lyric's end (a lead-in, or a tail past the last
///             syllable), and an EMPTY phrase, which emitted nothing at all and
///             was simply gone. <see cref="DotCharter.Core.Utilities.ChartProjectRestorer" />
///             carries both, so undo preserved what a save destroyed.
///         </item>
///     </list>
///     <para>
///         <see cref="RscpDeepValueRoundTripTests" /> cannot see the second one:
///         its fixture builds the phrase with <c>Position</c> equal to the first
///         lyric's position and <c>EndPosition</c> equal to the last lyric's end
///         BY CONSTRUCTION, which is exactly the derived pair — so the value walk
///         compares a phrase that round-trips by luck.
///     </para>
/// </summary>
public sealed class RscpVocalPhraseRoundTripTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dotcharter_rscp_vocals_").FullName;

    private string ProjectPath => Path.Combine(_dir, "project.rscp");

    public void Dispose() => BestEffortDelete.Directory(_dir);

    /// <summary>
    ///     "run-" ends its phrase AND is a word part. Before the fix the writer's
    ///     <c>else if</c> meant only WORD_PART reached the file, the parser never
    ///     saw a terminator, and the two phrases came back as ONE.
    /// </summary>
    [Fact]
    public void AWordPartThatAlsoEndsItsPhrase_StillClosesThePhraseOnReload()
    {
        var project = NewProject();
        var track = new VocalTrack { Name = "Lead Vocals" };
        track.Phrases.Add(OneLyricPhrase(0, "run", true));
        track.Phrases.Add(OneLyricPhrase(8, "ning"));
        project.VocalTracks.Add(track);

        var loaded = SaveAndReload(project).VocalTracks.Single();

        Assert.Equal(2, loaded.Phrases.Count);
        Assert.Equal(new FractionalPosition(0), loaded.Phrases[0].Position);
        Assert.Equal(new FractionalPosition(8), loaded.Phrases[1].Position);
        Assert.Equal(new FractionalPosition(8, new Fraction(1, 2)), loaded.Phrases[1].EndPosition);
        Assert.Equal("run", loaded.Phrases[0].Lyrics.Single().Text);
        Assert.True(loaded.Phrases[0].Lyrics.Single().IsWordPart);
        Assert.True(loaded.Phrases[0].Lyrics.Single().IsPhraseEnd);
        Assert.Equal("ning", loaded.Phrases[1].Lyrics.Single().Text);
    }

    /// <summary>
    ///     The legacy flag still says WORD_PART — the phrase end rides an ADDITIVE
    ///     attribute beside it, so an older reader loses nothing it already had.
    /// </summary>
    [Fact]
    public void AWordPartThatAlsoEndsItsPhrase_KeepsTheLegacyFlagAndAddsTheAdditiveAttribute()
    {
        var project = NewProject();
        var track = new VocalTrack { Name = "Lead Vocals" };
        track.Phrases.Add(OneLyricPhrase(0, "run", true));
        track.Phrases.Add(OneLyricPhrase(8, "ning"));
        project.VocalTracks.Add(track);

        new RscpChartSerializer().Save(ProjectPath, project);

        var vocalNodes = VocalNodes();
        Assert.Equal("WORD_PART", vocalNodes[0].Attribute("flag")?.Value);
        Assert.Equal("true", vocalNodes[0].Attribute("dcPhraseEnd")?.Value);
    }

    /// <summary>
    ///     A plain phrase end has the legacy slot to itself, so it must NOT grow
    ///     the additive attribute — that is the byte-identical guarantee for every
    ///     chart written before it existed.
    /// </summary>
    [Fact]
    public void APlainPhraseEnd_DoesNotWriteTheAdditiveAttribute()
    {
        var project = NewProject();
        var track = new VocalTrack { Name = "Lead Vocals" };
        track.Phrases.Add(OneLyricPhrase(0, "hello"));
        project.VocalTracks.Add(track);

        new RscpChartSerializer().Save(ProjectPath, project);

        var vocalNode = VocalNodes().Single();
        Assert.Equal("PHRASE_END", vocalNode.Attribute("flag")?.Value);
        Assert.Null(vocalNode.Attribute("dcPhraseEnd"));
    }

    /// <summary>
    ///     A phrase authored to lead in before its first syllable and to run past its
    ///     last. Both ends used to snap back onto the lyrics on every save, because
    ///     the format has no phrase element and the parser re-derives the span.
    /// </summary>
    [Fact]
    public void APhraseWhoseSpanIsWiderThanItsLyrics_RoundTripsUnchanged()
    {
        var project = NewProject();
        var track = new VocalTrack { Name = "Lead Vocals" };
        var phrase = new LyricPhrase
        {
            Position = new FractionalPosition(2),
            EndPosition = new FractionalPosition(9, new Fraction(1, 4))
        };
        phrase.Lyrics.Add(new LyricEvent
        {
            Position = new FractionalPosition(4),
            EndPosition = new FractionalPosition(4, new Fraction(1, 2)),
            Text = "hel",
            IsWordPart = true,
            Note = 62
        });
        phrase.Lyrics.Add(new LyricEvent
        {
            Position = new FractionalPosition(5),
            EndPosition = new FractionalPosition(5, new Fraction(3, 4)),
            Text = "lo",
            IsPhraseEnd = true,
            Note = 64
        });
        track.Phrases.Add(phrase);
        project.VocalTracks.Add(track);

        var loaded = SaveAndReload(project).VocalTracks.Single().Phrases.Single();

        Assert.Equal(new FractionalPosition(2), loaded.Position);
        Assert.Equal(new FractionalPosition(9, new Fraction(1, 4)), loaded.EndPosition);
        Assert.Equal(2, loaded.Lyrics.Count);
        Assert.Equal(new FractionalPosition(4), loaded.Lyrics[0].Position);
        Assert.Equal(new FractionalPosition(5, new Fraction(3, 4)), loaded.Lyrics[1].EndPosition);
    }

    /// <summary>
    ///     A phrase with no lyrics emitted nothing at all and was gone on reload. The
    ///     lyric COUNT on each entry is what puts it back between the right
    ///     neighbours.
    /// </summary>
    [Fact]
    public void AnEmptyPhrase_SurvivesASaveInItsPlace()
    {
        var project = NewProject();
        var track = new VocalTrack { Name = "Lead Vocals" };
        track.Phrases.Add(OneLyricPhrase(0, "one"));
        track.Phrases.Add(new LyricPhrase
        {
            Position = new FractionalPosition(4),
            EndPosition = new FractionalPosition(6)
        });
        track.Phrases.Add(OneLyricPhrase(8, "two"));
        project.VocalTracks.Add(track);

        var loaded = SaveAndReload(project).VocalTracks.Single();

        Assert.Equal(3, loaded.Phrases.Count);
        Assert.Equal("one", loaded.Phrases[0].Lyrics.Single().Text);
        Assert.Empty(loaded.Phrases[1].Lyrics);
        Assert.Equal(new FractionalPosition(4), loaded.Phrases[1].Position);
        Assert.Equal(new FractionalPosition(6), loaded.Phrases[1].EndPosition);
        Assert.Equal("two", loaded.Phrases[2].Lyrics.Single().Text);
        Assert.Equal(new FractionalPosition(8), loaded.Phrases[2].Position);
    }

    /// <summary>
    ///     A phrase whose span IS the derived pair must not write the extension node —
    ///     that is the byte-identical guarantee for every chart written before it
    ///     existed, and it is also why <see cref="RscpDeepValueRoundTripTests" />
    ///     cannot see this bug.
    /// </summary>
    [Fact]
    public void APhraseSpanningExactlyItsLyrics_DoesNotWriteTheExtensionNode()
    {
        var project = NewProject();
        var track = new VocalTrack { Name = "Lead Vocals" };
        track.Phrases.Add(OneLyricPhrase(0, "hello"));
        project.VocalTracks.Add(track);

        new RscpChartSerializer().Save(ProjectPath, project);

        Assert.Null(VocalsOuterNode().Element("dcPhrases"));
    }

    /// <summary>
    ///     A syllable whose text the charter cleared is still a syllable, and a save
    ///     must not delete it.
    ///     <para>
    ///         <c>RscpWriter.BuildVocalLyricsNode</c> emits one <c>&lt;vocal&gt;</c> per
    ///         <c>LyricEvent</c> unconditionally, and the parser dropped every one whose
    ///         text was empty — so the syllable, its span and its Note were gone on
    ///         reload, and the phrase's lyric count changed, which desynchronises the
    ///         <c>n</c> counts <c>ApplyExplicitPhraseSpans</c> reads to put the spans
    ///         back. Clearing a syllable's text is an ordinary, undoable inspector edit.
    ///     </para>
    /// </summary>
    [Fact]
    public void ASyllableWithClearedText_SurvivesTheRoundTrip()
    {
        var project = NewProject();
        var track = new VocalTrack { Name = "Lead Vocals" };
        var phrase = new LyricPhrase
        {
            Position = new FractionalPosition(0),
            EndPosition = new FractionalPosition(1)
        };
        phrase.Lyrics.Add(new LyricEvent
        {
            Position = new FractionalPosition(0),
            EndPosition = new FractionalPosition(0, new Fraction(1, 2)),
            Text = "hel", IsWordPart = true, Note = 62
        });
        phrase.Lyrics.Add(new LyricEvent
        {
            Position = new FractionalPosition(0, new Fraction(1, 2)),
            EndPosition = new FractionalPosition(1),
            Text = "", IsPhraseEnd = true, Note = 64
        });
        track.Phrases.Add(phrase);
        project.VocalTracks.Add(track);

        var serializer = new RscpChartSerializer();
        serializer.Save(ProjectPath, project);
        var loaded = serializer.Load(ProjectPath).VocalTracks.Single();

        var reloaded = Assert.Single(loaded.Phrases);
        Assert.Equal(2, reloaded.Lyrics.Count);
        Assert.Equal("", reloaded.Lyrics[1].Text);
        Assert.Equal(64, reloaded.Lyrics[1].Note);
        Assert.Equal(new FractionalPosition(0, new Fraction(1, 2)), reloaded.Lyrics[1].Position);
    }

    /// <summary>
    ///     #283: a phrase end flagged on a lyric BEFORE the last one (a line break inside
    ///     the phrase) is written as PHRASE_END, and the parser split the phrase there on
    ///     every reload; the next phrase then took the wrong explicit span.
    /// </summary>
    [Fact]
    public void APhraseEndInsideAPhrase_DoesNotSplitItOnReload()
    {
        var project = NewProject();
        var track = new VocalTrack { Name = "Lead Vocals" };
        var phrase = new LyricPhrase { Position = new FractionalPosition(0), EndPosition = new FractionalPosition(3) };
        for (var beat = 0; beat < 3; beat++)
        {
            phrase.Lyrics.Add(new LyricEvent
            {
                Position = new FractionalPosition(beat),
                EndPosition = new FractionalPosition(beat, new Fraction(1, 2)),
                Text = "w" + beat,
                IsPhraseEnd = beat == 1,
                Note = 60
            });
        }

        track.Phrases.Add(phrase);
        track.Phrases.Add(OneLyricPhrase(8, "next"));
        project.VocalTracks.Add(track);

        var loaded = SaveAndReload(project).VocalTracks.Single();

        Assert.Equal(2, loaded.Phrases.Count);
        Assert.Equal(["w0", "w1", "w2"], loaded.Phrases[0].Lyrics.Select(l => l.Text));
        // The mid-phrase flag survives (the last lyric always reloads as its phrase's end).
        Assert.Equal([false, true], loaded.Phrases[0].Lyrics.Take(2).Select(l => l.IsPhraseEnd));
        Assert.Equal(new FractionalPosition(0), loaded.Phrases[0].Position);
        Assert.Equal(new FractionalPosition(3), loaded.Phrases[0].EndPosition);
        Assert.Equal("next", loaded.Phrases[1].Lyrics.Single().Text);
        Assert.Equal(new FractionalPosition(8), loaded.Phrases[1].Position);
    }

    /// <summary>The lyric counts are only trusted when they account for every lyric; a
    /// node that does not (a truncated or hand-edited file) keeps the old mapping.</summary>
    [Fact]
    public void LyricCountsThatDoNotAddUp_FallBackToTheDerivedPhrases()
    {
        var project = NewProject();
        var track = new VocalTrack { Name = "Lead Vocals" };
        track.Phrases.Add(new LyricPhrase());
        track.Phrases.Add(OneLyricPhrase(0, "a"));
        track.Phrases.Add(OneLyricPhrase(8, "b"));
        project.VocalTracks.Add(track);
        new RscpChartSerializer().Save(ProjectPath, project);

        var doc = XDocument.Load(ProjectPath);
        doc.Root!.Element("vocalPaths")!.Element("vocals")!.Element("dcPhrases")!.Elements("phrase").Last()
            .SetAttributeValue("n", "5");
        doc.Save(ProjectPath);
        var loaded = new RscpChartSerializer().Load(ProjectPath).VocalTracks.Single();

        Assert.Equal(3, loaded.Phrases.Count);
        Assert.Empty(loaded.Phrases[0].Lyrics);
        Assert.Equal("a", loaded.Phrases[1].Lyrics.Single().Text);
        Assert.Equal("b", loaded.Phrases[2].Lyrics.Single().Text);
    }

    private static LyricPhrase OneLyricPhrase(int bar, string text, bool isWordPart = false)
    {
        var start = new FractionalPosition(bar);
        var end = new FractionalPosition(bar, new Fraction(1, 2));
        var phrase = new LyricPhrase { Position = start, EndPosition = end };
        phrase.Lyrics.Add(new LyricEvent
        {
            Position = start,
            EndPosition = end,
            Text = text,
            IsWordPart = isWordPart,
            IsPhraseEnd = true,
            Note = 60
        });
        return phrase;
    }

    private XElement VocalsOuterNode() => XDocument.Load(ProjectPath).Root!.Element("vocalPaths")!.Element("vocals")!;

    private List<XElement> VocalNodes() => VocalsOuterNode().Element("vocals")!.Elements("vocal").ToList();

    private ChartProject SaveAndReload(ChartProject project)
    {
        new RscpChartSerializer().Save(ProjectPath, project);
        return new RscpChartSerializer().Load(ProjectPath);
    }

    private static ChartProject NewProject()
    {
        var project = new ChartProject();
        var arrangement = new Arrangement { Name = "Lead" };
        arrangement.Levels.Add(new ChartLevel());
        project.Arrangements.Add(arrangement);
        return project;
    }
}
