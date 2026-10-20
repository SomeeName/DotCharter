using DotCharter.Services.Export.Sng;
using DotCharter.Services.Export.Sng.Model;
using DotCharter.Services.Parsing.Xml.Song;
using DotCharter.Tests.Services.Parsing.GuitarPro;

namespace DotCharter.Tests.Services.Export;

/// <summary>
///     <b>The SNG is what the GAME reads.</b> A chord a <c>LinkNext</c> sound
///     lands on has to be spelled as loose single notes — RS2014 reads a chord
///     as a STRUM, and a shape slid into was never re-picked — and
///     <c>LinkLandingBreakup</c> is where that projection lives. The RS-XML
///     writer consumed it from the day it was written; the SNG builder never
///     did, so every `.psarc` this app produced (the save companion AND the
///     package export) shipped exactly the chord the XML refuses to write.
///     <para>These tests assert the two export paths agree, which is the only
///     form of the claim that cannot rot when one of them changes.</para>
/// </summary>
public sealed class SngLinkLandingBreakupTests
{
    private static List<BeatMarker> Beats(int count = 64)
    {
        var beats = new List<BeatMarker>(count);
        for (var i = 0; i < count; i++)
        {
            beats.Add(new BeatMarker
            { Time = i * 500.0, IsMeasure = i % 4 == 0, Bpm = 120.0, BeatsInMeasure = 4, NoteDenominator = 4 });
        }

        return beats;
    }

    /// <summary>A note that links into a two-string shape one beat later.</summary>
    private static Arrangement LinkedIntoChord(IList<BeatMarker> beats)
    {
        var arrangement = new Arrangement { Name = "Lead", ArrangementType = ArrangementType.Lead, StringCount = 6 };
        for (var i = 0; i < 6; i++) arrangement.TuningPitches.Add(new TuningOffset(0));

        var template = new ChordTemplate { Id = 0, Name = "A5", Frets = { [1] = 7, [2] = 9 } };
        arrangement.ChordTemplates.Add(template);

        arrangement.Phrases.AddRange(["COUNT", "riff", "END"]);
        arrangement.PhraseIterations.Add(new PhraseIteration(new FractionalPosition(0), 0));
        arrangement.PhraseIterations.Add(new PhraseIteration(new FractionalPosition(4), 1));
        arrangement.PhraseIterations.Add(new PhraseIteration(new FractionalPosition(56), 2));

        var level = new ChartLevel { DifficultyIndex = 0 };
        // The link SOURCE: one beat long, landing exactly on the chord, on a
        // string the chord plays.
        level.Notes.Add(new GuitarNote
        {
            Position = new FractionalPosition(4),
            StringIndex = 1,
            Fret = 5,
            Sustain = new FractionalPosition(1),
            LinkNext = true
        });

        var chord = new Chord { Position = new FractionalPosition(5), TemplateId = 0 };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 1, Fret = 7 });
        chord.ChordNotes.Add(new ChordNote { StringIndex = 2, Fret = 9 });
        level.Chords.Add(chord);

        // An UNLINKED chord later on, which must survive as a chord — the
        // projection is a scalpel, not a blanket.
        var strum = new Chord { Position = new FractionalPosition(12), TemplateId = 0 };
        strum.ChordNotes.Add(new ChordNote { StringIndex = 1, Fret = 7 });
        strum.ChordNotes.Add(new ChordNote { StringIndex = 2, Fret = 9 });
        level.Chords.Add(strum);

        level.HandShapes.Add(new HandShape
        { Position = new FractionalPosition(5), EndPosition = new FractionalPosition(6), TemplateId = 0 });
        level.Anchors.Add(new FretAnchor(new FractionalPosition(0), 5));
        arrangement.Levels.Add(level);
        return arrangement;
    }

    [Fact]
    public void TheChordALinkLandsOn_IsNotWrittenAsAChordInTheSng()
    {
        var beats = Beats();
        var sng = SngFromDomainMapper.BuildInstrumental(LinkedIntoChord(beats), beats, 30000).Sng;

        var landing = sng.Levels[0].Notes.Where(n => Math.Abs(n.Time - 2.5f) < 0.002f).ToList();

        Assert.Equal(2, landing.Count);
        Assert.All(landing, n => Assert.Equal(0u, n.Mask & RsNoteMask.Chord));
        Assert.Equal([1, 2], landing.Select(n => (int)n.StringIndex).Order());
        Assert.Equal([7, 9], landing.Select(n => (int)n.Fret).Order());
    }

    [Fact]
    public void AnUnlinkedChord_StillShipsAsAChord()
    {
        var beats = Beats();
        var sng = SngFromDomainMapper.BuildInstrumental(LinkedIntoChord(beats), beats, 30000).Sng;

        var strum = Assert.Single(sng.Levels[0].Notes, n => Math.Abs(n.Time - 6.0f) < 0.002f);
        Assert.Equal(RsNoteMask.Chord, strum.Mask & RsNoteMask.Chord);
    }

    /// <summary>
    ///     A chord marked <c>SplitIntoNotes</c> ships as one note per string in the
    ///     game's file too — here the imported pick scrapes of the owner's reference
    ///     (#599): eight ignored, tapped, tremolo, fret-hand-muted notes with an
    ///     unpitched slide, and no chord.
    /// </summary>
    [Fact]
    public void ASplitChord_ShipsAsSingleNotesInTheSng()
    {
        var project = PickScrapeScenes.ReferenceProject();
        var arrangement = project.Arrangements[0];
        Assert.Equal(4, arrangement.Levels[0].Chords.Count(c => c.SplitIntoNotes));

        var sng = SngFromDomainMapper.BuildInstrumental(arrangement, project.Beats, 300_000).Sng;

        var notes = sng.Levels[0].Notes;
        Assert.DoesNotContain(notes, n => (n.Mask & RsNoteMask.Chord) != 0);
        var scrapes = notes.Where(n => (n.Mask & RsNoteMask.Ignore) != 0).ToList();
        Assert.Equal(8, scrapes.Count);
        const uint scrape = RsNoteMask.Tap | RsNoteMask.Tremolo | RsNoteMask.Mute | RsNoteMask.UnpitchedSlide;
        Assert.All(scrapes, n => Assert.Equal(scrape, n.Mask & scrape));
    }

    /// <summary>
    ///     The invariant that cannot rot: whatever the RS-XML writer decides to
    ///     spell as a chord, the SNG spells as a chord too. Both consume the one
    ///     projection.
    /// </summary>
    [Fact]
    public void TheSngAndTheRsXml_SpellTheSameSoundsAsChords()
    {
        var beats = Beats();
        var arrangement = LinkedIntoChord(beats);
        var sng = SngFromDomainMapper.BuildInstrumental(arrangement, beats, 30000).Sng;

        var (xmlNotes, xmlChords) = LinkLandingBreakup.Project(arrangement.Levels[0]);

        var sngNotes = sng.Levels[0].Notes;
        Assert.Equal(xmlChords.Count, sngNotes.Count(n => (n.Mask & RsNoteMask.Chord) != 0));
        Assert.Equal(xmlNotes.Count, sngNotes.Count(n => (n.Mask & RsNoteMask.Chord) == 0));
    }
}
