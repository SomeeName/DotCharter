using DotCharter.Services.Parsing.GuitarPro;

namespace DotCharter.Tests.Services.Parsing.GuitarPro;

/// <summary>
///     The LANDING-side twin of inspector L09 (<c>SlideFromOpenString</c>).
///     Both parsers guard the slide OUT — <c>GpNoteParser.ApplySlideFlags</c> and
///     <c>SongsterrConverter</c> only set the <c>-1</c> "slide to the next sound"
///     marker when <c>Fret &gt; 0</c> — but nothing checked where the marker
///     RESOLVED. A shift/legato slide whose next sound on that string is an OPEN
///     note therefore resolved to <c>SlideTo = 0</c>: a slide INTO an open string,
///     which is not a thing a hand can play. Zero then survived every later pass
///     (they all test <c>SlideTo is &gt; 0</c>, so none of them saw it) and
///     <c>SngFromDomainMapper</c> exported it as a real slide, because it sets
///     <c>RsNoteMask.Slide</c> for anything that is not <c>-1</c>. No validation
///     rule caught it either.
///     <para>
///     <c>GpPostProcessor.LinkSlides</c> is the pass EVERY tab importer shares
///     (gpif, GP3-5 binary, Songsterr), so the fix and these tests cover all
///     three. An open landing is now treated exactly like the same-fret case:
///     the slide is dropped and any legato <c>LinkNext</c> is kept, degrading the
///     note to a plain linked note.
///     </para>
/// </summary>
public sealed class GpSlideOntoOpenStringTests
{
    private static GuitarNote Slider(int stringIndex, int fret, bool linkNext = false) => new()
    {
        Position = new FractionalPosition(0, Fraction.Zero),
        Sustain = new FractionalPosition(0, new Fraction(1, 2)),
        StringIndex = stringIndex,
        Fret = fret,
        SlideTo = -1,
        LinkNext = linkNext
    };

    private static GuitarNote Landing(int stringIndex, int fret) => new()
    {
        Position = new FractionalPosition(0, new Fraction(1, 2)),
        StringIndex = stringIndex,
        Fret = fret
    };

    /// <summary>A shift/legato slide whose landing note is OPEN is no slide at
    /// all: the marker is dropped rather than resolved to fret 0.</summary>
    [Fact]
    public void LinkSlides_OpenLandingNote_DropsTheSlide()
    {
        var level = new ChartLevel();
        var first = Slider(3, 7);
        level.Notes.Add(first);
        level.Notes.Add(Landing(3, 0));

        GpPostProcessor.LinkSlides(level, 6);

        Assert.Null(first.SlideTo); // never 0 ("slide into an open string"), never the -1 marker
        Assert.False(first.UnpitchedSlide);
    }

    /// <summary>The legato link survives the drop, exactly as it does for the
    /// same-fret resolution — the note degrades to a plain linked note.</summary>
    [Fact]
    public void LinkSlides_OpenLandingNote_KeepsTheLegatoLink()
    {
        var level = new ChartLevel();
        var first = Slider(2, 12, linkNext: true);
        level.Notes.Add(first);
        level.Notes.Add(Landing(2, 0));

        GpPostProcessor.LinkSlides(level, 6);

        Assert.Null(first.SlideTo);
        Assert.True(first.LinkNext, "the legato link degrades to a plain linked note");
    }

    /// <summary>A CHORD whose note on that string is open is the same landing —
    /// <c>LinkSlides</c> walks chord notes alongside single notes.</summary>
    [Fact]
    public void LinkSlides_OpenLandingChordNote_DropsTheSlide()
    {
        var level = new ChartLevel();
        var first = Slider(4, 5);
        level.Notes.Add(first);
        level.Chords.Add(new Chord
        {
            Position = new FractionalPosition(0, new Fraction(1, 2)),
            ChordNotes = { new ChordNote { StringIndex = 4, Fret = 0 }, new ChordNote { StringIndex = 3, Fret = 2 } }
        });

        GpPostProcessor.LinkSlides(level, 6);

        Assert.Null(first.SlideTo);
    }

    /// <summary>The negative case: a landing on a REAL fret still resolves, and
    /// still claims the <c>LinkNext</c> the resolved slide needs. Without this
    /// the fix above would be indistinguishable from "drop every slide".</summary>
    [Fact]
    public void LinkSlides_FrettedLanding_StillResolves()
    {
        var level = new ChartLevel();
        var first = Slider(3, 7);
        level.Notes.Add(first);
        level.Notes.Add(Landing(3, 9));

        GpPostProcessor.LinkSlides(level, 6);

        Assert.Equal(9, first.SlideTo);
        Assert.True(first.LinkNext);
    }

    // ── The MATERIALIZERS' side of the same rule ─────────────────────────────
    //
    // LinkSlides enforces "no slide lands on an open string" only while
    // RESOLVING the -1 marker. GpPostProcessor's two materializers SYNTHESIZE a
    // slide instead of resolving one, so they never pass through that check —
    // and both wrote `SlideTo = target.Fret` with no guard on the target being
    // open. A materialized `SlideTo = 0` then survives the whole pipeline for
    // exactly the reason LinkSlides' own comment gives: every later pass tests
    // `SlideTo is > 0`, so none of them sees a zero, and SngFromDomainMapper
    // sets RsNoteMask.Slide for anything that is not -1.

    /// <summary>
    ///     A GP slide-IN flag on an OPEN-string note. The sentinel is set
    ///     deliberately even on an open note (its origin is a SYNTHESIZED fret,
    ///     so the L09 "no slide FROM an open string" guard does not apply), but
    ///     the note it slides INTO is still open — and there is no fretted finish
    ///     to slide to. The sentinel must be consumed WITHOUT minting a slide.
    /// </summary>
    [Theory]
    [InlineData(GpNoteParser.SlideInFromBelow)]
    [InlineData(GpNoteParser.SlideInFromAbove)]
    public void MaterializeSlideIns_OpenTarget_MintsNoSlide(int sentinel)
    {
        var level = new ChartLevel();
        var target = new GuitarNote
        {
            Position = new FractionalPosition(4),
            Sustain = new FractionalPosition(1),
            StringIndex = 0,
            Fret = 0,
            SlideTo = sentinel
        };
        level.Notes.Add(target);

        GpPostProcessor.MaterializeSlideIns(level);

        Assert.All(level.Notes, n => Assert.True(n.SlideTo is null or > 0,
            $"a materialized slide target must be a real fret, was {n.SlideTo}"));
    }

    /// <summary>The negative case: a FRETTED slide-in target still materializes
    /// its origin note and its slide, so the guard above is not "drop every
    /// slide-in".</summary>
    [Fact]
    public void MaterializeSlideIns_FrettedTarget_StillMaterializes()
    {
        var level = new ChartLevel();
        var target = new GuitarNote
        {
            Position = new FractionalPosition(4),
            Sustain = new FractionalPosition(1),
            StringIndex = 0,
            Fret = 7,
            SlideTo = GpNoteParser.SlideInFromBelow
        };
        level.Notes.Add(target);

        GpPostProcessor.MaterializeSlideIns(level);

        var origin = Assert.Single(level.Notes, n => !ReferenceEquals(n, target));
        Assert.Equal(5, origin.Fret);
        Assert.Equal(7, origin.SlideTo);
    }

    /// <summary>A "sl." grace ornament onto an OPEN string. The guard only ever
    /// constrained the GRACE fret, never the TARGET fret, so an open landing
    /// minted <c>SlideTo = 0</c> the same way.</summary>
    [Fact]
    public void MaterializeGraceSlides_OpenTarget_MintsNoSlide()
    {
        var level = new ChartLevel();
        var target = new GuitarNote
        {
            Position = new FractionalPosition(4),
            Sustain = new FractionalPosition(1),
            StringIndex = 2,
            Fret = 0,
            GraceFret = 3,
            GraceSlide = true
        };
        level.Notes.Add(target);

        GpPostProcessor.MaterializeGraceSlides(level);

        Assert.All(level.Notes, n => Assert.True(n.SlideTo is null or > 0,
            $"a materialized grace slide target must be a real fret, was {n.SlideTo}"));
    }

    /// <summary>The negative case for the grace path.</summary>
    [Fact]
    public void MaterializeGraceSlides_FrettedTarget_StillMaterializes()
    {
        var level = new ChartLevel();
        var target = new GuitarNote
        {
            Position = new FractionalPosition(4),
            Sustain = new FractionalPosition(1),
            StringIndex = 2,
            Fret = 5,
            GraceFret = 3,
            GraceSlide = true
        };
        level.Notes.Add(target);

        GpPostProcessor.MaterializeGraceSlides(level);

        var origin = Assert.Single(level.Notes, n => !ReferenceEquals(n, target));
        Assert.Equal(3, origin.Fret);
        Assert.Equal(5, origin.SlideTo);
    }

    /// <summary>
    ///     The ORIGIN-side rule on the GP5 path: a slide OUT of an open string is
    ///     dropped, as the gpif path drops it, and the note stays. The binary reader kept
    ///     it, so a GP5 import and the same score saved by Guitar Pro 7 disagreed — the
    ///     biggest single class of the corpus round-trip diff after the meter fix.
    /// </summary>
    [Theory]
    [InlineData(0x01)] // shift
    [InlineData(0x02)] // legato
    [InlineData(0x04)] // out down
    [InlineData(0x08)] // out up
    public void Gp5_SlideOutOfAnOpenString_IsDropped(int slide)
    {
        var path = LegacyGpFixtureBuilder.WriteFile("5.10",
        [
            new FxMeasure().AddVoice(
                FxBeat.Chord(0, new FxNote(2, 0, Slide: slide)),
                FxBeat.Chord(0, new FxNote(2, 3, Slide: slide)),
                FxBeat.Note(0, 2, 5),
                FxBeat.RestBeat(0))
        ]);
        try
        {
            var importer = new GpImporter();
            var notes = Assert.Single(importer.ImportTracks(path, [.. importer.GetAvailableTracks(path).Select(t => t.Id)]))
                .Levels[0].Notes.OrderBy(n => n.Position).ToList();

            Assert.Equal([0, 3, 5], notes.Select(n => n.Fret));
            Assert.Null(notes[0].SlideTo);
            Assert.False(notes[0].UnpitchedSlide);
            Assert.NotNull(notes[1].SlideTo); // a fretted note keeps its slide
        }
        finally
        {
            File.Delete(path);
        }
    }
}
