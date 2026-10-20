namespace DotCharter.Tests.Services.Editing.Validation;

/// <summary>
///     Covers the three DotCharter-native pre-export gates the FEHLER inspector
///     now runs live: tone gaps, first-note silence and empty phrases. Exercised
///     end-to-end through <see cref="ArrangementValidationService"/> so the DI
///     rule ordering / context building is part of the contract.
/// </summary>
public sealed class ToneSilenceAndPhraseRuleTests
{
    // 120 BPM => 500 ms / beat. 16 beats => 8 seconds of grid.
    private static List<BeatMarker> Beats(int count = 16, double bpm = 120)
    {
        var ms = 60000.0 / bpm;
        return [.. Enumerable.Range(0, count).Select(i => new BeatMarker { Time = i * ms, Bpm = bpm })];
    }

    private static IArrangementValidationService NewService() =>
        new ArrangementValidationService(new FakeAppLogger());

    private static Arrangement NewArr()
    {
        var arr = new Arrangement { ArrangementType = ArrangementType.Lead };
        arr.Levels.Add(new ChartLevel());
        return arr;
    }

    private static GuitarNote Note(int beat, int fret = 5, int stringIdx = 0)
    {
        return new GuitarNote
        {
            Position = new FractionalPosition(beat),
            Fret = fret,
            StringIndex = stringIdx
        };
    }

    /// <summary>A project owning a body for each of <paramref name="keys" />.</summary>
    private static ChartProject ProjectWithBodies(params string[] keys)
    {
        var project = new ChartProject();
        foreach (var key in keys)
            project.ToneDefinitions.Add(new ToneDefinition { Key = key, Name = key, Json = "{}" });
        return project;
    }

    // ---- tone BODIES -----------------------------------------------------

    /// <summary>
    ///     THE REGRESSION. The gap check compares tone changes against the
    ///     arrangement's SLOTS and stops there, and bodies live on the PROJECT,
    ///     which validation could not see at all. So a chart that declares its
    ///     tones properly and owns not one body for them validated completely
    ///     clean and played nothing at every marker — the state of every
    ///     psarc-opened chart before the loader imported bodies.
    /// </summary>
    [Fact]
    public void Detects_ToneChange_DeclaredButWithNoBodyInTheProject()
    {
        var arr = NewArr();
        arr.Levels[0].Notes.Add(Note(4));
        arr.ToneA = "solo";
        arr.ToneChanges.Add(new ToneChange { Position = new FractionalPosition(4), ToneName = "solo" });
        var project = ProjectWithBodies("something_else");

        var issues = NewService().Validate(arr, Beats(), null, project);

        // Consistent chart, so the ORIGINAL gate is silent — that is the point.
        Assert.DoesNotContain(issues, i => i.Type == ArrangementIssueType.ToneChangeWithoutMatchingTone);
        Assert.Contains(issues, i => i.Type == ArrangementIssueType.ToneChangeWithoutDefinition);
    }

    [Fact]
    public void DoesNot_Flag_ToneChange_WhenTheProjectOwnsItsBody()
    {
        var arr = NewArr();
        arr.Levels[0].Notes.Add(Note(4));
        arr.ToneA = "solo";
        arr.ToneChanges.Add(new ToneChange { Position = new FractionalPosition(4), ToneName = "solo" });

        var issues = NewService().Validate(arr, Beats(), null, ProjectWithBodies("solo"));

        Assert.DoesNotContain(issues, i => i.Type == ArrangementIssueType.ToneChangeWithoutDefinition);
    }

    /// <summary>
    ///     Null project means UNKNOWN, never "empty". A caller that does not
    ///     supply one must get no project-level findings rather than every tone in
    ///     the chart reported as missing.
    /// </summary>
    [Fact]
    public void WithNoProject_TheBodyCheckSaysNothingAtAll()
    {
        var arr = NewArr();
        arr.Levels[0].Notes.Add(Note(4));
        arr.ToneA = "solo";
        arr.ToneChanges.Add(new ToneChange { Position = new FractionalPosition(4), ToneName = "solo" });

        var issues = NewService().Validate(arr, Beats());

        Assert.DoesNotContain(issues, i => i.Type == ArrangementIssueType.ToneChangeWithoutDefinition);
    }

    /// <summary>An UNDECLARED tone is the gap check's finding, not this one — two
    /// issues on one marker would say the same thing twice.</summary>
    [Fact]
    public void AnUndeclaredTone_IsReportedOnceAsAGapAndNotAlsoAsAMissingBody()
    {
        var arr = NewArr();
        arr.Levels[0].Notes.Add(Note(4));
        arr.ToneChanges.Add(new ToneChange { Position = new FractionalPosition(4), ToneName = "typo" });

        var issues = NewService().Validate(arr, Beats(), null, ProjectWithBodies("solo"));

        Assert.Contains(issues, i => i.Type == ArrangementIssueType.ToneChangeWithoutMatchingTone);
        Assert.DoesNotContain(issues, i => i.Type == ArrangementIssueType.ToneChangeWithoutDefinition);
    }

    /// <summary>Matching follows the rig's own lookup — trimmed, case-insensitive,
    /// key OR name — so a body the rig would find is never called missing.</summary>
    [Fact]
    public void ABodyMatchedOnlyByCaseOrPadding_IsStillABody()
    {
        var arr = NewArr();
        arr.Levels[0].Notes.Add(Note(4));
        arr.ToneA = "  SOLO ";
        arr.ToneChanges.Add(new ToneChange { Position = new FractionalPosition(4), ToneName = "solo" });

        var issues = NewService().Validate(arr, Beats(), null, ProjectWithBodies("Solo"));

        Assert.DoesNotContain(issues, i => i.Type == ArrangementIssueType.ToneChangeWithoutDefinition);
        Assert.DoesNotContain(issues, i => i.Type == ArrangementIssueType.ToneChangeWithoutMatchingTone);
    }

    /// <summary>
    ///     …and the inspector must actually PASS the project, or the whole check
    ///     is dead code that keeps its own passing unit tests. The `project` param
    ///     is optional and defaults to null precisely so old callers compile — the
    ///     shape that let <c>CoopRigApplier</c> ship registered-and-unreached for a
    ///     release.
    /// </summary>
    [Fact]
    public void TheInspectorPassesTheProjectSoTheBodyCheckIsReached()
    {
        var issuesVm = UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(),
            "DotCharter.UI", "ViewModels", "Editor", "Inspector", "InspectorIssuesViewModel.cs")));

        Assert.Contains("_projectState.ActiveProject)", issuesVm, StringComparison.Ordinal);

        var lint = UiSourceTree.ReadAllText(Path.Combine(TestPaths.RepoRoot(),
            "DotCharter.Services", "Editing", "Linting", "BatchChartLintService.cs"));
        Assert.Contains("audioLengthMs, project)", lint, StringComparison.Ordinal);
    }

    // ---- tone gaps -------------------------------------------------------

    [Fact]
    public void Detects_ToneChange_WithNoDeclaredTone()
    {
        var arr = NewArr();
        arr.Levels[0].Notes.Add(Note(4)); // 2000 ms, keeps silence rule quiet
        arr.ToneChanges.Add(new ToneChange { Position = new FractionalPosition(4), ToneName = "solo" });

        var issues = NewService().Validate(arr, Beats());

        Assert.Contains(issues, i => i.Type == ArrangementIssueType.ToneChangeWithoutMatchingTone);
    }

    [Fact]
    public void DoesNot_Flag_ToneChange_WhenToneDeclaredInSlot()
    {
        var arr = NewArr();
        arr.Levels[0].Notes.Add(Note(4));
        arr.ToneA = "solo";
        arr.ToneChanges.Add(new ToneChange { Position = new FractionalPosition(4), ToneName = "solo" });

        var issues = NewService().Validate(arr, Beats());

        Assert.DoesNotContain(issues, i => i.Type == ArrangementIssueType.ToneChangeWithoutMatchingTone);
    }

    [Fact]
    public void DoesNot_Flag_ToneChange_WhenToneDeclaredInToneNames_CaseInsensitive()
    {
        var arr = NewArr();
        arr.Levels[0].Notes.Add(Note(4));
        arr.ToneNames.Add("Clean");
        arr.ToneChanges.Add(new ToneChange { Position = new FractionalPosition(4), ToneName = "clean" });

        var issues = NewService().Validate(arr, Beats());

        Assert.DoesNotContain(issues, i => i.Type == ArrangementIssueType.ToneChangeWithoutMatchingTone);
    }

    [Fact]
    public void Detects_ToneChange_WithBlankName()
    {
        var arr = NewArr();
        arr.Levels[0].Notes.Add(Note(4));
        arr.ToneChanges.Add(new ToneChange { Position = new FractionalPosition(4), ToneName = "  " });

        var issues = NewService().Validate(arr, Beats());

        Assert.Contains(issues, i => i.Type == ArrangementIssueType.ToneChangeWithoutMatchingTone);
    }

    [Fact]
    public void ToneGap_Issue_HasTimeForClickToJump()
    {
        var arr = NewArr();
        arr.Levels[0].Notes.Add(Note(4));
        arr.ToneChanges.Add(new ToneChange { Position = new FractionalPosition(6), ToneName = "solo" });

        var issue = NewService().Validate(arr, Beats())
            .First(i => i.Type == ArrangementIssueType.ToneChangeWithoutMatchingTone);

        Assert.True(issue.HasTime);
        Assert.Equal("D01", issue.Code);
        Assert.Equal(ArrangementIssueSeverity.Important, issue.Severity);
    }

    // ---- first-note silence ----------------------------------------------

    [Fact]
    public void Detects_FirstNoteTooEarly_WhenNoteBefore1Second()
    {
        var arr = NewArr();
        arr.Levels[0].Notes.Add(Note(1)); // 500 ms < 1000 ms

        var issues = NewService().Validate(arr, Beats());

        var issue = Assert.Single(issues, i => i.Type == ArrangementIssueType.FirstNoteTooEarly);
        Assert.NotNull(issue.Note); // back-ref so the inspector can select it
        Assert.True(issue.HasTime);
        Assert.Equal("D02", issue.Code);
    }

    [Fact]
    public void DoesNot_Flag_FirstNoteTooEarly_WhenLeadInIsEnough()
    {
        var arr = NewArr();
        arr.Levels[0].Notes.Add(Note(4)); // 2000 ms

        var issues = NewService().Validate(arr, Beats());

        Assert.DoesNotContain(issues, i => i.Type == ArrangementIssueType.FirstNoteTooEarly);
    }

    [Fact]
    public void FirstNoteSilence_UsesEarliestChord_WhenChordIsFirst()
    {
        var arr = NewArr();
        var chord = new Chord { Position = new FractionalPosition(1) }; // 500 ms
        chord.ChordNotes.Add(new ChordNote { StringIndex = 0, Fret = 5 });
        arr.Levels[0].Chords.Add(chord);
        arr.Levels[0].Notes.Add(Note(4));

        var issue = NewService().Validate(arr, Beats())
            .First(i => i.Type == ArrangementIssueType.FirstNoteTooEarly);

        Assert.NotNull(issue.Chord);
        Assert.Null(issue.Note);
    }

    [Fact]
    public void DoesNot_Flag_FirstNoteTooEarly_OnEmptyArrangement()
    {
        var issues = NewService().Validate(NewArr(), Beats());
        Assert.DoesNotContain(issues, i => i.Type == ArrangementIssueType.FirstNoteTooEarly);
    }

    // ---- empty phrases ---------------------------------------------------

    [Fact]
    public void Detects_EmptyPhrase_WhenNamedPhraseHasNoNotes()
    {
        var arr = NewArr();
        // Content lives in the "verse" region only; "solo" spans nothing.
        arr.EventPoints.Add(new EventPoint { Position = new FractionalPosition(0), Phrase = "intro" });
        arr.EventPoints.Add(new EventPoint { Position = new FractionalPosition(4), Phrase = "verse" });
        arr.EventPoints.Add(new EventPoint { Position = new FractionalPosition(8), Phrase = "solo" });
        arr.EventPoints.Add(new EventPoint { Position = new FractionalPosition(12), Phrase = "END" });
        arr.Levels[0].Notes.Add(Note(5)); // 2500 ms → inside [2000, 4000) verse

        var issues = NewService().Validate(arr, Beats());

        Assert.Contains(issues, i => i.Type == ArrangementIssueType.EmptyPhrase);
    }

    [Fact]
    public void DoesNot_Flag_EmptyPhrase_WhenPhraseHasNotes()
    {
        var arr = NewArr();
        arr.EventPoints.Add(new EventPoint { Position = new FractionalPosition(0), Phrase = "intro" });
        arr.EventPoints.Add(new EventPoint { Position = new FractionalPosition(4), Phrase = "verse" });
        arr.EventPoints.Add(new EventPoint { Position = new FractionalPosition(8), Phrase = "END" });
        arr.Levels[0].Notes.Add(Note(5)); // verse region [2000, 4000)

        var issues = NewService().Validate(arr, Beats());

        Assert.DoesNotContain(issues, i => i.Type == ArrangementIssueType.EmptyPhrase);
    }

    [Fact]
    public void DoesNot_Flag_EmptyPhrase_InsideANoguitarSection()
    {
        var arr = NewArr();
        arr.EventPoints.Add(new EventPoint { Position = new FractionalPosition(0), Phrase = "COUNT" });
        arr.EventPoints.Add(new EventPoint { Position = new FractionalPosition(4), Phrase = "verse", Section = "verse" });
        // EOF names the phrase freely; the SECTION is what says "no guitar".
        arr.EventPoints.Add(new EventPoint { Position = new FractionalPosition(8), Phrase = "p3", Section = "noguitar" });
        arr.EventPoints.Add(new EventPoint { Position = new FractionalPosition(10), Phrase = "chorus", Section = "chorus" });
        arr.EventPoints.Add(new EventPoint { Position = new FractionalPosition(14), Phrase = "END" });
        arr.Levels[0].Notes.Add(Note(5));
        arr.Levels[0].Notes.Add(Note(11));

        var issues = NewService().Validate(arr, Beats());

        Assert.DoesNotContain(issues, i => i.Type == ArrangementIssueType.EmptyPhrase);
    }

    [Fact]
    public void DoesNot_Flag_EmptyPhrase_ForFirstOrStructuralPhrases()
    {
        var arr = NewArr();
        // First phrase empty (allowed), noguitar empty (allowed), END empty (allowed).
        arr.EventPoints.Add(new EventPoint { Position = new FractionalPosition(0), Phrase = "COUNT" });
        arr.EventPoints.Add(new EventPoint { Position = new FractionalPosition(4), Phrase = "verse" });
        arr.EventPoints.Add(new EventPoint { Position = new FractionalPosition(8), Phrase = "noguitar" });
        arr.EventPoints.Add(new EventPoint { Position = new FractionalPosition(12), Phrase = "END" });
        arr.Levels[0].Notes.Add(Note(5)); // verse region only

        var issues = NewService().Validate(arr, Beats());

        // Only "noguitar" and "END" are empty besides the first — all excluded.
        Assert.DoesNotContain(issues, i => i.Type == ArrangementIssueType.EmptyPhrase);
    }

    [Fact]
    public void DoesNot_Flag_EmptyPhrase_WhenArrangementHasNoContent()
    {
        var arr = NewArr();
        arr.EventPoints.Add(new EventPoint { Position = new FractionalPosition(0), Phrase = "intro" });
        arr.EventPoints.Add(new EventPoint { Position = new FractionalPosition(4), Phrase = "verse" });

        var issues = NewService().Validate(arr, Beats());

        Assert.DoesNotContain(issues, i => i.Type == ArrangementIssueType.EmptyPhrase);
    }

    /// <summary>
    ///     D03 keyed on <c>EventPoint.Phrase</c> alone, so it found ZERO phrases
    ///     in every RS-XML / psarc import and reported "no empty phrases" on a
    ///     chart full of them. See <c>Core/Utilities/PhraseMarkers</c> — the two
    ///     phrase stores, and why reading one is a silent whole-feature failure.
    /// </summary>
    [Fact]
    public void Detects_EmptyPhrase_OnAnImportedChart()
    {
        var arr = NewArr();
        foreach (var name in new[] { "intro", "verse", "solo", "END" })
            arr.Phrases.Add(name);
        arr.PhraseIterations.Add(new PhraseIteration(new FractionalPosition(0), 0));
        arr.PhraseIterations.Add(new PhraseIteration(new FractionalPosition(4), 1));
        arr.PhraseIterations.Add(new PhraseIteration(new FractionalPosition(8), 2));
        arr.PhraseIterations.Add(new PhraseIteration(new FractionalPosition(12), 3));
        arr.Levels[0].Notes.Add(Note(5)); // verse region only; "solo" spans nothing

        var issues = NewService().Validate(arr, Beats());

        Assert.Contains(issues, i => i.Type == ArrangementIssueType.EmptyPhrase);
    }

    [Fact]
    public void DoesNot_Flag_EmptyPhrase_OnAnImportedChartWhereEveryPhrasePlays()
    {
        var arr = NewArr();
        foreach (var name in new[] { "intro", "verse", "END" })
            arr.Phrases.Add(name);
        arr.PhraseIterations.Add(new PhraseIteration(new FractionalPosition(0), 0));
        arr.PhraseIterations.Add(new PhraseIteration(new FractionalPosition(4), 1));
        arr.PhraseIterations.Add(new PhraseIteration(new FractionalPosition(8), 2));
        arr.Levels[0].Notes.Add(Note(5));

        var issues = NewService().Validate(arr, Beats());

        Assert.DoesNotContain(issues, i => i.Type == ArrangementIssueType.EmptyPhrase);
    }
}
