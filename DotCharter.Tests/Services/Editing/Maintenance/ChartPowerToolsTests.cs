using DotCharter.Services.Editing;
using DotCharter.Services.Editing.Fingering;
using DotCharter.Services.Editing.FretAnchors;
using DotCharter.Services.Editing.Fixing;
using DotCharter.Services.Editing.Phrases;
using DotCharter.Services.Editing.Refret;
using DotCharter.Services.Editing.Selection;
using DotCharter.Services.Parsing.Midi;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.Services.Editing.Tracks;
using DotCharter.Services.Editing.Maintenance;
using DotCharter.Services.Session;

namespace DotCharter.Tests.Services.Editing.Maintenance;

/// <summary>
///     The 0.22 charting power tools were fully implemented and fully unreachable:
///     five tested services with zero UI references. These tests cover the WIRING —
///     that the coordinator the menu/palette actually calls reaches each service AND
///     that the result lands in the project THROUGH the history service, so every
///     power tool is undoable. (The algorithms themselves are covered by
///     <c>LevelGenerationServiceTests</c> / <c>PhraseIterationPropagationServiceTests</c>
///     / the MIDI parser tests — they are not re-tested here.)
/// </summary>
public sealed class ChartPowerToolsTests
{
    [Fact]
    public void GenerateLevels_expands_the_single_level_and_undo_restores_it()
    {
        var h = new Harness();
        Assert.Single(h.State.ActiveArrangement!.Levels);

        var ok = h.Maintenance.GenerateLevels(4);

        Assert.True(ok);
        Assert.Equal(4, h.State.ActiveArrangement!.Levels.Count);
        // The top level is a verbatim copy of the source chart.
        Assert.Equal(6, h.State.ActiveArrangement!.Levels[3].Notes.Count);
        // Lower levels are thinner, and nested (each is a superset of the one below).
        Assert.True(h.State.ActiveArrangement!.Levels[0].Notes.Count <
                    h.State.ActiveArrangement!.Levels[3].Notes.Count);

        Assert.True(h.History.CanUndo);
        h.History.Undo();
        Assert.Single(h.State.ActiveArrangement!.Levels);
        Assert.Equal(6, h.State.ActiveArrangement!.Levels[0].Notes.Count);
    }

    /// <summary>
    ///     The async variant exists so the dialog can show progress instead of
    ///     freezing the window, and it must be the SAME tool: same result, same
    ///     ONE undo node, plus one report per level built.
    /// </summary>
    [Fact]
    public async Task GenerateLevelsAsync_matchesTheSyncTool_reportsEveryLevel_andStaysOneUndoNode()
    {
        var h = new Harness();
        var steps = new List<LevelGenerationProgress>();

        var ok = await h.Maintenance.GenerateLevelsAsync(4, new InlineProgress(steps.Add));

        Assert.True(ok);
        Assert.Equal(4, h.State.ActiveArrangement!.Levels.Count);
        Assert.Equal(6, h.State.ActiveArrangement!.Levels[3].Notes.Count);

        Assert.Equal([1, 2, 3, 4], steps.Select(s => s.CompletedLevels));
        Assert.All(steps, s => Assert.Equal(4, s.TotalLevels));
        Assert.Equal(1.0, steps[^1].Fraction);

        // ONE node: a single Undo puts the single source level back and empties
        // the stack. A per-level node would leave three more behind.
        Assert.True(h.History.CanUndo);
        h.History.Undo();
        Assert.Single(h.State.ActiveArrangement!.Levels);
        Assert.False(h.History.CanUndo);
    }

    /// <summary>A refused request reports nothing and records nothing — a
    /// progress bar that moved for a no-op is a lie about work.</summary>
    [Fact]
    public async Task GenerateLevelsAsync_withFewerThanTwoLevels_reportsNothing_andRecordsNothing()
    {
        var h = new Harness();
        var steps = new List<LevelGenerationProgress>();

        Assert.False(await h.Maintenance.GenerateLevelsAsync(1, new InlineProgress(steps.Add)));

        Assert.Empty(steps);
        Assert.False(h.History.CanUndo);
    }

    /// <summary>Reports on whatever thread produced them, so the order is exact
    /// rather than whatever a SynchronizationContext got round to.</summary>
    private sealed class InlineProgress(Action<LevelGenerationProgress> onReport)
        : IProgress<LevelGenerationProgress>
    {
        public void Report(LevelGenerationProgress value) => onReport(value);
    }

    [Fact]
    public void GenerateLevels_with_fewer_than_two_levels_is_a_no_op()
    {
        var h = new Harness();

        Assert.False(h.Maintenance.GenerateLevels(1));

        Assert.Single(h.State.ActiveArrangement!.Levels);
        Assert.False(h.History.CanUndo);
    }

    [Fact]
    public void PropagatePhrase_copies_the_iteration_under_the_playhead_and_undo_restores_the_targets()
    {
        var h = new Harness();
        var arrangement = h.State.ActiveArrangement!;

        // Two iterations of phrase 0 (beats 0.. and 8..) plus a filler phrase, so
        // the source span is [0,4) and the target span is [8,12).
        arrangement.PhraseIterations.Add(new PhraseIteration(new FractionalPosition(0), 0));
        arrangement.PhraseIterations.Add(new PhraseIteration(new FractionalPosition(4), 1));
        arrangement.PhraseIterations.Add(new PhraseIteration(new FractionalPosition(8), 0));
        arrangement.PhraseIterations.Add(new PhraseIteration(new FractionalPosition(12), 1));

        var level = arrangement.Levels[0];
        var targetSoundsBefore = level.Notes.Count(n =>
            n.Position >= new FractionalPosition(8) && n.Position < new FractionalPosition(12));
        Assert.Equal(0, targetSoundsBefore);

        // Playhead inside the FIRST iteration of phrase 0 (beat 0 = 0 ms).
        h.State.CurrentTimeMs = 10;

        var count = h.Maintenance.PropagatePhraseAtPlayhead();

        Assert.Equal(1, count);
        // Beats 0..3 carry 4 notes in the fixture; they are replayed at beats 8..11.
        var copied = level.Notes.Where(n =>
            n.Position >= new FractionalPosition(8) && n.Position < new FractionalPosition(12)).ToList();
        Assert.Equal(4, copied.Count);

        Assert.True(h.History.CanUndo);
        h.History.Undo();
        Assert.DoesNotContain(level.Notes, n =>
            n.Position >= new FractionalPosition(8) && n.Position < new FractionalPosition(12));
    }

    [Fact]
    public void PropagatePhrase_without_phrase_iterations_is_a_no_op()
    {
        var h = new Harness();

        Assert.Equal(0, h.Maintenance.PropagatePhraseAtPlayhead());
        Assert.False(h.History.CanUndo);
    }

    [Fact]
    public void ApplyMidiDraft_adds_a_draft_arrangement_and_undo_removes_it()
    {
        var h = new Harness();
        var draft = MidiToDraftConverter.Convert(TwoNoteMidi());
        Assert.NotEmpty(draft.Notes);

        var ok = h.Maintenance.ApplyMidiDraft(draft, replaceBeatGrid: false);

        Assert.True(ok);
        Assert.Equal(2, h.State.ActiveProject!.Arrangements.Count);
        var imported = h.State.ActiveProject!.Arrangements[^1];
        Assert.Equal(draft.Notes.Count, imported.Levels[0].Notes.Count);
        // The freshly imported draft is what the user is looking at.
        Assert.Same(imported, h.State.ActiveArrangement);

        Assert.True(h.History.CanUndo);
        h.History.Undo();
        Assert.Single(h.State.ActiveProject!.Arrangements);
    }

    /// <summary>
    ///     A chart states its tuning TWICE — note LETTERS and per-string
    ///     OFFSETS — and the draft-arrangement initializer copied only the
    ///     letters. Every exporter resolves through
    ///     <c>TuningOffsetResolution</c>, whose letters-only derivation covers
    ///     4- and 6-string instruments ONLY, so a draft taken beside a 7-string
    ///     drop-A arrangement exported with an EMPTY offset vector, i.e. as
    ///     standard tuning — while its own frets had been computed against the
    ///     template's real offsets.
    /// </summary>
    [Fact]
    public void ApplyMidiDraft_carries_the_templates_tuning_offsets_not_just_its_letters()
    {
        var h = new Harness();
        var template = h.State.ActiveArrangement!;
        template.StringCount = 7;
        template.Tuning = ["A", "E", "A", "D", "G", "B", "E"];
        template.TuningPitches.Clear();
        foreach (var step in new[] { -2, 0, 0, 0, 0, 0, 0 })
            template.TuningPitches.Add(new TuningOffset(step));
        template.Capo = 2;

        Assert.True(h.Maintenance.ApplyMidiDraft(MidiToDraftConverter.Convert(TwoNoteMidi()),
            replaceBeatGrid: false));

        var imported = h.State.ActiveProject!.Arrangements[^1];
        Assert.Equal(7, imported.StringCount);
        Assert.Equal(2, imported.Capo);
        Assert.Equal([-2, 0, 0, 0, 0, 0, 0], imported.TuningPitches.Select(t => t.Step));
        // …and the copy is a copy: editing the draft must not move the template.
        imported.TuningPitches[0].Step = -5;
        Assert.Equal(-2, template.TuningPitches[0].Step);
    }

    /// <summary>With no arrangement to copy the factory still has to produce a
    /// usable six-string default rather than an offset-less one.</summary>
    [Fact]
    public void ADraftArrangementWithNoTemplate_IsPlainSixStringStandard()
    {
        var draft = DraftArrangementFactory.FromTemplate(null, "Draft");

        Assert.Equal("Draft", draft.Name);
        Assert.Equal(6, draft.StringCount);
        Assert.Equal(ArrangementType.Lead, draft.ArrangementType);
        Assert.Empty(draft.TuningPitches);
        Assert.Equal([0, 0, 0, 0, 0, 0], TuningOffsetResolution.Resolve(draft));
    }

    [Fact]
    public void ApplyMidiDraft_can_replace_the_beat_grid_with_the_midi_tempo_map()
    {
        var h = new Harness();
        var beatsBefore = h.State.Beats.Count;
        var draft = MidiToDraftConverter.Convert(TwoNoteMidi());

        Assert.True(h.Maintenance.ApplyMidiDraft(draft, replaceBeatGrid: true));

        Assert.Equal(draft.SuggestedBeats.Count, h.State.Beats.Count);
        Assert.NotEqual(beatsBefore, h.State.Beats.Count);

        h.History.Undo();
        Assert.Equal(beatsBefore, h.State.Beats.Count);
    }

    [Fact]
    public void Refret_optimizes_the_active_arrangement_as_one_undo_step()
    {
        var h = new Harness();
        var level = h.State.ActiveArrangement!.Levels[0];
        level.Notes.Clear();
        // A scale authored entirely on the A string, frets 3..15 — a classic
        // one-position cross-string fingering exists, so the engine must act.
        int[] frets = [3, 5, 7, 8, 10, 12, 14, 15];
        for (var i = 0; i < frets.Length; i++)
        {
            level.Notes.Add(new GuitarNote
            {
                Position = new FractionalPosition(i),
                StringIndex = 1,
                Fret = frets[i]
            });
        }

        var ok = h.Maintenance.Refret();

        Assert.True(ok);
        // The snapshot action reconciles into the live project — read through state.
        var refretted = h.State.ActiveArrangement!.Levels[0].Notes;
        Assert.True(refretted.Select(n => n.StringIndex).Distinct().Count() > 1,
            "The awkward scale should spread across strings.");

        // Pitches are bit-identical (standard tuning: 40 + open-offset + fret).
        Assert.Equal(
            frets.Select(f => 45 + f).ToList(),
            refretted.OrderBy(n => n.Position)
                .Select(n => 40 + StandardOpenStrings.SemitonesAboveLowE(6, n.StringIndex) + n.Fret).ToList());

        // ONE undo restores the original fretting entirely.
        Assert.True(h.History.CanUndo);
        h.History.Undo();
        var restored = h.State.ActiveArrangement!.Levels[0].Notes.OrderBy(n => n.Position).ToList();
        Assert.All(restored, n => Assert.Equal(1, n.StringIndex));
        Assert.Equal(frets, restored.Select(n => n.Fret).ToArray());
    }

    [Fact]
    public void Refret_on_an_already_optimal_chart_is_a_no_op_with_no_history_entry()
    {
        var h = new Harness();
        var level = h.State.ActiveArrangement!.Levels[0];
        level.Notes.Clear();
        // An open-position phrase that is already where the cost model wants it.
        for (var i = 0; i < 4; i++)
        {
            level.Notes.Add(new GuitarNote
            {
                Position = new FractionalPosition(i),
                StringIndex = i % 2,
                Fret = i % 3
            });
        }

        Assert.False(h.Maintenance.Refret());
        Assert.False(h.History.CanUndo);
    }

    [Fact]
    public void MergeNotesIntoChord_builds_a_named_fingered_template_and_undo_restores_the_notes()
    {
        var h = new Harness();
        var level = h.State.ActiveLevel!;
        var arrangement = h.State.ActiveArrangement!;

        // Two stacked notes at beat 10 (E-string fret 3 + A-string fret 5 = G5).
        var low = new GuitarNote
        {
            Position = new FractionalPosition(10), StringIndex = 0, Fret = 3,
            Sustain = new FractionalPosition(1), Hopo = HopoType.HammerOn
        };
        var high = new GuitarNote { Position = new FractionalPosition(10), StringIndex = 1, Fret = 5 };
        level.Notes.Add(low);
        level.Notes.Add(high);
        var untouchedCount = level.Notes.Count - 2;

        h.Selection.SelectedNotes.Add(low);
        h.Selection.SelectedNotes.Add(high);

        h.Maintenance.MergeSelectedNotesIntoChords();

        var chord = Assert.Single(level.Chords);
        Assert.Equal(new FractionalPosition(10), chord.Position);
        Assert.Equal(2, chord.ChordNotes.Count);
        // Techniques/sustains carry over per note.
        var lowCn = chord.ChordNotes.Single(n => n.StringIndex == 0);
        Assert.Equal(new FractionalPosition(1), lowCn.Sustain);
        Assert.Equal(HopoType.HammerOn, lowCn.Hopo);
        Assert.Equal(untouchedCount, level.Notes.Count);

        // The template arrives charting-ready: fingering suggested, and the name
        // deliberately BLANK — merging two notes makes a two-string shape, and
        // since 0.41 the automatic namer leaves those to the charter because two
        // pitches do not determine a chord.
        var template = Assert.Single(arrangement.ChordTemplates, t => t.Id == chord.TemplateId);
        Assert.Equal("", template.Name);
        Assert.NotEmpty(template.Fingers);

        Assert.True(h.History.CanUndo);
        h.History.Undo();
        Assert.Empty(level.Chords);
        Assert.Equal(untouchedCount + 2, level.Notes.Count);
    }

    [Fact]
    public void MergeNotesIntoChord_carries_each_notes_grace_hint_and_split_gives_it_back()
    {
        // ChordNote.GraceFret is per-STRING (0.33.2), so the merge can finally
        // carry a notation grace ornament across — and the split is its inverse.
        // Notes without a hint must stay hint-less.
        var h = new Harness();
        var level = h.State.ActiveLevel!;

        var low = new GuitarNote
        {
            Position = new FractionalPosition(10), StringIndex = 0, Fret = 3, GraceFret = 1
        };
        var high = new GuitarNote { Position = new FractionalPosition(10), StringIndex = 1, Fret = 5 };
        level.Notes.Add(low);
        level.Notes.Add(high);
        h.Selection.SelectedNotes.Add(low);
        h.Selection.SelectedNotes.Add(high);

        h.Maintenance.MergeSelectedNotesIntoChords();

        var chord = Assert.Single(level.Chords);
        Assert.Equal(1, chord.ChordNotes.Single(n => n.StringIndex == 0).GraceFret);
        Assert.Null(chord.ChordNotes.Single(n => n.StringIndex == 1).GraceFret);

        h.Selection.SelectedNotes.Clear();
        h.Selection.SelectedChords.Add(chord);
        h.Maintenance.SplitSelectedChordsIntoNotes();

        var backLow = level.Notes.Single(n =>
            n.Position.CompareTo(new FractionalPosition(10)) == 0 && n.StringIndex == 0);
        var backHigh = level.Notes.Single(n =>
            n.Position.CompareTo(new FractionalPosition(10)) == 0 && n.StringIndex == 1);
        Assert.Equal(1, backLow.GraceFret);
        Assert.Null(backHigh.GraceFret);
    }

    [Fact]
    public void MergeNotesIntoChord_with_a_single_note_is_a_no_op()
    {
        var h = new Harness();
        var level = h.State.ActiveLevel!;
        h.Selection.SelectedNotes.Add(level.Notes[0]);

        h.Maintenance.MergeSelectedNotesIntoChords();

        Assert.Empty(level.Chords);
        Assert.False(h.History.CanUndo);
    }

    [Fact]
    public void SplitChordsIntoNotes_replaces_the_chord_with_its_notes_and_undo_restores_it()
    {
        var h = new Harness();
        var level = h.State.ActiveLevel!;
        var arrangement = h.State.ActiveArrangement!;

        var template = new ChordTemplate { Id = 0, Name = "G5" };
        template.Frets[0] = 3;
        template.Frets[1] = 5;
        arrangement.ChordTemplates.Add(template);
        var chord = new Chord { Position = new FractionalPosition(10), TemplateId = 0 };
        chord.ChordNotes.Add(new ChordNote
        {
            StringIndex = 0, Fret = 3, Sustain = new FractionalPosition(1), Vibrato = true
        });
        chord.ChordNotes.Add(new ChordNote { StringIndex = 1, Fret = 5 });
        level.Chords.Add(chord);
        var noteCountBefore = level.Notes.Count;

        h.Selection.SelectedChords.Add(chord);

        h.Maintenance.SplitSelectedChordsIntoNotes();

        Assert.Empty(level.Chords);
        Assert.Equal(noteCountBefore + 2, level.Notes.Count);
        var lowNote = level.Notes.Single(n =>
            n.Position.CompareTo(new FractionalPosition(10)) == 0 && n.StringIndex == 0);
        Assert.Equal(3, lowNote.Fret);
        Assert.Equal(new FractionalPosition(1), lowNote.Sustain);
        Assert.True(lowNote.Vibrato);

        Assert.True(h.History.CanUndo);
        h.History.Undo();
        Assert.Single(level.Chords);
        Assert.Equal(noteCountBefore, level.Notes.Count);
    }

    /// <summary>An SMF format-0 file with two quarter notes (E2 then A2) at 480 PPQ.</summary>
    private static MidiFileData TwoNoteMidi()
    {
        var bytes = new List<byte>();
        bytes.AddRange("MThd"u8.ToArray());
        bytes.AddRange([0, 0, 0, 6]);
        bytes.AddRange([0, 0]);      // format 0
        bytes.AddRange([0, 1]);      // one track
        bytes.AddRange([0x01, 0xE0]); // 480 ppq

        var track = new List<byte>
        {
            0x00, 0x90, 40, 100,       // note on E2
            0x83, 0x60, 0x80, 40, 0,   // 480 ticks later: note off
            0x00, 0x90, 45, 100,       // note on A2
            0x83, 0x60, 0x80, 45, 0,   // note off
            0x00, 0xFF, 0x2F, 0x00     // end of track
        };

        bytes.AddRange("MTrk"u8.ToArray());
        bytes.AddRange([
            (byte)(track.Count >> 24), (byte)(track.Count >> 16), (byte)(track.Count >> 8), (byte)track.Count
        ]);
        bytes.AddRange(track);
        return MidiFileParser.Parse(bytes.ToArray());
    }

    /// <summary>
    ///     Internal rather than private so the deletion-undo tests can reuse the
    ///     same wiring — they assert the same property (the edit lands THROUGH the
    ///     history service) about two more commands.
    /// </summary>
    internal sealed class Harness
    {
        public Harness(ILevelSquashService? squash = null,
            DotCharter.Services.Import.Audio.IAudioSilenceSplicer? splicer = null,
            IArrangementAutoFixService? autoFix = null)
        {
            var logger = new FakeAppLogger();
            History = new HistoryService(logger);
            var tempo = new TempoService(logger);
            var config = new FakeConfigurationService();

            var project = new ChartProject();
            for (var i = 0; i < 32; i++)
            {
                project.Beats.Add(new BeatMarker
                {
                    Time = i * 500, Bpm = 120, BeatsInMeasure = 4, IsMeasure = i % 4 == 0
                });
            }

            var arrangement = new Arrangement { Name = "Lead", ArrangementType = ArrangementType.Lead };
            var level = new ChartLevel();
            // Six notes across beats 0..5 — beats 0..3 are the source phrase span.
            for (var beat = 0; beat < 6; beat++)
            {
                level.Notes.Add(new GuitarNote
                {
                    Position = new FractionalPosition(beat),
                    StringIndex = beat % 6,
                    Fret = 3 + beat % 3
                });
            }

            arrangement.Levels.Add(level);
            project.Arrangements.Add(arrangement);

            State.ActiveProject = project;
            State.ActiveArrangement = arrangement;
            State.ActiveLevel = level;

            var transport = new TransportViewModel(new FakeAudioPlaybackService(), State, config, FakeLocalization.KeysOnly());
            var trackContexts = new TrackContextRegistry(State, Selection);
            var editor = new ChartEditorViewModel(State, transport, History, Selection, tempo, trackContexts, FakeLocalization.KeysOnly());
            Editor = editor;

            var scope = new EditingScope(State);
            Session = new NoOpProjectSessionService();
            Maintenance = new ChartMaintenanceService(
                config,
                new NoOpImprovementService(),
                squash ?? new NoOpSquashService(),
                new LevelGenerationService(),
                new PhraseIterationPropagationService(History, scope, logger),
                new NoOpAnchorGenerationService(),
                Session,
                History,
                logger,
                State,
                editor,
                autoFix ?? new NoOpAutoFixService(),
                new RefretService(new FretAnchorGenerationService(), new FingeringGenerationService()),
                FakeLocalization.English(),
                splicer);
        }

        public ProjectState State { get; } = new();
        public SelectionService Selection { get; } = new();
        public HistoryService History { get; }
        public ChartMaintenanceService Maintenance { get; }

        /// <summary>The session stub the coordinator was given — carries the
        /// dirty-mark count the tempo-map lock test asserts on.</summary>
        public NoOpProjectSessionService Session { get; }

        /// <summary>
        ///     Exposed because the ACTIVE VOCAL TRACK lives here, not on
        ///     <see cref="ProjectState" /> — the deletion tests have to select one
        ///     before they can delete it.
        /// </summary>
        public ChartEditorViewModel Editor { get; private set; } = null!;
    }

    // The power tools under test never call these; stubbing them keeps the harness
    // to the collaborators that actually matter.
    private sealed class NoOpImprovementService : IChartImprovementService
    {
        public ChartImprovementResult ImproveProject(ChartProject? project, AppConfig config,
            double totalAudioLengthMs) => new();
    }

    private sealed class NoOpSquashService : ILevelSquashService
    {
        public bool TrySquash(Arrangement arrangement) => false;
    }

    private sealed class NoOpAnchorGenerationService : IFretAnchorGenerationService
    {
        public IReadOnlyList<FretAnchor> GenerateAnchors(ChartLevel level,
            FractionalPosition? rangeStart = null, FractionalPosition? rangeEnd = null,
            IReadOnlyList<ChordTemplate>? chordTemplates = null, int capo = 0,
            bool bassArrangement = false, Func<ChordTemplate, ChordTemplate?>? freeGrip = null,
            Arrangement? tuning = null) =>
            Array.Empty<FretAnchor>();
    }

    private sealed class NoOpAutoFixService : IArrangementAutoFixService
    {
        public void Fix(Arrangement arrangement, ArrangementFixResult result) { }
    }

    /// <summary>
    ///     Internal so the tempo-map tests can assert a coordinator recorded a
    ///     change the undo history cannot represent — see
    ///     <see cref="BeatMapLockDirtyTests" />.
    /// </summary>
    internal sealed class NoOpProjectSessionService : IProjectSessionService
    {
        public AppConfig AppConfig { get; } = new();
        public bool IsProjectDirty => false;

        /// <summary>How many times <see cref="MarkProjectDirty" /> was called.</summary>
        public int DirtyMarks { get; private set; }

        /// <summary>No shell is listening, so a replace is always allowed.</summary>
        public Func<Task<bool>>? ConfirmDiscardAsync { get; set; }

        public Task<bool> MayReplaceOpenProjectAsync() => Task.FromResult(true);
        public void MarkProjectClean() { }
        public void MarkProjectDirty() => DirtyMarks++;
        public event Action? ProjectDirtyChanged { add { } remove { } }
        public void Initialize() { }
        public void LoadAppConfig() { }
        public void SaveAppConfig() { }
        public void FreeAudio() { }
        public Task LoadFile(string filePath) => Task.CompletedTask;

        /// <summary>Every audio file the coordinator asked to load, in order.</summary>
        public List<string> LoadedAudio { get; } = [];

        public Task LoadAudioFile(string filePath, bool generateBeats = true, bool preferMemoryPlayback = false)
        {
            LoadedAudio.Add(filePath);
            return Task.CompletedTask;
        }

        public Task<bool> AttachAudioAsync(string sourcePath) => Task.FromResult(true);

        public Task LoadSilentTransportAsync(double projectLengthMs) => Task.CompletedTask;

        public Task OpenProject() => Task.CompletedTask;
        public Task<bool> SaveProjectAsync() => Task.FromResult(true);
        public Task<bool> SaveProjectAsAsync() => Task.FromResult(true);
        public IReadOnlyList<RecentProjectEntry> GetRecentProjects() => [];
        public event Action? RecentProjectsChanged { add { } remove { } }
        public void Dispose() { }
    }
}
