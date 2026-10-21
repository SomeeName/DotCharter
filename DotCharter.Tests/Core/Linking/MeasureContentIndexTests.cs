using DotCharter.Core.Linking;
using DotCharter.Services.Editing.Collisions;
using DotCharter.Services.Parsing.Rscp;

namespace DotCharter.Tests.Core.Linking;

/// <summary>
///     The shared measure-content comparison: equal content gives equal keys whatever
///     the tempo map and whatever the clean-up passes did near the run's edges, and any
///     change to what is played gives a different key.
/// </summary>
public sealed class MeasureContentIndexTests
{
    public static TheoryData<int, MinNoteDistanceMode, double, int> TempoMaps()
    {
        var data = new TheoryData<int, MinNoteDistanceMode, double, int>();
        for (var seed = 1; seed <= 12; seed++)
        {
            data.Add(seed, MinNoteDistanceMode.MeasureFraction, 10, 64);
            data.Add(seed, MinNoteDistanceMode.MeasureFraction, 10, 16);
            data.Add(seed, MinNoteDistanceMode.Milliseconds, 10, 64);
            data.Add(seed, MinNoteDistanceMode.Milliseconds, 80, 64);
            data.Add(seed, MinNoteDistanceMode.BeatFraction, 10, 8);
        }

        return data;
    }

    /// <summary>Random tempo per bar plus up to ±8 % drift per beat, every spacing mode:
    /// the three copies of the pattern keep one key after the enforcer ran.</summary>
    [Theory]
    [MemberData(nameof(TempoMaps))]
    public void EqualContent_KeepsOneKey_OnAnyTempoMap_AfterCleanUp(int seed, MinNoteDistanceMode mode, double ms, int divisor)
    {
        var project = LinkFixture.Project(seed);
        var config = LinkFixture.Config(mode, ms, divisor);
        ChartSpacingEnforcer.EnforceProject(project, config);

        var index = LinkFixture.Index(project, config);
        var keys = LinkFixture.PatternMeasures.Select(m => index.KeyFor(m)).ToList();

        Assert.All(keys, k => Assert.Equal(5, k.SoundCount));
        Assert.Equal(keys[0], keys[1]);
        Assert.Equal(keys[0], keys[2]);
        Assert.Equal(keys[0].Hash, keys[2].Hash);
        Assert.NotEqual(keys[0], index.KeyFor(0));
    }

    /// <summary>
    ///     Non-vacuity: the clean-up passes really did make the stored copies differ, and it is
    ///     the spacing gap that absorbs it. Told the gap is zero, the rule no longer sees the
    ///     copies as equal on any tempo map.
    /// </summary>
    [Fact]
    public void WithoutTheGap_TheCleanedUpCopiesDiffer()
    {
        var strict = MeasureContentOptions.Detection with
        {
            Spacing = new SpacingGap(SpacingGapMode.Milliseconds, 64, 0),
            AnchorSnapToleranceMs = 0
        };
        for (var seed = 1; seed <= 12; seed++)
        {
            var project = LinkFixture.Project(seed);
            ChartSpacingEnforcer.EnforceProject(project, LinkFixture.Config(MinNoteDistanceMode.Milliseconds, 40));
            var index = LinkFixture.Index(project, strict);

            Assert.NotEqual(index.KeyFor(1), index.KeyFor(4));
            Assert.NotEqual(index.KeyFor(1), index.KeyFor(7));
        }
    }

    /// <summary>Before any clean-up, the stored copies are identical in beats: a tempo map
    /// alone never changes a key.</summary>
    [Fact]
    public void TempoAlone_NeverChangesAKey()
    {
        var steady = LinkFixture.Index(LinkFixture.Project(seed: null));
        var drifting = LinkFixture.Index(LinkFixture.Project(seed: 7));

        Assert.Equal(steady.KeyFor(1), drifting.KeyFor(1));
        Assert.Equal(steady.KeyFor(7), drifting.KeyFor(7));
    }

    public static TheoryData<string> Edits() =>
    [
        "fret", "string", "technique", "bend", "sustain", "chord shape", "chord finger", "note finger",
        "anchor", "hand shape", "extra note", "fret-hand mute", "position"
    ];

    /// <summary>Every kind of content the rule claims to compare does change the key.</summary>
    [Theory]
    [MemberData(nameof(Edits))]
    public void AnyEditInsideTheRun_ChangesTheKey(string edit)
    {
        var project = LinkFixture.Project(seed: 3);
        var config = LinkFixture.Config(MinNoteDistanceMode.MeasureFraction);
        ChartSpacingEnforcer.EnforceProject(project, config);
        var before = LinkFixture.Index(project, config).KeyFor(4);

        var level = project.Arrangements[0].Levels[0];
        var inRun = level.Notes.Where(n => n.Position >= LinkFixture.At(4, 0) && n.Position < LinkFixture.At(5, 0))
            .OrderBy(n => n.Position).ToList();
        var chord = level.Chords.Single(c => c.Position == LinkFixture.At(4, 2));
        switch (edit)
        {
            case "fret": inRun[0].Fret = 6; break;
            case "string": inRun[0].StringIndex = 5; break;
            case "technique": inRun[0].Mute = MuteType.Palm; break;
            case "bend": inRun[1].BendValues[0].Step = 2; break;
            case "sustain": inRun[0].Sustain = LinkFixture.Beats(0.25); break;
            case "note finger": inRun[1].LeftHandFinger = 2; break;
            case "extra note": level.Notes.Add(LinkFixture.Note(LinkFixture.At(4, 0.5), 5, 0, 0)); break;
            case "position": inRun[1].Position = LinkFixture.At(4, 1.5); inRun[1].BendValues.Clear(); break;
            case "fret-hand mute": level.FretHandMutes.Add(new FretHandMute(LinkFixture.At(4, 1))); break;
            case "anchor": level.Anchors.Single(a => a.Position == LinkFixture.At(4, 2)).Fret = 7; break;
            case "hand shape": level.HandShapes.Single(h => h.Position == LinkFixture.At(4, 2)).EndPosition = LinkFixture.At(4, 2.5); break;
            case "chord shape":
                project.Arrangements[0].ChordTemplates.Add(new ChordTemplate { Id = 1, Frets = new() { [1] = 7, [2] = 9 } });
                chord.TemplateId = 1;
                break;
            case "chord finger":
                project.Arrangements[0].ChordTemplates.Add(new ChordTemplate
                {
                    Id = 1, Frets = new() { [1] = 7, [2] = 9, [3] = 9 }, Fingers = new() { [1] = 1, [2] = 3, [3] = 3 }
                });
                chord.TemplateId = 1;
                break;
        }

        ChartSpacingEnforcer.EnforceProject(project, config);
        Assert.NotEqual(before, LinkFixture.Index(project, config).KeyFor(4));
    }

    /// <summary>A template is compared by its shape, not by its id or name: two ids with one
    /// shape (another arrangement's numbering) compare equal.</summary>
    [Fact]
    public void ChordTemplates_CompareByShape_NotIdOrName()
    {
        var project = LinkFixture.Project(seed: null);
        var arrangement = project.Arrangements[0];
        var before = LinkFixture.Index(project).KeyFor(4);
        arrangement.ChordTemplates.Add(new ChordTemplate
        {
            Id = 9, Name = "renamed", Frets = new() { [1] = 7, [2] = 9, [3] = 9 }, Fingers = new() { [1] = 1, [2] = 3, [3] = 4 }
        });
        arrangement.Levels[0].Chords.Single(c => c.Position == LinkFixture.At(4, 2)).TemplateId = 9;
        arrangement.Levels[0].HandShapes.Single(h => h.Position == LinkFixture.At(4, 2)).TemplateId = 9;

        Assert.Equal(before, LinkFixture.Index(project).KeyFor(4));
    }

    /// <summary>The time signature is part of the content: one beat more, never equal.</summary>
    [Fact]
    public void ADifferentTimeSignature_NeverCompares()
    {
        var project = LinkFixture.Project(seed: null);
        var beats = project.Beats;
        beats[16].BeatsInMeasure = 3;
        var index = LinkFixture.Index(project);

        Assert.NotEqual(index.KeyFor(1), index.KeyFor(4));
    }

    /// <summary>A hand shape that starts before the run reads as starting at its first beat —
    /// link creation splits shapes at the run's edges.</summary>
    [Fact]
    public void AHandShapeFromBeforeTheRun_IsClippedToIt()
    {
        var project = LinkFixture.Project(seed: null);
        var level = project.Arrangements[0].Levels[0];
        level.HandShapes.Add(new HandShape { Position = LinkFixture.At(0, 3), EndPosition = LinkFixture.At(1, 1), TemplateId = 0 });
        level.HandShapes.Add(new HandShape { Position = LinkFixture.At(4, 0), EndPosition = LinkFixture.At(4, 1), TemplateId = 0 });
        var index = LinkFixture.Index(project);

        Assert.Equal(index.KeyFor(1), index.KeyFor(4));
    }

    /// <summary>A run is compared whole: a sustain held across its inner barline counts.</summary>
    [Fact]
    public void ARun_ComparesAcrossItsInnerBarline()
    {
        var beats = LinkFixture.Beats(8, seed: null);
        var arrangement = new Arrangement { StringCount = 6 };
        var level = new ChartLevel();
        arrangement.Levels.Add(level);
        // Measures 0-1 and 4-5: same notes, but the first copy holds its last note one beat into
        // the next measure and the second copy two beats. Single measures compare equal; the runs do not.
        foreach (var (start, hold) in new[] { (0, 2.0), (4, 3.0) })
        {
            level.Notes.Add(LinkFixture.Note(LinkFixture.At(start, 0), 0, 3, 3 + hold));
            level.Notes.Add(LinkFixture.Note(LinkFixture.At(start + 1, 3), 1, 5, 0));
        }

        var index = new MeasureContentIndex(arrangement, level, beats);

        Assert.Equal(index.KeyFor(0), index.KeyFor(4));
        Assert.Equal(index.KeyFor(1), index.KeyFor(5));
        Assert.NotEqual(index.KeyFor(0, 2), index.KeyFor(4, 2));
    }

    /// <summary>The stored hash is a wire format: it must not change between builds. A
    /// deliberate format change bumps the version token and this literal.</summary>
    [Fact]
    public void TheHash_IsStable()
    {
        var beats = LinkFixture.Beats(2, seed: null);
        var arrangement = new Arrangement { StringCount = 6 };
        var level = new ChartLevel();
        arrangement.Levels.Add(level);
        level.Notes.Add(LinkFixture.Note(LinkFixture.At(0, 1), 2, 5, 0.5));
        var key = new MeasureContentIndex(arrangement, level, beats).KeyFor(0);

        Assert.Equal("mc2;det;mf64;snap5;AHF|t4/4x4|N1:2:5~0+1/2|A1:-", key.Canonical);
        Assert.Equal("e33d276aceb3267a20c609d50f8ff20b", key.Hash);
        Assert.Equal(MeasureContentKeyHash(key.Canonical), key.Hash);
        Assert.Matches("^[0-9a-f]{32}$", key.Hash);
    }

    /// <summary>A link must survive save → load → clean-up → edit elsewhere, round after round.</summary>
    [Fact]
    public void Keys_SurviveRepeatedSaveLoadAndEditRounds()
    {
        var config = LinkFixture.Config(MinNoteDistanceMode.Milliseconds, 25);
        var project = LinkFixture.Project(seed: 11);
        ChartSpacingEnforcer.EnforceProject(project, config);
        var original = LinkFixture.Index(project, config).KeyFor(1);
        var fingerprint = LinkFixture.Index(project, MeasureContentOptions.Integrity).KeyFor(1);
        var serializer = new RscpChartSerializer();
        var folder = Directory.CreateTempSubdirectory("dc-link-roundtrip-");
        try
        {
            for (var round = 0; round < 6; round++)
            {
                var path = Path.Combine(folder.FullName, $"round{round}.rscp");
                serializer.Save(path, project);
                project = serializer.Load(path);
                ChartSpacingEnforcer.EnforceProject(project, config);

                // An edit OUTSIDE every copy, next to their edges, each round.
                var level = project.Arrangements[0].Levels[0];
                level.Notes.Add(LinkFixture.Note(LinkFixture.At(2 + (round % 2 * 3), 0.25 * (round + 1)), round % 6, round, 1));
                ChartSpacingEnforcer.EnforceProject(project, config);

                var index = LinkFixture.Index(project, config);
                Assert.Equal(fingerprint, LinkFixture.Index(project, MeasureContentOptions.Integrity).KeyFor(1));
                Assert.Equal(original, index.KeyFor(1));
                Assert.Equal(original, index.KeyFor(4));
                Assert.Equal(original, index.KeyFor(7));
            }
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    /// <summary>Staccato and legato 16ths are different music: the gap only absorbs the
    /// enforcer's own clamp, never a deliberately short note.</summary>
    [Theory]
    [InlineData(MinNoteDistanceMode.MeasureFraction, 10.0)]
    [InlineData(MinNoteDistanceMode.Milliseconds, 10.0)]
    public void StaccatoAndLegato_NeverCompareEqual(MinNoteDistanceMode mode, double ms)
    {
        var project = new ChartProject();
        foreach (var beat in LinkFixture.Beats(3, seed: 2)) project.Beats.Add(beat);
        var arrangement = new Arrangement { StringCount = 6 };
        var level = new ChartLevel();
        arrangement.Levels.Add(level);
        project.Arrangements.Add(arrangement);
        for (var i = 0; i < 16; i++)
        {
            level.Notes.Add(LinkFixture.Note(LinkFixture.At(0, i * 0.25), 2, 5, 0.25)); // legato: rings to the next
            level.Notes.Add(LinkFixture.Note(LinkFixture.At(1, i * 0.25), 2, 5, 0.125)); // staccato: half as long
        }

        var config = LinkFixture.Config(mode, ms);
        ChartSpacingEnforcer.EnforceProject(project, config);
        var index = LinkFixture.Index(project, config);

        Assert.NotEqual(index.KeyFor(0), index.KeyFor(1));
        Assert.Contains("~~", index.KeyFor(0).Canonical, StringComparison.Ordinal);
        Assert.Contains("~0+1/8", index.KeyFor(1).Canonical, StringComparison.Ordinal);
    }

    /// <summary>A key says which rule built it: a different kind, gap or option set never
    /// produces an equal key or hash, so a stored hash from another rule stops matching.</summary>
    [Fact]
    public void TheKey_DescribesTheRuleThatBuiltIt()
    {
        var project = LinkFixture.Project(seed: null);
        var keys = new[]
        {
            LinkFixture.Index(project, MeasureContentOptions.Detection).KeyFor(1),
            LinkFixture.Index(project, MeasureContentOptions.SoundsOnly).KeyFor(1),
            LinkFixture.Index(project, MeasureContentOptions.Integrity).KeyFor(1),
            LinkFixture.Index(project, LinkFixture.Config(MinNoteDistanceMode.Milliseconds, 20)).KeyFor(1),
            LinkFixture.Index(project, LinkFixture.Config(MinNoteDistanceMode.MeasureFraction, divisor: 32)).KeyFor(1)
        };

        Assert.Equal(keys.Length, keys.Select(k => k.Hash).Distinct().Count());
        Assert.All(keys, k => Assert.StartsWith(MeasureContentOptions.FormatVersion + ";", k.Canonical, StringComparison.Ordinal));
        Assert.StartsWith("mc2;det;ms20;", keys[3].Canonical, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The two roles: after clean-up the three copies are equal for DETECTION and differ in
    ///     their stored INTEGRITY content (trimmed sustains, a snapped or removed anchor) — so the
    ///     integrity key fingerprints one placement and never compares two.
    /// </summary>
    [Fact]
    public void Integrity_SeesTheStoredEdges_DetectionDoesNot()
    {
        var project = LinkFixture.Project(seed: 4);
        var config = LinkFixture.Config(MinNoteDistanceMode.MeasureFraction);
        ChartSpacingEnforcer.EnforceProject(project, config);

        var detection = LinkFixture.Index(project, config);
        var integrity = LinkFixture.Index(project, MeasureContentOptions.Integrity);

        Assert.Equal(detection.KeyFor(1), detection.KeyFor(7));
        Assert.NotEqual(integrity.KeyFor(1), integrity.KeyFor(7));
    }

    public static TheoryData<string> NotationEdits() => ["notation duration", "notation voice", "sustain inside the gap", "template name"];

    /// <summary>What the integrity key adds over detection: notation and exact stored values.</summary>
    [Theory]
    [MemberData(nameof(NotationEdits))]
    public void IntegrityOnlyEdits_ChangeIntegrity_NotDetection(string edit)
    {
        var project = LinkFixture.Project(seed: null);
        var config = LinkFixture.Config(MinNoteDistanceMode.MeasureFraction);
        ChartSpacingEnforcer.EnforceProject(project, config);
        var detection = LinkFixture.Index(project, config).KeyFor(4);
        var integrity = LinkFixture.Index(project, MeasureContentOptions.Integrity).KeyFor(4);

        var arrangement = project.Arrangements[0];
        var first = arrangement.Levels[0].Notes.Single(n => n.Position == LinkFixture.At(4, 0));
        switch (edit)
        {
            case "notation duration": first.NotationDuration = new Fraction(1, 4); break;
            case "notation voice":
                arrangement.NotationVoices.Add(new NotationVoice { VoiceIndex = 2, Notes = { LinkFixture.Note(LinkFixture.At(4, 1), 5, 0, 1) } });
                break;
            case "sustain inside the gap": first.Sustain = LinkFixture.Beats(1) - new FractionalPosition(new Fraction(1, 32)); break;
            case "template name": arrangement.ChordTemplates[0].Name = "A5 (renamed)"; break;
        }

        Assert.Equal(detection, LinkFixture.Index(project, config).KeyFor(4));
        Assert.NotEqual(integrity, LinkFixture.Index(project, MeasureContentOptions.Integrity).KeyFor(4));
    }

    /// <summary>The Core gap and the enforcer's clamp are one implementation.</summary>
    [Theory]
    [InlineData(MinNoteDistanceMode.MeasureFraction, 10.0, 64)]
    [InlineData(MinNoteDistanceMode.BeatFraction, 10.0, 12)]
    [InlineData(MinNoteDistanceMode.Milliseconds, 37.5, 64)]
    public void TheGap_IsTheEnforcersBound(MinNoteDistanceMode mode, double ms, int divisor)
    {
        var beats = LinkFixture.Beats(4, seed: 9, beatsPerMeasure: 7);
        var config = LinkFixture.Config(mode, ms, divisor);
        foreach (var position in new[] { new FractionalPosition(0), new FractionalPosition(9, new Fraction(1, 3)), new FractionalPosition(20) })
        {
            Assert.Equal(ChartSpacingEnforcer.GetMinDistance(position, beats, config),
                ChartSpacingEnforcer.SpacingGapOf(config).At(position, beats));
        }
    }

    private static string MeasureContentKeyHash(string canonical) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(canonical)).AsSpan(0, 16));
}
