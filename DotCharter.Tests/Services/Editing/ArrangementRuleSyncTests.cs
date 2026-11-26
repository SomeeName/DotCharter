using DotCharter.Services.Editing.Sync;
using DotCharter.Tests.Services.Editing.Maintenance;

namespace DotCharter.Tests.Services.Editing;

/// <summary>
///     #914: a reduced 6-string rhythm part re-voices every low power chord of its 7-string
///     source (B5 = B1+F#2 becomes F#2+B2), so pitch matching finds nothing — but the same
///     source chord always becomes the same target voicing. The hand work authored on ONE
///     target occurrence of a mapping is carried to every other occurrence of it.
/// </summary>
public sealed class ArrangementRuleSyncTests
{
    private static readonly int[] DropA7 = [-2, 0, 0, 0, 0, 0, 0]; // A1 E2 A2 D3 G3 B3 E4
    private static readonly int[] DropD6 = [-2, 0, 0, 0, 0, 0];    // D2 A2 D3 G3 B3 E4

    // Source power chord on strings 0+1 at fret f (A1/E2) -> target fifth-in-the-bass on
    // strings 0+1 (D2/A2): root fret f, fifth fret f+2 on D2.
    private static readonly (int Fret, int TargetFifth, int TargetRoot)[] Chords = [(2, 4, 2), (3, 5, 3), (4, 6, 4)];

    [Fact]
    public void APinnedTargetOccurrence_IsNeverOverwritten_AnUnpinnedControlIs()
    {
        foreach (var pinned in new[] { true, false })
        {
            var (source, target) = Pair();
            var level = target.Levels[0];
            var generated = target.FindChordTemplate(level.Chords[0].TemplateId)!;
            generated.Pinned = pinned;
            var authored = Author(target, 3, fingers: [(0, 4), (1, 1)], anchorFret: 1);

            ArrangementSoundSync.Apply(source, target);

            var onAuthored = level.Chords.Count(c => c.TemplateId == authored.Id);
            Assert.Equal(pinned ? 1 : 4, onAuthored);
            Assert.Equal(pinned, level.Chords[0].TemplateId == generated.Id);
        }
    }

    [Fact]
    public void AuthoringOneOccurrence_CarriesToEveryOtherOccurrenceOfThatMapping_NotesNeverChange()
    {
        var (source, target) = Pair();
        var level = target.Levels[0];
        var notesBefore = Sounds(target);
        var generatedB5 = target.FindChordTemplate(level.Chords[0].TemplateId)!;
        var generatedBefore = generatedB5.Clone();

        // The user charts the B5 at beat 3: a different fingering (chord AND bar, as the
        // chord box does) and a fret-hand position one fret lower.
        var authored = Author(target, 3, fingers: [(0, 4), (1, 1)], anchorFret: 1);

        var preview = ArrangementSoundSync.Preview(source, target);
        Assert.Equal(notesBefore, Sounds(target));
        Assert.Equal(1, level.Chords.Count(c => c.TemplateId == authored.Id));

        var summary = ArrangementSoundSync.Apply(source, target);

        Assert.True(summary.ByPitch);
        Assert.Equal(0, summary.SharedSounds);
        Assert.Equal(3, summary.RuleMappings);
        Assert.Equal((3, 3, 3, 0), (summary.RuleFingerings, summary.RuleHandShapes, summary.RuleAnchors,
            summary.RuleConflicts));
        Assert.Equal(summary, preview);
        Assert.True(summary.WouldChangeAnything);

        // Every B5 now plays the authored grip, bar and position; C5 and C#5 are untouched.
        foreach (var chord in level.Chords)
        {
            var isB5 = chord.ChordNotes.Any(n => n.StringIndex == 1 && n.Fret == 2);
            Assert.Equal(isB5, chord.TemplateId == authored.Id);
            var bar = level.HandShapes.Single(h => h.Covers(chord.Position));
            Assert.Equal(chord.TemplateId, bar.TemplateId);
            var anchor = level.Anchors.Single(a => a.Position == chord.Position);
            Assert.Equal(isB5 ? 1 : chord.ChordNotes.Min(n => n.Fret), anchor.Fret);
        }

        Assert.Equal(notesBefore, Sounds(target));
        Assert.Equal(generatedBefore, generatedB5);
        Assert.Equal(generatedBefore.Fingers, generatedB5.Fingers);

        // Run again: nothing left to carry.
        Assert.False(ArrangementSoundSync.Preview(source, target).WouldChangeAnything);
    }

    [Fact]
    public void ConflictingAuthoredVariants_CopyNothing_AndAreReported()
    {
        var (source, target) = Pair();
        var level = target.Levels[0];
        Author(target, 3, fingers: [(0, 4), (1, 1)], anchorFret: 1);
        Author(target, 6, fingers: [(0, 4), (1, 2)], anchorFret: 3);
        var before = level.Chords.Select(c => c.TemplateId).ToArray();
        var anchorsBefore = level.Anchors.Select(a => a.Fret).ToArray();

        var summary = ArrangementSoundSync.Apply(source, target);

        Assert.Equal((0, 0, 0), (summary.RuleFingerings, summary.RuleHandShapes, summary.RuleAnchors));
        Assert.Equal(3, summary.RuleConflicts);
        Assert.False(summary.WouldChangeAnything);
        Assert.Equal(before, level.Chords.Select(c => c.TemplateId).ToArray());
        Assert.Equal(anchorsBefore, level.Anchors.Select(a => a.Fret).ToArray());
    }

    [Fact]
    public void AnAnchorOverOtherSounds_StaysWhereItIs()
    {
        var (source, target) = Pair();
        var level = target.Levels[0];
        Author(target, 3, fingers: [(0, 4), (1, 1)], anchorFret: 1);

        // The B5 at beat 6 shares its window with a single note: the hand may not sit there.
        level.Notes.Add(new GuitarNote { Position = new FractionalPosition(6, new Fraction(1, 2)), StringIndex = 3, Fret = 7 });

        var summary = ArrangementSoundSync.Apply(source, target);

        Assert.Equal(2, summary.RuleAnchors);
        Assert.Equal(2, level.Anchors.Single(a => a.Position == new FractionalPosition(6)).Fret);
    }

    [Fact]
    public void SameStringsAndTuning_HasNoRulePass()
    {
        var a = Arr(DropD6);
        var b = Arr(DropD6);
        AddChord(a, 0, (0, 4), (1, 2));
        AddChord(b, 0, (0, 5), (1, 3));

        var summary = ArrangementSoundSync.Preview(a, b);

        Assert.False(summary.ByPitch);
        Assert.Equal(0, summary.RuleMappings);
    }

    [Fact]
    public void TheRuleAndPitchPasses_AreOneUndoStep()
    {
        var h = new ChartPowerToolsTests.Harness();
        var project = h.State.ActiveProject!;
        var (source, target) = Pair();
        var authored = Author(target, 3, fingers: [(0, 4), (1, 1)], anchorFret: 1);
        project.Arrangements.Add(source);
        project.Arrangements.Add(target);
        var targetIndex = project.Arrangements.IndexOf(target);
        var before = target.Levels[0].Chords.Select(c => c.TemplateId).ToArray();

        var summary = h.Maintenance.SyncArrangement(source, target);

        Assert.Equal(3, summary.RuleFingerings);
        var synced = project.Arrangements[targetIndex];
        Assert.Equal(4, synced.Levels[^1].Chords.Count(c => c.TemplateId == authored.Id));

        h.History.Undo();

        var restored = h.State.ActiveProject!.Arrangements[targetIndex];
        Assert.Equal(before, restored.Levels[^1].Chords.Select(c => c.TemplateId).ToArray());
        Assert.Equal(1, restored.Levels[^1].Anchors.Count(a => a.Fret == 1));
    }

    // ---- fixtures ----------------------------------------------------------

    /// <summary>
    ///     B5, C5, C#5 four times each (beats 0..11), each in the target as the importer
    ///     leaves it: one shared suggested-fingering template per voicing, a bar over the
    ///     chord, an anchor at its root fret.
    /// </summary>
    private static (Arrangement Source, Arrangement Target) Pair()
    {
        var source = Arr(DropA7);
        var target = Arr(DropD6);
        var templates = new Dictionary<int, ChordTemplate>();
        for (var beat = 0; beat < 12; beat++)
        {
            var (fret, fifth, root) = Chords[beat % 3];
            AddChord(source, beat, (0, fret), (1, fret));
            if (!templates.TryGetValue(fret, out var template))
            {
                template = new ChordTemplate { Id = templates.Count, Frets = { [0] = fifth, [1] = root } };
                ChordTemplateFingerSetter.SetSuggestedFingers(template, 6);
                target.ChordTemplates.Add(template);
                templates[fret] = template;
            }

            AddChord(target, beat, (0, fifth), (1, root)).TemplateId = template.Id;
            target.Levels[0].HandShapes.Add(new HandShape
            {
                Position = new FractionalPosition(beat), EndPosition = new FractionalPosition(beat + 1),
                TemplateId = template.Id
            });
            target.Levels[0].Anchors.Add(new FretAnchor(new FractionalPosition(beat), root, 4));
        }

        return (source, target);
    }

    /// <summary>What the user does on one target occurrence: a new grip on chord and bar, and an anchor fret.</summary>
    private static ChordTemplate Author(Arrangement target, int beat, (int S, int F)[] fingers, int anchorFret)
    {
        var level = target.Levels[0];
        var chord = level.Chords.Single(c => c.Position == new FractionalPosition(beat));
        var grip = target.FindChordTemplate(chord.TemplateId)!.Clone();
        grip.Id = target.ChordTemplates.Max(t => t.Id) + 1;
        grip.Fingers.Clear();
        foreach (var (s, f) in fingers) grip.Fingers[s] = f;
        Assert.NotEqual(target.FindChordTemplate(chord.TemplateId), grip);
        target.ChordTemplates.Add(grip);
        chord.TemplateId = grip.Id;
        level.HandShapes.Single(h => h.Covers(chord.Position)).TemplateId = grip.Id;
        level.Anchors.Single(a => a.Position == chord.Position).Fret = anchorFret;
        return grip;
    }

    private static string Sounds(Arrangement arrangement) => string.Join(";",
        arrangement.Levels[0].Chords.Select(c =>
            $"{c.Position}:{string.Join(",", c.ChordNotes.Select(n => $"{n.StringIndex}/{n.Fret}"))}")
            .Concat(arrangement.Levels[0].Notes.Select(n => $"{n.Position}:{n.StringIndex}/{n.Fret}")));

    private static Arrangement Arr(int[] offsets)
    {
        var arrangement = new Arrangement
        {
            Name = $"{offsets.Length}", ArrangementType = ArrangementType.Rhythm, StringCount = offsets.Length
        };
        foreach (var step in offsets) arrangement.TuningPitches.Add(new TuningOffset(step));
        arrangement.Levels.Add(new ChartLevel());
        return arrangement;
    }

    private static Chord AddChord(Arrangement arrangement, int beat, params (int StringIndex, int Fret)[] notes)
    {
        var chord = new Chord { Position = new FractionalPosition(beat), TemplateId = -1 };
        foreach (var (stringIndex, fret) in notes)
            chord.ChordNotes.Add(new ChordNote { StringIndex = stringIndex, Fret = fret });
        arrangement.Levels[0].Chords.Add(chord);
        return chord;
    }
}
