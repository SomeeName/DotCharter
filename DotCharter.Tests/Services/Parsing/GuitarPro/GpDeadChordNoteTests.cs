using DotCharter.Services.Parsing.GuitarPro;
using DotCharter.Services.Parsing.Rscp;

namespace DotCharter.Tests.Services.Parsing.GuitarPro;

/// <summary>
///     The two halves of the dead-note import rule.
///     <para>
///     PARTIAL dead (owner report 2026-07-18, real tab): GP stores TAB-ARBITRARY
///     frets on dead ("x") chord notes — an 11-x-11 octave shape arrived as
///     11-1-11 and the fret-1 ghost poisoned the template, the auto-fingering and
///     the generated anchor. Such a note is REMOVED.
///     </para>
///     <para>
///     FULLY dead (owner report 2026-07-20, verified against EOF's and Charter's
///     RS2 output for the same source file): a fret-hand-muted strum KEEPS its
///     authored shape. Before v0.33.2 every fret was zeroed behind a default-on
///     <c>DeadStrumsAsOpenMutes</c> setting, so a 3-5-5 muted chord exported as
///     <c>fret="0"</c> on every string and got its own bogus all-open template
///     instead of reusing the neighbouring chords'. The setting is gone;
///     preserving the shape is unconditional.
///     </para>
/// </summary>
public sealed class GpDeadChordNoteTests
{
    private static ChartLevel LevelWithChord(params (int stringIdx, int fret, MuteType mute)[] notes)
    {
        var level = new ChartLevel();
        level.Chords.Add(ChordAt(4, notes));
        return level;
    }

    private static Chord ChordAt(int beat, params (int stringIdx, int fret, MuteType mute)[] notes)
    {
        var chord = new Chord
        {
            Position = new FractionalPosition(beat),
            NotationDuration = new Fraction(1, 1)
        };
        foreach (var (s, f, m) in notes)
        {
            chord.ChordNotes.Add(new ChordNote
            {
                StringIndex = s,
                Fret = f,
                Mute = m,
                Sustain = new FractionalPosition(1)
            });
        }

        return chord;
    }

    private static Arrangement Arr()
    {
        var arr = new Arrangement { Name = "Lead", StringCount = 6 };
        arr.TuningPitches.Clear();
        for (var i = 0; i < 6; i++) arr.TuningPitches.Add(new TuningOffset(0));
        return arr;
    }

    // ── partial dead: unchanged behaviour ────────────────────────────────────

    [Fact]
    public void DeadNote_InsideASoundingChord_IsRemoved()
    {
        var level = LevelWithChord(
            (5, 11, MuteType.None), (4, 1, MuteType.Full), (3, 11, MuteType.None));

        GpPostProcessor.CleanDeadChordNotes(level);

        var notes = level.Chords[0].ChordNotes;
        Assert.Equal(2, notes.Count);
        Assert.All(notes, cn => Assert.Equal(11, cn.Fret));
        Assert.All(notes, cn => Assert.Equal(MuteType.None, cn.Mute));
    }

    [Fact]
    public void PalmMutedNote_IsNotADeadNote_AndStays()
    {
        var level = LevelWithChord(
            (5, 11, MuteType.None), (4, 9, MuteType.Palm), (3, 11, MuteType.None));

        GpPostProcessor.CleanDeadChordNotes(level);

        Assert.Equal(3, level.Chords[0].ChordNotes.Count);
    }

    [Fact]
    public void ChordWithoutDeadNotes_IsUntouched()
    {
        var level = LevelWithChord(
            (5, 5, MuteType.None), (4, 7, MuteType.None));

        GpPostProcessor.CleanDeadChordNotes(level);

        Assert.Equal(2, level.Chords[0].ChordNotes.Count);
        Assert.Equal(5, level.Chords[0].ChordNotes[0].Fret);
        Assert.Equal(7, level.Chords[0].ChordNotes[1].Fret);
    }

    // ── fully dead: the v0.33.2 fix ──────────────────────────────────────────

    [Fact]
    public void FullyDeadStrum_KeepsItsFrets_AndStaysFullyMuted()
    {
        // The exact shape from the owner's score.gpif: string 1 fret 3,
        // string 2 fret 5, string 3 fret 5 — all x.
        var level = LevelWithChord(
            (1, 3, MuteType.Full), (2, 5, MuteType.Full), (3, 5, MuteType.Full));

        GpPostProcessor.CleanDeadChordNotes(level);

        var notes = level.Chords[0].ChordNotes;
        Assert.Equal(3, notes.Count);
        Assert.Equal(new[] { 3, 5, 5 }, notes.Select(cn => cn.Fret).ToArray());
        Assert.All(notes, cn => Assert.Equal(MuteType.Full, cn.Mute));
    }

    [Fact]
    public void FullyDeadStrum_ThroughTheWholePipeline_KeepsItsShape()
    {
        var arr = Arr();
        var level = new ChartLevel();
        level.Chords.Add(ChordAt(0, (1, 3, MuteType.Full), (2, 5, MuteType.Full), (3, 5, MuteType.Full)));

        GpPostProcessor.Run(level, arr);

        var chord = Assert.Single(level.Chords);
        Assert.Equal(new[] { 3, 5, 5 }, chord.ChordNotes.Select(cn => cn.Fret).ToArray());
        Assert.True(chord.IsFullyMuted());

        // The auto-created template describes the real shape, not an all-open one.
        var template = Assert.Single(arr.ChordTemplates);
        Assert.Equal(3, template.Frets[1]);
        Assert.Equal(5, template.Frets[2]);
        Assert.Equal(5, template.Frets[3]);
    }

    [Fact]
    public void FullyDeadStrum_ResolvesToTheSameTemplateAsAnIdenticalUnmutedChord()
    {
        // What EOF does and DotCharter used not to: the muted strum reuses the
        // surrounding chords' chordId because it still carries their frets.
        var arr = Arr();
        var level = new ChartLevel();
        level.Chords.Add(ChordAt(0, (1, 3, MuteType.None), (2, 5, MuteType.None), (3, 5, MuteType.None)));
        level.Chords.Add(ChordAt(4, (1, 3, MuteType.Full), (2, 5, MuteType.Full), (3, 5, MuteType.Full)));
        arr.Levels.Add(level);

        GpPostProcessor.Run(level, arr);

        // Import-time find-or-create matches on frets alone ⇒ ONE template.
        Assert.Single(arr.ChordTemplates);

        // …and the save-time template assignment lands both chords on it.
        new RscpTemplateManager().RegenerateTemplates(arr);

        var unmuted = level.Chords.Single(c => !c.IsFullyMuted());
        var muted = level.Chords.Single(c => c.IsFullyMuted());
        Assert.Equal(unmuted.TemplateId, muted.TemplateId);
        Assert.True(muted.TemplateId >= 0);
    }
}
