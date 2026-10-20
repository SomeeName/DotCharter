using DotCharter.Core.Domain.Vocals;
using DotCharter.Services.Export.Validation;

namespace DotCharter.Tests.Services.Export;

public sealed class ChartExportValidatorTests
{
    private static List<BeatMarker> CreateBeats(int count = 32)
    {
        var beats = new List<BeatMarker>(count);
        for (var i = 0; i < count; i++)
            beats.Add(new BeatMarker { Time = i * 500.0, IsMeasure = i % 4 == 0 });
        return beats;
    }

    private static FractionalPosition Pos(IList<BeatMarker> beats, double ms) => TimelineMath.PositionFromMs(ms, beats);

    private static ChartProject CreateValidProject()
    {
        var project = new ChartProject();
        foreach (var beat in CreateBeats()) project.Beats.Add(beat);
        var beats = project.Beats;

        var arrangement = new Arrangement { Name = "Lead" };
        arrangement.Phrases.AddRange(["COUNT", "riff", "END"]);
        arrangement.PhraseIterations.Add(new PhraseIteration(Pos(beats, 0), 0));
        arrangement.PhraseIterations.Add(new PhraseIteration(Pos(beats, 1500), 1));
        arrangement.PhraseIterations.Add(new PhraseIteration(Pos(beats, 14000), 2));

        var level = new ChartLevel();
        level.Anchors.Add(new FretAnchor(Pos(beats, 1500), 1));
        level.Notes.Add(new GuitarNote { Position = Pos(beats, 2000), StringIndex = 0, Fret = 3 });
        level.Notes.Add(new GuitarNote { Position = Pos(beats, 3000), StringIndex = 0, Fret = 5 });
        arrangement.Levels.Add(level);
        project.Arrangements.Add(arrangement);
        return project;
    }

    private static IReadOnlyList<ExportValidationFinding> Validate(ChartProject project) => new ChartExportValidator().Validate(project);

    [Fact]
    public void Display_names_the_game_font_lacks_are_a_warning_each()
    {
        var project = CreateValidProject();
        project.Chart.SongName = "ザホールブルーワールド";
        project.Chart.Artist = "Ave Mujica";
        project.Chart.AlbumName = "黒";

        var glyphs = Validate(project).Where(f => f.MessageKey == ChartExportValidator.KeyDisplayNameGlyphs).ToList();

        Assert.All(glyphs, f => Assert.Equal(ExportFindingSeverity.Warning, f.Severity));
        Assert.Equal(["ザホールブルーワールド", "黒"], glyphs.Select(f => f.Detail));
    }

    [Fact]
    public void Latin_display_names_raise_no_glyph_warning()
    {
        var project = CreateValidProject();
        project.Chart.SongName = "Déjà Vu – Live";
        project.Chart.Artist = "Beyoncé";

        Assert.DoesNotContain(Validate(project), f => f.MessageKey == ChartExportValidator.KeyDisplayNameGlyphs);
    }

    [Fact]
    public void Valid_project_produces_no_findings() => Assert.Empty(Validate(CreateValidProject()));

    [Fact]
    public void Missing_beat_map_is_an_error()
    {
        var finding = Assert.Single(Validate(new ChartProject()));
        Assert.Equal(ChartExportValidator.KeyNoBeatMap, finding.MessageKey);
        Assert.Equal(ExportFindingSeverity.Error, finding.Severity);
    }

    [Fact]
    public void Project_without_instrumental_arrangements_is_an_error()
    {
        var project = new ChartProject();
        foreach (var beat in CreateBeats()) project.Beats.Add(beat);

        Assert.Contains(Validate(project), f => f.MessageKey == ChartExportValidator.KeyNoArrangements);
    }

    [Fact]
    public void Notes_before_the_first_anchor_report_missing_fhp_coverage()
    {
        var project = CreateValidProject();
        var beats = project.Beats;
        project.Arrangements[0].Levels[0].Notes.Add(
            new GuitarNote { Position = Pos(beats, 1000), StringIndex = 1, Fret = 2 });

        var finding = Assert.Single(Validate(project),
            f => f.MessageKey == ChartExportValidator.KeyMissingFhpCoverage);
        Assert.Equal(ExportFindingSeverity.Error, finding.Severity);
        Assert.Equal(1000.0, finding.TimeMs!.Value, 1.0);
    }

    [Fact]
    public void Level_without_anchors_reports_missing_fhp_coverage_as_warning()
    {
        var project = CreateValidProject();
        project.Arrangements[0].Levels[0].Anchors.Clear();

        // Warning, not Error: the SNG mapper synthesizes a covering anchor,
        // so the export stays game-safe and must not be blocked.
        var finding = Assert.Single(Validate(project),
            f => f.MessageKey == ChartExportValidator.KeyMissingFhpCoverage);
        Assert.Equal(ExportFindingSeverity.Warning, finding.Severity);
    }

    [Fact]
    public void Overlapping_notes_on_the_same_string_are_an_error()
    {
        var project = CreateValidProject();
        var beats = project.Beats;
        var level = project.Arrangements[0].Levels[0];
        level.Notes.Add(new GuitarNote
        {
            Position = Pos(beats, 4000),
            StringIndex = 2,
            Fret = 5,
            Sustain = Pos(beats, 1000)
        });
        level.Notes.Add(new GuitarNote { Position = Pos(beats, 4500), StringIndex = 2, Fret = 7 });

        var finding = Assert.Single(Validate(project),
            f => f.MessageKey == ChartExportValidator.KeyOverlappingNotes);
        Assert.Equal("string 2", finding.Detail);
    }

    [Fact]
    public void Notes_on_different_strings_do_not_overlap()
    {
        var project = CreateValidProject();
        var beats = project.Beats;
        var level = project.Arrangements[0].Levels[0];
        level.Notes.Add(new GuitarNote { Position = Pos(beats, 2000), StringIndex = 3, Fret = 5 });

        Assert.DoesNotContain(Validate(project), f => f.MessageKey == ChartExportValidator.KeyOverlappingNotes);
    }

    [Fact]
    public void Phrase_without_iterations_is_reported_empty()
    {
        var project = CreateValidProject();
        project.Arrangements[0].Phrases.Add("unused");

        var finding = Assert.Single(Validate(project),
            f => f.MessageKey == ChartExportValidator.KeyEmptyPhrase);
        Assert.Equal("unused", finding.Detail);
        Assert.Equal(ExportFindingSeverity.Warning, finding.Severity);
    }

    [Fact]
    public void Count_and_end_phrases_are_not_reported_empty()
    {
        var project = CreateValidProject();
        // COUNT/END have iterations in the fixture; remove riff's iteration
        // and verify only riff is reported.
        project.Arrangements[0].PhraseIterations.RemoveAt(1);

        var empties = Validate(project)
            .Where(f => f.MessageKey == ChartExportValidator.KeyEmptyPhrase)
            .ToList();
        Assert.Equal("riff", Assert.Single(empties).Detail);
    }

    /// <summary>
    ///     A phrase PLACED on the timeline is used, even with no iteration yet.
    ///     <para>
    ///         Nothing in the editor writes <c>PhraseIterations</c> — placing a
    ///         phrase writes <c>EventPoint.Phrase</c>, and the id/iteration pair
    ///         is not built until <c>PhraseSectionGenerator</c> runs at export.
    ///         The GP and EOF importers meanwhile fill <c>Phrases</c> and never
    ///         add an iteration. So an imported, hand-edited chart reached the
    ///         export check with a full name list and an empty iteration list,
    ///         and every phrase in it was reported unused: "Phrase 'intro' is
    ///         never used", for phrases visibly placed all over the timeline.
    ///     </para>
    /// </summary>
    [Fact]
    public void Phrase_placed_as_an_event_point_is_not_reported_empty()
    {
        var project = CreateValidProject();
        var arrangement = project.Arrangements[0];
        arrangement.Phrases.Add("intro");
        arrangement.EventPoints.Add(new EventPoint
        {
            Position = Pos(project.Beats, 1500), Phrase = "intro"
        });

        Assert.DoesNotContain(Validate(project), f => f.MessageKey == ChartExportValidator.KeyEmptyPhrase);
    }

    /// <summary>Matching is trimmed and case-insensitive — the marker the charter
    /// typed and the imported name rarely agree on either.</summary>
    [Theory]
    [InlineData("Intro")]
    [InlineData(" intro ")]
    [InlineData("INTRO")]
    public void A_placed_phrase_matches_regardless_of_case_or_padding(string placed)
    {
        var project = CreateValidProject();
        var arrangement = project.Arrangements[0];
        arrangement.Phrases.Add("intro");
        arrangement.EventPoints.Add(new EventPoint
        {
            Position = Pos(project.Beats, 1500), Phrase = placed
        });

        Assert.DoesNotContain(Validate(project), f => f.MessageKey == ChartExportValidator.KeyEmptyPhrase);
    }

    /// <summary>A phrase that is genuinely nowhere is STILL reported — the point
    /// was to stop lying, not to stop checking.</summary>
    [Fact]
    public void A_phrase_that_is_neither_iterated_nor_placed_is_still_reported()
    {
        var project = CreateValidProject();
        var arrangement = project.Arrangements[0];
        arrangement.Phrases.Add("ghost");
        arrangement.EventPoints.Add(new EventPoint
        {
            Position = Pos(project.Beats, 1500), Phrase = "intro"
        });

        var finding = Assert.Single(Validate(project),
            f => f.MessageKey == ChartExportValidator.KeyEmptyPhrase);
        Assert.Equal("ghost", finding.Detail);
    }

    [Fact]
    public void Tone_change_without_slot_and_missing_base_are_reported()
    {
        var project = CreateValidProject();
        var beats = project.Beats;
        var arrangement = project.Arrangements[0];
        arrangement.ToneChanges.Add(new ToneChange { Position = Pos(beats, 2000), ToneName = "clean" });
        arrangement.ToneChanges.Add(new ToneChange { Position = Pos(beats, 3000), ToneName = "" });

        var findings = Validate(project);
        // "clean" resolves to a slot automatically, the unnamed change does not.
        Assert.Contains(findings, f => f.MessageKey == ChartExportValidator.KeyToneWithoutSlot);
        // ToneBase falls back to the first slot, so no missing-base warning.
        Assert.DoesNotContain(findings, f => f.MessageKey == ChartExportValidator.KeyMissingBaseTone);
    }

    [Fact]
    public void Referenced_tone_without_definition_is_a_warning_and_counted()
    {
        var project = CreateValidProject();
        var beats = project.Beats;
        var arrangement = project.Arrangements[0];
        arrangement.ToneA = "clean";
        arrangement.ToneB = "lead";
        arrangement.ToneChanges.Add(new ToneChange { Position = Pos(beats, 2000), ToneName = "clean" });
        project.ToneDefinitions.Add(new ToneDefinition { Key = "clean", Name = "Clean", Json = "{}" });

        var findings = Validate(project);
        var missing = Assert.Single(findings, f => f.MessageKey == ChartExportValidator.KeyToneNotDefined);
        Assert.Equal("lead", missing.Detail);
        Assert.Equal(ExportFindingSeverity.Warning, missing.Severity);

        var (referenced, defined) = ChartExportValidator.CountToneDefinitions(project);
        Assert.Equal(2, referenced);
        Assert.Equal(1, defined);
    }

    [Fact]
    public void Declared_and_defined_tones_produce_no_tone_findings()
    {
        var project = CreateValidProject();
        var beats = project.Beats;
        var arrangement = project.Arrangements[0];
        arrangement.ToneBase = "clean";
        arrangement.ToneA = "clean";
        arrangement.ToneChanges.Add(new ToneChange { Position = Pos(beats, 2000), ToneName = "clean" });
        // Definition matching is trim- + case-tolerant like the tone coordinator.
        project.ToneDefinitions.Add(new ToneDefinition { Key = " Clean ", Name = "Clean", Json = "{}" });

        var findings = Validate(project);
        Assert.DoesNotContain(findings, f => f.MessageKey == ChartExportValidator.KeyToneNotDefined);
        Assert.DoesNotContain(findings, f => f.MessageKey == ChartExportValidator.KeyToneWithoutSlot);

        var (referenced, defined) = ChartExportValidator.CountToneDefinitions(project);
        Assert.Equal(1, referenced);
        Assert.Equal(1, defined);
    }

    [Fact]
    public void More_than_four_distinct_tones_is_an_error()
    {
        var project = CreateValidProject();
        var beats = project.Beats;
        var arrangement = project.Arrangements[0];
        for (var i = 0; i < 5; i++)
        {
            arrangement.ToneChanges.Add(new ToneChange
            {
                Position = Pos(beats, 2000 + i * 500),
                ToneName = $"tone{i}"
            });
        }

        Assert.Contains(Validate(project), f => f.MessageKey == ChartExportValidator.KeyTooManyTones);
    }

    [Fact]
    public void Out_of_range_frets_are_errors()
    {
        var project = CreateValidProject();
        var beats = project.Beats;
        var level = project.Arrangements[0].Levels[0];
        level.Notes.Add(new GuitarNote { Position = Pos(beats, 5000), StringIndex = 4, Fret = 25 });

        var chord = new Chord { Position = Pos(beats, 6000), TemplateId = -1 };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 0, Fret = 30 });
        level.Chords.Add(chord);

        var findings = Validate(project)
            .Where(f => f.MessageKey == ChartExportValidator.KeyFretOutOfRange)
            .ToList();
        Assert.Equal(2, findings.Count);
        Assert.All(findings, f => Assert.Equal(ExportFindingSeverity.Error, f.Severity));
    }

    [Theory]
    [InlineData(ArrangementType.Lead, 6)] // a six-string guitar has no string 6 or 7
    [InlineData(ArrangementType.Bass, 8)] // the rs7string layout is guitar only (#736)
    [InlineData(ArrangementType.Lead, 9)] // past eight strings nothing can carry it
    public void Sounds_beyond_what_the_package_can_carry_are_errors(ArrangementType type, int stringCount)
    {
        var project = CreateValidProject();
        var beats = project.Beats;
        var arrangement = project.Arrangements[0];
        arrangement.ArrangementType = type;
        arrangement.StringCount = stringCount; // a legal model state (the rscp parser allows 1..12)
        var level = arrangement.Levels[0];

        // The SNG mapper folds these onto its last string, where they OVERWRITE
        // that string's real mask/slide/bend data. Nothing may ship that quietly.
        level.Notes.Add(new GuitarNote { Position = Pos(beats, 5000), StringIndex = 6, Fret = 3 });

        var chord = new Chord { Position = Pos(beats, 6000), TemplateId = -1 };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 0, Fret = 3 });
        chord.ChordNotes.Add(new ChordNote { StringIndex = 7, Fret = 5 });
        level.Chords.Add(chord);

        var findings = Validate(project)
            .Where(f => f.MessageKey == ChartExportValidator.KeyStringOutOfRange)
            .ToList();

        Assert.Equal(2, findings.Count);
        Assert.All(findings, f => Assert.Equal(ExportFindingSeverity.Error, f.Severity));
        Assert.Contains(findings, f => f.Detail == "6");
        Assert.Contains(findings, f => f.Detail == "7");
    }

    [Fact]
    public void A_seven_string_guitar_rejects_only_the_string_it_does_not_have()
    {
        var project = CreateValidProject();
        var beats = project.Beats;
        var arrangement = project.Arrangements[0];
        arrangement.StringCount = 7;
        var level = arrangement.Levels[0];
        level.Notes.Add(new GuitarNote { Position = Pos(beats, 5000), StringIndex = 6, Fret = 3 });
        level.Notes.Add(new GuitarNote { Position = Pos(beats, 5500), StringIndex = 7, Fret = 3 });

        var finding = Assert.Single(Validate(project), f => f.MessageKey == ChartExportValidator.KeyStringOutOfRange);
        Assert.Equal(ExportFindingSeverity.Error, finding.Severity);
        Assert.Equal("7", finding.Detail);
        Assert.Contains(Validate(project), f => f.MessageKey == ChartExportValidator.KeyExtendedRangeGuitar);
    }

    [Fact]
    public void A_six_string_arrangement_produces_no_string_range_findings()
    {
        var project = CreateValidProject();
        var beats = project.Beats;
        var level = project.Arrangements[0].Levels[0];
        for (var s = 0; s < 6; s++)
            level.Notes.Add(new GuitarNote { Position = Pos(beats, 5000 + s * 500), StringIndex = s, Fret = 3 });

        Assert.DoesNotContain(
            Validate(project), f => f.MessageKey == ChartExportValidator.KeyStringOutOfRange);
    }

    [Fact]
    public void Fretted_note_below_the_capo_is_an_error()
    {
        var project = CreateValidProject();
        var beats = project.Beats;
        var arrangement = project.Arrangements[0];
        // Capo 2, not 4: the fixture already carries notes at frets 3 and 5, so a
        // capo of 4 makes THOSE trip the rule and the test passes without ever
        // exercising the note it adds.
        arrangement.Capo = 2;
        arrangement.Levels[0].Notes.Add(new GuitarNote
        {
            Position = Pos(beats, 5000),
            StringIndex = 4,
            Fret = 1
        });

        Assert.Contains(Validate(project), f => f.MessageKey == ChartExportValidator.KeyNoteBelowCapo);
    }

    [Fact]
    public void Fretted_chord_note_below_the_capo_is_an_error_too()
    {
        // The chord-note loop checked fret range and string range but not the
        // capo, so the identical mistake blocked the export as a single note and
        // sailed through inside a chord — while the inspector flagged both as
        // L01. The editor called it blocking; the gate disagreed.
        var project = CreateValidProject();
        var beats = project.Beats;
        var arrangement = project.Arrangements[0];
        arrangement.Capo = 2;
        arrangement.Levels[0].Chords.Add(new Chord
        {
            Position = Pos(beats, 5000),
            ChordNotes =
            {
                new ChordNote { StringIndex = 4, Fret = 1 },
                new ChordNote { StringIndex = 5, Fret = 7 }
            }
        });

        Assert.Contains(Validate(project), f => f.MessageKey == ChartExportValidator.KeyNoteBelowCapo);
    }

    [Fact]
    public void An_open_chord_note_under_a_capo_is_not_below_it()
    {
        // Fret 0 IS the capo — an open string is capo-invariant and must never
        // be reported, or every capo'd chart with an open string fails export.
        var project = CreateValidProject();
        var beats = project.Beats;
        var arrangement = project.Arrangements[0];
        arrangement.Capo = 2;
        arrangement.Levels[0].Chords.Add(new Chord
        {
            Position = Pos(beats, 5000),
            ChordNotes =
            {
                new ChordNote { StringIndex = 4, Fret = 0 },
                new ChordNote { StringIndex = 5, Fret = 7 }
            }
        });

        Assert.DoesNotContain(Validate(project), f => f.MessageKey == ChartExportValidator.KeyNoteBelowCapo);
    }

    [Fact]
    public void First_note_within_the_first_second_is_a_warning()
    {
        var project = CreateValidProject();
        var beats = project.Beats;
        project.Arrangements[0].Levels[0].Anchors.Insert(0, new FretAnchor(Pos(beats, 0), 1));
        project.Arrangements[0].Levels[0].Notes.Add(new GuitarNote
        {
            Position = Pos(beats, 200),
            StringIndex = 5,
            Fret = 1
        });

        var finding = Assert.Single(Validate(project),
            f => f.MessageKey == ChartExportValidator.KeyFirstNoteTooEarly);
        Assert.Equal(ExportFindingSeverity.Warning, finding.Severity);
        Assert.Equal(200.0, finding.TimeMs!.Value, 1.0);
    }

    [Fact]
    public void Note_before_the_first_phrase_iteration_is_an_error()
    {
        var project = CreateValidProject();
        var beats = project.Beats;
        var arrangement = project.Arrangements[0];
        // Move all iterations after the notes.
        arrangement.PhraseIterations.Clear();
        arrangement.PhraseIterations.Add(new PhraseIteration(Pos(beats, 5000), 0));
        arrangement.PhraseIterations.Add(new PhraseIteration(Pos(beats, 6000), 1));
        arrangement.PhraseIterations.Add(new PhraseIteration(Pos(beats, 14000), 2));

        Assert.Contains(Validate(project), f => f.MessageKey == ChartExportValidator.KeyFirstPhraseNotAtStart);
    }

    [Fact]
    public void Overlong_lyrics_are_errors()
    {
        var project = CreateValidProject();
        var beats = project.Beats;
        var track = new VocalTrack { Name = "Vocals" };
        var phrase = new LyricPhrase { Position = Pos(beats, 4000) };
        phrase.Lyrics.Add(new LyricEvent
        {
            Position = Pos(beats, 4000),
            EndPosition = Pos(beats, 4500),
            Text = new string('x', 60)
        });
        track.Phrases.Add(phrase);
        project.VocalTracks.Add(track);

        var finding = Assert.Single(Validate(project),
            f => f.MessageKey == ChartExportValidator.KeyLyricTooLong);
        Assert.Equal("Vocals", finding.ArrangementName);
        Assert.Equal(ExportFindingSeverity.Error, finding.Severity);
    }

    [Fact]
    public void Arrangement_without_notes_is_an_error()
    {
        var project = CreateValidProject();
        var level = project.Arrangements[0].Levels[0];
        level.Notes.Clear();
        level.Chords.Clear();

        Assert.Contains(Validate(project), f => f.MessageKey == ChartExportValidator.KeyNoNotes);
    }
}
