using DotCharter.Services.Editing;
using DotCharter.Services.Editing.Fingering;
using DotCharter.Services.Editing.Fixing;
using DotCharter.Services.Editing.FretAnchors;
using DotCharter.Services.Editing.Maintenance;
using DotCharter.Services.Editing.Phrases;
using DotCharter.Services.Editing.Refret;
using DotCharter.Services.Editing.Selection;
using DotCharter.Services.Editing.Tracks;
using DotCharter.Services.Parsing.GuitarPro;
using DotCharter.Tests.Services.Editing.Maintenance;
using DotCharter.Tests.Services.Parsing.GuitarPro;
using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.Tests.Services.Editing.FretAnchors;

/// <summary>
///     When the FHP walk keeps the hand below a FREE chord's index fret (#494),
///     the chord's fingering follows — as a variant template of its own, never by
///     mutating the shared one — and the result stays clean of E04, I46 and L08.
/// </summary>
public sealed class ChordFingeringFollowerTests
{
    private static readonly FingeringGenerationService Fingering = new();

    internal static List<BeatMarker> Beats(int count = 16) =>
        [.. Enumerable.Range(0, count).Select(i => new BeatMarker { Time = i * 500.0, Bpm = 120 })];

    private static Chord Chord(int beat, int templateId, IReadOnlyDictionary<int, int> frets)
    {
        var chord = new Chord { Position = new FractionalPosition(beat), TemplateId = templateId };
        foreach (var (stringIndex, fret) in frets)
            chord.ChordNotes.Add(new ChordNote { StringIndex = stringIndex, Fret = fret });
        return chord;
    }

    /// <summary>G9+B9 fingered 1-1, two strums of it, a hand shape over the first.</summary>
    private static (Arrangement Arrangement, ChartLevel Level, ChordTemplate Shared) DoubleStopArrangement()
    {
        var arrangement = new Arrangement();
        var level = new ChartLevel();
        arrangement.Levels.Add(level);
        var frets = new Dictionary<int, int> { [3] = 9, [4] = 9 };
        var shared = new ChordTemplate
        {
            Id = 0, Name = "x", Frets = new Dictionary<int, int>(frets),
            Fingers = new Dictionary<int, int> { [3] = 1, [4] = 1 }
        };
        arrangement.ChordTemplates.Add(shared);
        level.Chords.Add(Chord(2, 0, frets));
        level.Chords.Add(Chord(6, 0, frets));
        level.HandShapes.Add(new HandShape
        {
            Position = new FractionalPosition(2), EndPosition = new FractionalPosition(4), TemplateId = 0
        });
        return (arrangement, level, shared);
    }

    [Fact]
    public void AChordBelowItsIndexFret_GetsAShiftedVariant_AndTheSharedTemplateIsUntouched()
    {
        var (arrangement, level, shared) = DoubleStopArrangement();
        FretAnchor[] anchors = [new(new FractionalPosition(0), 8, 4)];

        var plan = ChordFingeringFollower.Plan(arrangement, level, anchors, t => t, Fingering);
        plan.ApplyTo(arrangement);

        // ONE variant for both strums, fingered from the hand at 8.
        var variant = Assert.Single(plan.MintedTemplates);
        Assert.Equal(new Dictionary<int, int> { [3] = 2, [4] = 2 }, variant.Fingers);
        Assert.Equal(shared.Frets, variant.Frets);
        Assert.Equal("x", variant.Name);
        Assert.Equal(1, variant.Id);
        Assert.All(level.Chords, c => Assert.Equal(1, c.TemplateId));
        Assert.Equal(1, level.HandShapes[0].TemplateId);

        // Identity: the shared instance is still in the collection and still
        // says index-on-9.
        Assert.Same(shared, arrangement.ChordTemplates[0]);
        Assert.Equal(new Dictionary<int, int> { [3] = 1, [4] = 1 }, shared.Fingers);
        Assert.Equal(2, arrangement.ChordTemplates.Count);
    }

    [Fact]
    public void AnAuthoredFingering_IsNeverFollowed()
    {
        var (arrangement, level, _) = DoubleStopArrangement();
        FretAnchor[] anchors = [new(new FractionalPosition(0), 8, 4)];

        var plan = ChordFingeringFollower.Plan(arrangement, level, anchors, _ => null, Fingering);

        Assert.True(plan.IsEmpty);
        Assert.Empty(plan.MintedTemplates);
    }

    [Fact]
    public void AnIdenticalVariantAlreadyInTheArrangement_IsReused_NotMintedAgain()
    {
        var (arrangement, level, _) = DoubleStopArrangement();
        var existing = new ChordTemplate
        {
            Id = 5, Name = "x", Frets = new Dictionary<int, int> { [3] = 9, [4] = 9 },
            Fingers = new Dictionary<int, int> { [3] = 2, [4] = 2 }
        };
        arrangement.ChordTemplates.Add(existing);
        FretAnchor[] anchors = [new(new FractionalPosition(0), 8, 4)];

        var plan = ChordFingeringFollower.Plan(arrangement, level, anchors, t => t, Fingering);

        Assert.Empty(plan.MintedTemplates);
        Assert.All(plan.Chords, c => Assert.Equal(5, c.NewId));
    }

    [Fact]
    public void AShiftPastThePinky_LeavesTheChordAlone()
    {
        // Index on 9 with the hand on 5: every finger would land past 4.
        var (arrangement, level, _) = DoubleStopArrangement();
        FretAnchor[] anchors = [new(new FractionalPosition(0), 5, 4)];

        Assert.True(ChordFingeringFollower.Plan(arrangement, level, anchors, t => t, Fingering).IsEmpty);
    }

    [Fact]
    public void AStringStoppedByTheCapo_KeepsItsFinger_AndTheShiftIsReadAboveIt()
    {
        // Capo 3: string 2 sits at the capo (open, unfingered); the index is on
        // 6. Under FHP 5 the fretted strings move one finger up and string 2
        // stays as it is.
        var arrangement = new Arrangement { Capo = 3 };
        var level = new ChartLevel();
        arrangement.Levels.Add(level);
        var frets = new Dictionary<int, int> { [2] = 3, [3] = 6, [4] = 7 };
        arrangement.ChordTemplates.Add(new ChordTemplate
        {
            Id = 0, Name = "x", Frets = new Dictionary<int, int>(frets),
            Fingers = new Dictionary<int, int> { [3] = 1, [4] = 2 }
        });
        level.Chords.Add(Chord(2, 0, frets));
        FretAnchor[] anchors = [new(new FractionalPosition(0), 5, 4)];

        var plan = ChordFingeringFollower.Plan(arrangement, level, anchors, t => t, Fingering);

        var variant = Assert.Single(plan.MintedTemplates);
        Assert.Equal(new Dictionary<int, int> { [3] = 2, [4] = 3 }, variant.Fingers);
        Assert.Equal(frets, variant.Frets);
    }

    [Fact]
    public void TwoPlansOfOneEdit_ShareTheirMintedTemplates()
    {
        // One plan per difficulty level, applied together: the second must reuse
        // the first's variant and never hand out its id again.
        var (arrangement, level, _) = DoubleStopArrangement();
        var second = new ChartLevel();
        second.Chords.Add(Chord(2, 0, new Dictionary<int, int> { [3] = 9, [4] = 9 }));
        FretAnchor[] anchors = [new(new FractionalPosition(0), 8, 4)];

        var first = ChordFingeringFollower.Plan(arrangement, level, anchors, t => t, Fingering);
        var then = ChordFingeringFollower.Plan(arrangement, second, anchors, t => t, Fingering,
            first.MintedTemplates);

        Assert.Empty(then.MintedTemplates);
        Assert.Equal(first.MintedTemplates[0].Id, Assert.Single(then.Chords).NewId);
    }

    [Fact]
    public void OnlyTheSuggestion_OrAVariantThisPassMinted_CountsAsFree()
    {
        var template = new ChordTemplate { Frets = new Dictionary<int, int> { [3] = 9, [4] = 10 } };
        Assert.Null(ChordFingeringFollower.SuggestedShift(template, 6));

        ChordTemplateFingerSetter.SetSuggestedFingers(template, 6);
        Assert.Equal(new Dictionary<int, int> { [3] = 1, [4] = 2 }, template.Fingers);
        Assert.Equal(0, ChordFingeringFollower.SuggestedShift(template, 6));

        Assert.True(ChordFingeringFollower.IsFreeFingering(template, 6));

        // Every finger one up the hand: the shape of a variant, but nobody minted
        // THIS one — a charter typed it, so it is authored.
        template.Fingers[3] = 2;
        template.Fingers[4] = 3;
        Assert.Equal(1, ChordFingeringFollower.SuggestedShift(template, 6));
        Assert.False(ChordFingeringFollower.IsFreeFingering(template, 6));

        // Not uniform, or below the suggestion: no shift at all.
        template.Fingers[4] = 4;
        Assert.Null(ChordFingeringFollower.SuggestedShift(template, 6));
        template.Fingers[3] = 1;
        template.Fingers[4] = 1;
        Assert.Null(ChordFingeringFollower.SuggestedShift(template, 6));
    }

    [Fact]
    public void TheVariantAPlanMints_IsFree()
    {
        var (arrangement, level, _) = DoubleStopArrangement();
        FretAnchor[] anchors = [new(new FractionalPosition(0), 8, 4)];
        var resolver = ChordFingeringFollower.FreeGripResolver(arrangement);

        var variant = Assert.Single(ChordFingeringFollower.Plan(arrangement, level, anchors, t => t, Fingering)
            .MintedTemplates);

        Assert.True(ChordFingeringFollower.IsMintedVariant(variant));
        // ...read through its base grip, index on 9 again.
        Assert.Equal(new Dictionary<int, int> { [3] = 1, [4] = 1 }, resolver(variant)!.Fingers);
        // The mark is a template field (#508), so a copy carries it.
        Assert.True(variant.FollowedFingering);
        Assert.True(ChordFingeringFollower.IsMintedVariant(variant.Clone()));
    }

    [Fact]
    public void AStringAtTheCapo_NeverMovesWithTheShift()
    {
        // Capo 3: the string on fret 3 is stopped by the capo, so a variant leaves
        // its finger where the suggestion put it and the shift is read off the
        // strings above it.
        var template = new ChordTemplate { Frets = new Dictionary<int, int> { [2] = 3, [3] = 4, [4] = 5 } };
        ChordTemplateFingerSetter.SetSuggestedFingers(template, 6);
        var suggested = new Dictionary<int, int>(template.Fingers);
        foreach (var s in new[] { 3, 4 }) template.Fingers[s] = suggested[s] + 1;

        Assert.Equal(1, ChordFingeringFollower.SuggestedShift(template, 6, capo: 3));
        // Without the capo the same fingering is not a uniform shift.
        Assert.Null(ChordFingeringFollower.SuggestedShift(template, 6));
    }

    // ─── The import pass, end to end, through the inspector ─────────────────

    /// <summary>
    ///     A line with the index on 8, a SUGGESTED-fingered double stop G9+B10 in
    ///     a hand shape, then 8 again — the shape #494 says is played from 8.
    /// </summary>
    internal static Arrangement ImportLikeArrangement()
    {
        var arrangement = new Arrangement();
        var level = new ChartLevel();
        arrangement.Levels.Add(level);
        var frets = new Dictionary<int, int> { [3] = 9, [4] = 10 };
        var template = new ChordTemplate { Id = 0, Frets = new Dictionary<int, int>(frets) };
        ChordTemplateFingerSetter.SetSuggestedFingers(template, 6);
        Assert.True(template.Fingers.GetValueOrDefault(3) == 1 && template.Fingers.Values.Max() <= 3,
            $"precondition: the suggester fingers G9+B10 index-first with a spare finger, got " +
            $"{string.Join(",", template.Fingers.Select(kv => $"{kv.Key}:{kv.Value}"))}");
        arrangement.ChordTemplates.Add(template);

        level.Notes.Add(new GuitarNote { Position = new FractionalPosition(0), StringIndex = 3, Fret = 8 });
        level.Notes.Add(new GuitarNote { Position = new FractionalPosition(1), StringIndex = 4, Fret = 8 });
        level.Chords.Add(Chord(2, 0, frets));
        level.HandShapes.Add(new HandShape
        {
            Position = new FractionalPosition(2), EndPosition = new FractionalPosition(4), TemplateId = 0
        });
        level.Notes.Add(new GuitarNote { Position = new FractionalPosition(4), StringIndex = 3, Fret = 8 });
        return arrangement;
    }

    private static List<string> AnchorFingeringFindings(Arrangement arrangement) =>
        new ArrangementValidationService(new FakeAppLogger()).Validate(arrangement, Beats())
            .Where(i => i.Type is ArrangementIssueType.IndexFingerNotOnAnchorFret
                or ArrangementIssueType.FingeringAnchorMismatch
                or ArrangementIssueType.FirstFingerNotOnLowestFret)
            .Select(i => i.Code)
            .ToList();

    [Fact]
    public void TheImportPass_HoldsTheHand_RefingersTheChord_AndLeavesNoE04I46OrL08()
    {
        var arrangement = ImportLikeArrangement();
        var original = arrangement.ChordTemplates[0];
        var originalFingers = new Dictionary<int, int>(original.Fingers);
        var level = arrangement.Levels[0];

        ImportFhpPass.Run(arrangement, level, bassArrangement: false);

        Assert.Equal([8], level.Anchors.Select(a => a.Fret));
        var chord = Assert.Single(level.Chords);
        // The chord was the original's only user, so the original is DROPPED
        // (an import has no undo to keep it for) and the variant closes the gap:
        // ids stay list positions, and 0 is a real id.
        var variant = Assert.Single(arrangement.ChordTemplates);
        Assert.NotSame(original, variant);
        Assert.Equal(0, variant.Id);
        Assert.Equal(0, chord.TemplateId);
        Assert.Equal(0, level.HandShapes[0].TemplateId);
        Assert.DoesNotContain(1, variant.Fingers.Values);
        // Dropped, never mutated.
        Assert.Equal(originalFingers, original.Fingers);
        Assert.Empty(AnchorFingeringFindings(arrangement));
    }

    [Fact]
    public void TheImportPass_DropsOnlyTheOrphan_AndRepointsEveryIdAboveIt()
    {
        // Template 0 is the followed double stop; template 1 is a chord nobody
        // re-fingers. Dropping 0 moves 1 to 0, the variant to 1, and every chord
        // and hand shape with them.
        var arrangement = ImportLikeArrangement();
        var level = arrangement.Levels[0];
        var frets = new Dictionary<int, int> { [0] = 3, [1] = 5, [2] = 5 };
        var kept = new ChordTemplate
        {
            Id = 1, Name = "kept", Frets = new Dictionary<int, int>(frets),
            Fingers = new Dictionary<int, int> { [0] = 1, [1] = 3, [2] = 4 }
        };
        arrangement.ChordTemplates.Add(kept);
        level.Chords.Add(Chord(8, 1, frets));
        level.HandShapes.Add(new HandShape
        {
            Position = new FractionalPosition(8), EndPosition = new FractionalPosition(9), TemplateId = 1
        });

        ImportFhpPass.Run(arrangement, level, bassArrangement: false);

        Assert.Equal([0, 1], arrangement.ChordTemplates.Select(t => t.Id));
        Assert.Same(kept, arrangement.ChordTemplates[0]);
        var keptChord = level.Chords.Single(c => c.Position == new FractionalPosition(8));
        var followed = level.Chords.Single(c => c.Position == new FractionalPosition(2));
        Assert.Equal(0, keptChord.TemplateId);
        Assert.Equal(1, followed.TemplateId);
        Assert.Equal([1, 0], level.HandShapes.OrderBy(h => h.Position).Select(h => h.TemplateId));
        Assert.DoesNotContain(1, arrangement.ChordTemplates[1].Fingers.Values);
    }

    [Fact]
    public void TheImportPass_KeepsATemplateAnotherChordStillUses()
    {
        // A second strum of the same grip after a jump up the neck: the hand
        // comes down onto ITS index, so it keeps the original template — which
        // therefore stays.
        var arrangement = ImportLikeArrangement();
        var level = arrangement.Levels[0];
        var original = arrangement.ChordTemplates[0];
        level.Notes.Add(new GuitarNote { Position = new FractionalPosition(6), StringIndex = 3, Fret = 14 });
        level.Chords.Add(Chord(8, 0, new Dictionary<int, int> { [3] = 9, [4] = 10 }));

        ImportFhpPass.Run(arrangement, level, bassArrangement: false);

        Assert.Equal(2, arrangement.ChordTemplates.Count);
        Assert.Same(original, arrangement.ChordTemplates[0]);
        Assert.Equal(0, original.Id);
        Assert.Equal(0, level.Chords.Single(c => c.Position == new FractionalPosition(8)).TemplateId);
        Assert.Equal(1, level.Chords.Single(c => c.Position == new FractionalPosition(2)).TemplateId);
    }

    // ─── Guitar Pro: a fingering the tab states is authored ─────────────────

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AGuitarProChordFingeredLikeTheSuggestion_IsNeverRefingeredOnImport(bool fingeredInTheTab)
    {
        // Fret 8, then G9+B10, then 8 again. The tab fingers the double stop 1-2 —
        // exactly what the suggester says — so "equals the suggestion" cannot be
        // what makes it free. Without fingers in the file (the control) the same
        // chord follows the hand at 8.
        var chord = FxBeat.Chord(0,
            new FxNote(3, 9, LeftFinger: fingeredInTheTab ? 1 : null),
            new FxNote(4, 10, LeftFinger: fingeredInTheTab ? 2 : null));
        var measure = new FxMeasure().AddVoice(FxBeat.Note(0, 3, 8), chord, FxBeat.Note(0, 3, 8), FxBeat.Note(0, 4, 8));

        var arrangement = ImportGp5(LegacyGpFixtureBuilder.WriteFile("5.10", [measure]));
        var level = Assert.Single(arrangement.Levels);
        var imported = Assert.Single(level.Chords);
        var template = arrangement.ChordTemplates.Single(t => t.Id == imported.TemplateId);
        var atChord = level.Anchors.Last(a => a.Position <= imported.Position);

        if (fingeredInTheTab)
        {
            Assert.Equal(new Dictionary<int, int> { [3] = 1, [4] = 2 }, template.Fingers);
            Assert.Equal(9, atChord.Fret);
        }
        else
        {
            Assert.Equal(new Dictionary<int, int> { [3] = 2, [4] = 3 }, template.Fingers);
            Assert.Equal(8, atChord.Fret);
        }

        Assert.Single(arrangement.ChordTemplates);
    }

    private static Arrangement ImportGp5(string path)
    {
        try
        {
            var importer = new GpImporter();
            var tracks = importer.GetAvailableTracks(path);
            return Assert.Single(importer.ImportTracks(path, tracks.Select(t => t.Id).ToList()));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void WithoutTheFollower_TheHeldHandWouldContradictTheFingering()
    {
        // The vacuity keeper for the test above: the same walk without the
        // follower leaves an index-on-9 grip under FHP 8 — exactly E04.
        var arrangement = ImportLikeArrangement();
        var level = arrangement.Levels[0];

        var anchors = new FretAnchorGenerationService().GenerateAnchors(level, null, null,
            arrangement.ChordTemplates, arrangement.Capo, false, t => t);
        foreach (var a in anchors) level.Anchors.Add(a);

        Assert.Contains("E04", AnchorFingeringFindings(arrangement));
    }

    // ─── Tools ▸ Generate FHP: one undo node ────────────────────────────────

    [Fact]
    public void GenerateFhp_PutsTheMintedTemplateInTheSameUndoNodeAsTheAnchors()
    {
        var project = new ChartProject();
        foreach (var beat in Beats()) project.Beats.Add(beat);
        var arrangement = ImportLikeArrangement();
        project.Arrangements.Add(arrangement);
        var level = arrangement.Levels[0];
        level.Anchors.Add(new FretAnchor(new FractionalPosition(0), 3, 4));
        var harness = new Harness(project);

        harness.Maintenance.GenerateFhp();

        Assert.Equal([8], level.Anchors.Select(a => a.Fret));
        Assert.Equal(2, arrangement.ChordTemplates.Count);
        Assert.NotEqual(0, level.Chords[0].TemplateId);

        harness.History.Undo();

        // One undo step takes back the anchors AND the minted template.
        Assert.Equal([3], level.Anchors.Select(a => a.Fret));
        Assert.Single(arrangement.ChordTemplates);
        Assert.Equal(0, level.Chords[0].TemplateId);
        Assert.Equal(0, level.HandShapes[0].TemplateId);
        Assert.False(harness.History.CanUndo);
    }

    [Fact]
    public void GenerateFhpAgain_WithoutTheLineThatHeldTheHand_ReturnsTheChordToItsBaseGrip()
    {
        // The first pass holds the hand at 8 and shifts the double stop to a
        // variant with no index. Once the line that held it is deleted, a second
        // pass must read the variant as the FREE grip it is — not as an authored
        // one whose middle finger implies FHP 8 — and put it back on its base grip.
        var project = new ChartProject();
        foreach (var beat in Beats()) project.Beats.Add(beat);
        var arrangement = ImportLikeArrangement();
        project.Arrangements.Add(arrangement);
        var level = arrangement.Levels[0];
        var harness = new Harness(project);

        harness.Maintenance.GenerateFhp();
        Assert.NotEqual(0, level.Chords[0].TemplateId);

        foreach (var note in level.Notes.Where(n => n.Position < new FractionalPosition(2)).ToList())
            level.Notes.Remove(note);
        harness.Maintenance.GenerateFhp();

        Assert.Equal("2:9 4:8", string.Join(" ", level.Anchors.OrderBy(a => a.Position)
            .Select(a => $"{a.Position.BeatId}:{a.Fret}")));
        Assert.Equal(0, level.Chords[0].TemplateId);
        Assert.Equal(0, level.HandShapes[0].TemplateId);
    }

    [Fact]
    public void GenerateFhp_LeavesAChartersShiftedGripAlone()
    {
        // A hand-charted file: the charter fingered G9+B10 2-3 — a uniform shift
        // of the suggestion, but typed in, not minted. It still says "hand at 8",
        // so Generate FHP anchors there and never re-fingers it.
        var project = new ChartProject();
        foreach (var beat in Beats()) project.Beats.Add(beat);
        var arrangement = ImportLikeArrangement();
        var template = arrangement.ChordTemplates[0];
        template.Fingers[3] = 2;
        template.Fingers[4] = 3;
        project.Arrangements.Add(arrangement);
        var level = arrangement.Levels[0];
        foreach (var note in level.Notes.Where(n => n.Position < new FractionalPosition(2)).ToList())
            level.Notes.Remove(note);
        var harness = new Harness(project);

        harness.Maintenance.GenerateFhp();

        Assert.Equal(8, level.Anchors.OrderBy(a => a.Position).First().Fret);
        Assert.Single(arrangement.ChordTemplates);
        Assert.Equal(0, level.Chords[0].TemplateId);
    }

    internal sealed class Harness
    {
        public Harness(ChartProject project)
        {
            var logger = new FakeAppLogger();
            History = new HistoryService(logger);
            var config = new FakeConfigurationService();
            State.ActiveProject = project;
            State.ActiveArrangement = project.Arrangements[0];
            State.ActiveLevel = project.Arrangements[0].Levels[0];

            var selection = new SelectionService();
            var transport = new TransportViewModel(new FakeAudioPlaybackService(), State, config, FakeLocalization.KeysOnly());
            var editor = new ChartEditorViewModel(State, transport, History, selection, new TempoService(logger),
                new TrackContextRegistry(State, selection), FakeLocalization.KeysOnly());
            var anchors = new FretAnchorGenerationService();
            Maintenance = new ChartMaintenanceService(config, new ChartImprovementService(), new LevelSquashService(),
                new LevelGenerationService(), new PhraseIterationPropagationService(History, new EditingScope(State), logger),
                anchors, new ChartPowerToolsTests.NoOpProjectSessionService(), History, logger, State, editor,
                new ArrangementAutoFixService(), new RefretService(anchors, new FingeringGenerationService()),
                FakeLocalization.English());
        }

        public ProjectState State { get; } = new();
        public HistoryService History { get; }
        public ChartMaintenanceService Maintenance { get; }
    }
}
