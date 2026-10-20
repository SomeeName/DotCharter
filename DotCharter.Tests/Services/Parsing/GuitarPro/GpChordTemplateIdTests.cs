using DotCharter.Services.Editing.FretAnchors;
using DotCharter.Services.Parsing.GuitarPro;

namespace DotCharter.Tests.Services.Parsing.GuitarPro;

/// <summary>
///     An imported chord must NAME the template it plays.
///     <para>
///     Until 2026-08-22 the tab importers set <c>TemplateId</c> on the HAND SHAPE
///     only and left every chord on the <c>-1</c> sentinel until a save
///     round-trip (<c>RscpTemplateManager</c>) filled it in. Anchors are
///     generated DURING the import, and both of the things that read a chord's
///     fingering go through that id:
///     </para>
///     <list type="bullet">
///     <item><see cref="FretAnchorGenerationService" /> — <c>chordTemplates[chord.TemplateId]</c>,
///     so with <c>-1</c> every chord note arrived with finger <c>-1</c> and the
///     whole fingering-aware root logic (P4/P5, whose own comment says it stops
///     "a [5..8] window absorbing a finger-1-on-6 chord") was DEAD on every tab
///     import.</item>
///     <item><c>IndexFingerAnchorRule</c> (E04) — <c>chord.TemplateId &lt; 0 ? null : …</c>,
///     then <c>continue</c>. So the detector for the exact fault the generator
///     was producing skipped every chord too.</item>
///     </list>
///     <para>
///     One cause, both symptoms, which is what the owner's report described:
///     a {5,7,7,6,5} barre fingered 1 at fret 5 sat under an anchor on fret 4 —
///     a fret-4 window still "covers" frets 5–7 — and the issues tab said
///     nothing. Measured on that file: 474 Lead chords and 1,170 Rhythm chords
///     carried no template id before, 0 after.
///     </para>
/// </summary>
public sealed class GpChordTemplateIdTests
{
    private static Chord Chord(int beat, params (int String, int Fret)[] notes)
    {
        var chord = new Chord
        {
            Position = new FractionalPosition(beat), NotationDuration = new Fraction(1, 1)
        };
        foreach (var (str, fret) in notes)
        {
            chord.ChordNotes.Add(new ChordNote
            { StringIndex = str, Fret = fret, Sustain = new FractionalPosition(1) });
        }

        return chord;
    }

    [Fact]
    public void EveryImportedChord_NamesTheTemplateItPlays()
    {
        var arr = new Arrangement();
        var level = new ChartLevel();
        level.Chords.Add(Chord(0, (4, 5), (3, 6), (2, 7), (1, 7), (0, 5)));
        level.Chords.Add(Chord(4, (1, 4), (2, 6), (3, 4)));

        ChordHandShapeBuilder.BuildAndTrim(level, arr);

        Assert.All(level.Chords, c =>
            Assert.True(c.TemplateId >= 0, $"chord at {c.Position} still carries the -1 sentinel"));

        // …and it names the RIGHT one: the template's frets are the chord's.
        foreach (var chord in level.Chords)
        {
            var template = arr.ChordTemplates.Single(t => t.Id == chord.TemplateId);
            foreach (var cn in chord.ChordNotes)
                Assert.Equal(cn.Fret, template.Frets[cn.StringIndex]);
        }
    }

    [Fact]
    public void TwoChordsWithTheSameShape_ShareOneTemplateId()
    {
        // Templates are shared and deduped by shape; naming them per chord must
        // not start minting duplicates.
        var arr = new Arrangement();
        var level = new ChartLevel();
        level.Chords.Add(Chord(0, (1, 4), (2, 6)));
        level.Chords.Add(Chord(8, (1, 4), (2, 6)));

        ChordHandShapeBuilder.BuildAndTrim(level, arr);

        // The >= 0 matters: two chords both left on the -1 sentinel are also
        // "equal", so without it this passes against the very bug it guards.
        Assert.True(level.Chords[0].TemplateId >= 0);
        Assert.Equal(level.Chords[0].TemplateId, level.Chords[1].TemplateId);
        Assert.Single(arr.ChordTemplates);
    }

    /// <summary>
    ///     The behavioural half, with the owner's actual shape: a fret-4 anchor's
    ///     window covers frets 4–7, so a {5,7,7,6,5} barre fingered 1 at fret 5
    ///     "fits" it — and the anchor must move anyway, because the FHP fret IS
    ///     the index finger's fret (Rocksmith derives the playing finger from it).
    /// </summary>
    [Fact]
    public void AStillCoveringWindow_DoesNotAbsorbAFingerOneChordAboveIt()
    {
        var arr = new Arrangement();
        var level = new ChartLevel();

        var low = new ChordTemplate { Id = 0 };
        foreach (var (s, f, finger) in new[] { (1, 4, 1), (2, 6, 3), (3, 4, 1), (4, 5, 2), (5, 4, 1) })
        {
            low.Frets[s] = f;
            low.Fingers[s] = finger;
        }

        var barre = new ChordTemplate { Id = 1 };
        foreach (var (s, f, finger) in new[] { (0, 5, 1), (1, 7, 3), (2, 7, 4), (3, 6, 2), (4, 5, 1) })
        {
            barre.Frets[s] = f;
            barre.Fingers[s] = finger;
        }

        arr.ChordTemplates.Add(low);
        arr.ChordTemplates.Add(barre);

        var first = Chord(0, (1, 4), (2, 6), (3, 4), (4, 5), (5, 4));
        first.TemplateId = low.Id;
        var second = Chord(4, (0, 5), (1, 7), (2, 7), (3, 6), (4, 5));
        second.TemplateId = barre.Id;
        level.Chords.Add(first);
        level.Chords.Add(second);

        var anchors = new FretAnchorGenerationService()
            .GenerateAnchors(level, null, null, arr.ChordTemplates, arr.Capo, false)
            .OrderBy(a => a.Position)
            .ToList();

        var covering = anchors.Last(a => a.Position.CompareTo(second.Position) <= 0);
        Assert.Equal(5, covering.Fret);
    }
}
