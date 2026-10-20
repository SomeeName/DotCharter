using DotCharter.Services.Editing.HandShapes;
using DotCharter.Services.Parsing.GuitarPro;

namespace DotCharter.Tests.Services.Parsing.GuitarPro;

/// <summary>
///     #478 lifted the grouping out of <c>ChordHandShapeBuilder.BuildAndTrim</c> into
///     <see cref="ChordHandShapePlanner" /> so the editor could run it too. The import
///     is only allowed to come out the SAME: <see cref="OldBuildAndTrim" /> below is the
///     pre-#478 body verbatim (keep it dumb — a copy that "improves" makes this test
///     vacuous), and both run on clones of the same level, compared field by field —
///     hand shapes, chord template ids, sustains and the minted templates.
///     <para>
///         The end-to-end half is <c>ExportBytePinTests.GuitarProImport_IsByteIdentical…</c>,
///         whose four pins did not move with this change.
///     </para>
/// </summary>
public sealed class ChordHandShapeBuilderEquivalenceTests
{
    private const string NoCorpus = "TestData corpus not present";

    /// <summary>The byte-pin set: three gpif files and the GP3-5 binary reader.</summary>
    public static TheoryData<string> CorpusFiles =>
    [
        @"EOF\Yorushika - Fireworks Beneath My Shoes\Yorushika - Firworks Beneath My Shoes.gp",
        @"EOF\Ave Mujica - Musica Caelestis\Ave Mujica - Musica Caelestis.gp",
        @"EOF\Yorushika - Parade\Yorushika - Parade.gp",
        "TABS.gp5"
    ];

    [Theory]
    [MemberData(nameof(CorpusFiles))]
    public void OnARealImport_ThePlannerBuildsWhatTheOldBodyBuilt(string relative)
    {
        var root = FindTestDataRoot();
        var path = root == null ? null : Path.Combine(root, relative);
        Assert.SkipUnless(path != null && File.Exists(path), NoCorpus);

        var importer = new GpImporter();
        var tracks = importer.GetAvailableTracks(path);
        var result = importer.ImportTracksWithMetadata(path, tracks.Select(t => t.Id).ToList());

        var levels = 0;
        var shapes = 0;
        foreach (var arrangement in result.Arrangements)
        {
            // The import already ran the pass, so take its output back off: no
            // shapes, no templates, every chord on the sentinel. That is the state
            // the pass meets on a real import (it runs before anything else adds a
            // shape or a template).
            arrangement.ChordTemplates.Clear();
            foreach (var level in arrangement.Levels)
            {
                level.HandShapes.Clear();
                foreach (var chord in level.Chords) chord.TemplateId = -1;
            }

            foreach (var split in new[] { false, true })
            {
                for (var l = 0; l < arrangement.Levels.Count; l++)
                {
                    shapes += AssertSameOutcome(arrangement, l, split);
                    levels++;
                }
            }
        }

        Assert.True(levels > 0, "the fixture imported no levels");
        Assert.True(shapes > 0, "the fixture produced no hand shapes, so nothing was compared");
    }

    /// <summary>The same comparison on a synthetic level that always runs — repeated
    /// strums, a rest, a fret change, a technique chord, a slide landing, a melodic fill
    /// and a re-run over a level that already has its bars.</summary>
    [Fact]
    public void OnASyntheticLevel_ThePlannerBuildsWhatTheOldBodyBuilt()
    {
        var arrangement = new Arrangement { Name = "Lead", StringCount = 6 };
        arrangement.TuningPitches.Clear();
        for (var i = 0; i < 6; i++) arrangement.TuningPitches.Add(new TuningOffset(0));
        arrangement.ChordTemplates.Add(new ChordTemplate { Id = 3, Name = "prior", Frets = { [0] = 5, [1] = 7 } });
        var level = new ChartLevel();
        arrangement.Levels.Add(level);

        level.Chords.Add(Chord(0, 1, (0, 3), (1, 5)));
        level.Chords.Add(Chord(1, 1, (0, 3), (1, 5)));
        level.Chords.Add(Chord(2.5, 1, (0, 3), (1, 5))); // after a half-beat rest
        level.Chords.Add(Chord(4, 2, (0, 5), (1, 7))); // reuses "prior"
        var bend = Chord(6, 2, (0, 7), (1, 9));
        bend.ChordNotes[0].BendValues.Add(new BendValue(FractionalPosition.Zero, 1.0));
        level.Chords.Add(bend);
        level.Notes.Add(new GuitarNote { Position = new FractionalPosition(7), StringIndex = 2, Fret = 5 });
        var slider = Chord(9, 1, (0, 3), (1, 5));
        slider.ChordNotes[0].SlideTo = 5;
        level.Chords.Add(slider);
        level.Chords.Add(Chord(10, 1, (0, 5), (1, 5))); // the slide lands on string 0, fret 5
        level.Chords.Add(Chord(12, 1));                  // no notes: skipped
        level.Chords.Add(Chord(14, 1, (0, 3), (1, 5)));
        level.Chords.Add(Chord(17.5, 1, (0, 3), (1, 5))); // 2.5 beats after the last one's end

        foreach (var split in new[] { false, true })
            Assert.True(AssertSameOutcome(arrangement, 0, split) >= 5);

        // Idempotency: a second pass over a level that already has its bars adds
        // nothing, in both versions.
        var once = ChartProjectCloner.CloneArrangement(arrangement);
        ChordHandShapeBuilder.BuildAndTrim(once.Levels[0], once);
        Assert.Equal(0, AssertSameOutcome(once, 0, splitRunsOnRests: false));
    }

    /// <summary>A worktree under <c>.claude/worktrees/</c> finds the main checkout's
    /// junction by walking up, like <c>ExportBytePinTests</c>.</summary>
    private static string? FindTestDataRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "TestData");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }

        return null;
    }

    private static Chord Chord(double beat, double beats, params (int String, int Fret)[] notes)
    {
        var duration = new Fraction((int)Math.Round(beats * 4), 4);
        var chord = new Chord
        {
            Position = new FractionalPosition(0, new Fraction((int)Math.Round(beat * 4), 4)),
            NotationDuration = duration
        };
        foreach (var (s, f) in notes)
        {
            chord.ChordNotes.Add(new ChordNote
            {
                StringIndex = s, Fret = f, Sustain = new FractionalPosition(0, duration)
            });
        }

        return chord;
    }

    /// <summary>Runs the old body and the new one on two clones of
    /// <paramref name="source" /> and asserts they agree; returns the shapes added.</summary>
    private static int AssertSameOutcome(Arrangement source, int levelIndex, bool splitRunsOnRests)
    {
        var oldArr = ChartProjectCloner.CloneArrangement(source);
        var newArr = ChartProjectCloner.CloneArrangement(source);
        var shapesBefore = source.Levels[levelIndex].HandShapes.Count;

        OldBuildAndTrim(oldArr.Levels[levelIndex], oldArr, splitRunsOnRests);
        ChordHandShapeBuilder.BuildAndTrim(newArr.Levels[levelIndex], newArr, splitRunsOnRests: splitRunsOnRests);

        var oldLevel = oldArr.Levels[levelIndex];
        var newLevel = newArr.Levels[levelIndex];
        var context = $"{source.Name} level {levelIndex}, split={splitRunsOnRests}";

        Assert.True(oldLevel.HandShapes.Count == newLevel.HandShapes.Count, $"{context}: shape count");
        for (var i = 0; i < oldLevel.HandShapes.Count; i++)
        {
            var o = oldLevel.HandShapes[i];
            var n = newLevel.HandShapes[i];
            Assert.True(o.Position == n.Position && o.EndPosition == n.EndPosition &&
                        o.TemplateId == n.TemplateId && o.Name == n.Name,
                $"{context}: shape {i} differs: [{o.Position}, {o.EndPosition}) #{o.TemplateId} vs " +
                $"[{n.Position}, {n.EndPosition}) #{n.TemplateId}");
        }

        Assert.True(oldLevel.Chords.Count == newLevel.Chords.Count, $"{context}: chord count");
        for (var i = 0; i < oldLevel.Chords.Count; i++)
        {
            var o = oldLevel.Chords[i];
            var n = newLevel.Chords[i];
            Assert.True(o.TemplateId == n.TemplateId, $"{context}: chord {i} template id");
            Assert.Equal(o.ChordNotes.Select(c => c.Sustain), n.ChordNotes.Select(c => c.Sustain));
        }

        Assert.True(oldArr.ChordTemplates.Count == newArr.ChordTemplates.Count, $"{context}: template count");
        for (var i = 0; i < oldArr.ChordTemplates.Count; i++)
        {
            var o = oldArr.ChordTemplates[i];
            var n = newArr.ChordTemplates[i];
            Assert.True(o.Id == n.Id && o.Equals(n), $"{context}: template {i} differs");
        }

        return newLevel.HandShapes.Count - shapesBefore;
    }

    // ---------------------------------------------------------------------------------
    // The pre-#478 ChordHandShapeBuilder.BuildAndTrim, verbatim except: the comments
    // are dropped, the authored fingering/name parameters (null on every path this test
    // drives) are gone, and the helpers it called are inlined below it. KEEP IT DUMB.
    // ---------------------------------------------------------------------------------

    private static readonly FractionalPosition MaxMergeGap = new(2);

    private static void OldBuildAndTrim(ChartLevel level, Arrangement arrangement, bool splitRunsOnRests)
    {
        var chords = new List<Chord>(level.Chords);
        chords.Sort((l, r) => l.Position.CompareTo(r.Position));

        var coverage = new HandShapeCoverageIndex(level.HandShapes);

        var nextTemplateId = arrangement.ChordTemplates.Count == 0
            ? 0
            : arrangement.ChordTemplates.Max(t => t.Id) + 1;
        var notePositions = level.Notes.Select(n => n.Position).OrderBy(p => p).ToList();

        var slideLandings = new HashSet<(FractionalPosition Pos, int Str, int Fret)>();
        foreach (var ch in level.Chords)
        {
            foreach (var cn in ch.ChordNotes)
            {
                if (cn.SlideTo is > 0)
                    slideLandings.Add((ch.Position + cn.Sustain, cn.StringIndex, cn.SlideTo.Value));
            }
        }

        foreach (var n in level.Notes)
        {
            if (n.SlideTo is > 0)
                slideLandings.Add((n.Position + n.Sustain, n.StringIndex, n.SlideTo.Value));
        }

        bool IsSlideLanding(Chord chord)
        {
            foreach (var cn in chord.ChordNotes)
            {
                if (slideLandings.Contains((chord.Position, cn.StringIndex, cn.Fret)))
                    return true;
            }

            return false;
        }

        for (var i = 0; i < chords.Count; i++)
        {
            var first = chords[i];
            if (first.ChordNotes.Count == 0) continue;
            if (coverage.Covers(first.Position)) continue;

            var template = OldFindOrCreateTemplate(arrangement, first, ref nextTemplateId);
            var run = new List<Chord> { first };

            if (!OldHasSustainVisibleTechnique(first))
            {
                while (i + 1 < chords.Count)
                {
                    var prev = run[^1];
                    var next = chords[i + 1];
                    if (next.ChordNotes.Count == 0) break;
                    if (coverage.Covers(next.Position)) break;
                    if (OldHasSustainVisibleTechnique(next)) break;
                    if (!OldFretsMatch(template, next)) break;

                    var prevEnd = prev.Position + OldAuthoredSpan(prev);
                    if (splitRunsOnRests && next.Position.CompareTo(prevEnd) > 0) break;
                    if (next.Position.CompareTo(prevEnd + MaxMergeGap) > 0) break;
                    if (OldAnyNoteBetween(notePositions, prev.Position, next.Position)) break;

                    run.Add(next);
                    i++;
                }
            }

            var last = run[^1];
            var end = last.Position + OldAuthoredSpan(last);

            var clip = end;
            if (i + 1 < chords.Count && chords[i + 1].Position.CompareTo(clip) < 0)
                clip = chords[i + 1].Position;
            var nextNote = OldFirstPositionAfter(notePositions, last.Position);
            if (nextNote is { } np && np.CompareTo(clip) < 0)
                clip = np;
            if (clip.CompareTo(first.Position) > 0)
                end = clip;

            level.HandShapes.Add(new HandShape
            {
                Position = first.Position,
                EndPosition = end,
                TemplateId = template.Id,
                Name = string.Empty
            });
            coverage.Add(first.Position, end);

            foreach (var chord in run)
                chord.TemplateId = template.Id;

            foreach (var chord in run)
            {
                if (!OldHasSustainVisibleTechnique(chord) && !IsSlideLanding(chord))
                {
                    foreach (var cn in chord.ChordNotes)
                        cn.Sustain = FractionalPosition.Zero;
                }
            }
        }
    }

    private static FractionalPosition? OldFirstPositionAfter(List<FractionalPosition> sorted, FractionalPosition after)
    {
        int lo = 0, hi = sorted.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) >> 1;
            if (sorted[mid].CompareTo(after) <= 0) lo = mid + 1;
            else hi = mid;
        }

        return lo < sorted.Count ? sorted[lo] : null;
    }

    private static bool OldAnyNoteBetween(List<FractionalPosition> sortedNotePositions,
        FractionalPosition from, FractionalPosition to)
    {
        int lo = 0, hi = sortedNotePositions.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) >> 1;
            if (sortedNotePositions[mid].CompareTo(from) <= 0) lo = mid + 1;
            else hi = mid;
        }

        return lo < sortedNotePositions.Count && sortedNotePositions[lo].CompareTo(to) < 0;
    }

    private static bool OldHasSustainVisibleTechnique(Chord chord)
    {
        foreach (var cn in chord.ChordNotes)
        {
            if (cn.BendValues.Count > 0 || cn.Tremolo || cn.Vibrato ||
                cn.SlideTo.HasValue || cn.UnpitchedSlide)
            {
                return true;
            }
        }

        return false;
    }

    private static FractionalPosition OldAuthoredSpan(Chord chord)
    {
        if (chord.NotationDuration is { Numerator: > 0 } nd)
            return new FractionalPosition(0, nd);
        return chord.GetMaxSustain();
    }

    private static ChordTemplate OldFindOrCreateTemplate(Arrangement arrangement, Chord chord, ref int nextTemplateId)
    {
        foreach (var t in arrangement.ChordTemplates)
        {
            if (!t.Arpeggio && OldFretsMatch(t, chord))
                return t;
        }

        var template = new ChordTemplate { Name = string.Empty };
        foreach (var cn in chord.ChordNotes)
            template.Frets[cn.StringIndex] = cn.Fret;

        ChordTemplateFingerSetter.SetSuggestedFingers(template, arrangement.StringCount);
        template.Name = ChordNameSuggester.SuggestFirstChordName(arrangement, template.Frets);

        template.Id = nextTemplateId++;
        arrangement.ChordTemplates.Add(template);
        return template;
    }

    private static bool OldFretsMatch(ChordTemplate template, Chord chord)
    {
        if (template.Frets.Count != chord.ChordNotes.Count) return false;
        foreach (var cn in chord.ChordNotes)
        {
            if (!template.Frets.TryGetValue(cn.StringIndex, out var fret) || fret != cn.Fret)
                return false;
        }

        return true;
    }
}
