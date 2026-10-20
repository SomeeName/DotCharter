namespace DotCharter.Tests.Services.Editing.Validation;

public sealed class ArrangementValidationServiceTests
{
    // 120 BPM => 500 ms / beat. 8 beats => total 4 seconds.
    private static List<BeatMarker> Beats(int count = 8, double bpm = 120)
    {
        var ms = 60000.0 / bpm;
        return [.. Enumerable.Range(0, count).Select(i => new BeatMarker { Time = i * ms, Bpm = bpm })];
    }

    private static IArrangementValidationService NewService() => new ArrangementValidationService(new FakeAppLogger());

    private static Arrangement NewArr(ArrangementType type = ArrangementType.Lead)
    {
        var arr = new Arrangement { ArrangementType = type };
        arr.Levels.Add(new ChartLevel());
        return arr;
    }

    private static GuitarNote Note(int beat, int fret, int stringIdx = 0,
        int sustainBeats = 0, int sustainFractionDen = 0, int sustainFractionNum = 0)
    {
        var sustain = sustainBeats == 0 && sustainFractionDen == 0
            ? FractionalPosition.Zero
            : new FractionalPosition(sustainBeats,
                sustainFractionDen == 0 ? Fraction.Zero : new Fraction(sustainFractionNum, sustainFractionDen));
        return new GuitarNote
        {
            Position = new FractionalPosition(beat),
            Sustain = sustain,
            Fret = fret,
            StringIndex = stringIdx
        };
    }

    [Fact]
    public void EmptyArrangement_ProducesNoIssues()
    {
        var issues = NewService().Validate(NewArr(), Beats());
        Assert.Empty(issues);
    }

    /// <summary>
    ///     A chart with SOUNDS but no phrases at all is the commonest real fault
    ///     — a tab import only produces phrases when the tab carried section
    ///     markers — and it is unambiguously missing its END. The check used to
    ///     sit behind a <c>phrases.Count &lt; 2</c> guard, so the loudest case said
    ///     nothing while `ChartExportValidator` reported it: the inspector and
    ///     the export dialog disagreed in the direction that lets a broken chart
    ///     through.
    /// </summary>
    [Fact]
    public void AChartWithSoundsAndNoPhrasesAtAll_IsReportedAsMissingItsEndPhrase()
    {
        var arr = NewArr();
        arr.Levels[0].Notes.Add(Note(1, 5));

        var issues = NewService().Validate(arr, Beats());

        Assert.Contains(issues, i => i.Type == ArrangementIssueType.NoEndPhrase);
    }

    [Fact]
    public void AnEmptyArrangement_IsStillNotSpammedWithAMissingEndPhrase()
    {
        // The other side of the same rule: an arrangement with nothing in it has
        // nothing to end, and not spamming a blank one is deliberate.
        var issues = NewService().Validate(NewArr(), Beats());

        Assert.DoesNotContain(issues, i => i.Type == ArrangementIssueType.NoEndPhrase);
    }

    /// <summary>
    ///     A rule that throws checked NOTHING, and a shorter list with no marker
    ///     reads exactly like a cleaner chart. The crash used to be logged and
    ///     otherwise invisible.
    /// </summary>
    [Fact]
    public void ACrashingRule_IsReportedRatherThanSilentlyDroppingItsChecks()
    {
        var service = new ArrangementValidationService(
            new FakeAppLogger(), new IArrangementRule[] { new ThrowingRule() });

        var issues = service.Validate(NewArr(), Beats());

        var issue = Assert.Single(issues);
        Assert.Equal(ArrangementIssueType.ValidationRuleCrashed, issue.Type);
        Assert.Contains(nameof(ThrowingRule), issue.Message, StringComparison.Ordinal);
    }

    private sealed class ThrowingRule : IArrangementRule
    {
        public void Validate(ArrangementValidationContext context, ICollection<ArrangementIssue> issues)
            => throw new InvalidOperationException("boom");
    }

    [Fact]
    public void NullArrangement_ReturnsEmpty()
    {
        var issues = NewService().Validate(null, Beats());
        Assert.Empty(issues);
    }

    /// <summary>
    ///     No beat grid means nothing CAN be judged — every rule positions its
    ///     finding on the beat map — so the validator says so instead of
    ///     returning an empty list. "I could not check" and "I found nothing"
    ///     are different answers and only one of them means the chart is fine;
    ///     `ChartExportValidator` already treats this state as a hard error, so
    ///     an empty result put the inspector in open disagreement with the
    ///     export dialog. (This test asserted `Empty` until 2026-08-22 — it
    ///     pinned the swallow.)
    /// </summary>
    [Fact]
    public void EmptyBeats_ReportsThatNoCheckCouldRun()
    {
        var issues = NewService().Validate(NewArr(), []);

        var issue = Assert.Single(issues);
        Assert.Equal(ArrangementIssueType.NoBeatGridSoNoChecksRan, issue.Type);
        Assert.Equal(ArrangementIssueSeverity.Important, issue.Type.GetSeverity());
    }

    [Fact]
    public void Detects_FretNumberMoreThan24()
    {
        var arr = NewArr();
        arr.Levels[0].Notes.Add(Note(1, 30));
        var issues = NewService().Validate(arr, Beats());
        Assert.Contains(issues, i => i.Type == ArrangementIssueType.FretNumberMoreThan24);
    }

    [Fact]
    public void Does_Not_Flag_FretsAtOrBelow24()
    {
        var arr = NewArr();
        arr.Levels[0].Notes.Add(Note(1, 24));
        var issues = NewService().Validate(arr, Beats());
        Assert.DoesNotContain(issues, i => i.Type == ArrangementIssueType.FretNumberMoreThan24);
    }

    [Fact]
    public void Detects_NoteAfterSongEnd()
    {
        var arr = NewArr();
        arr.Levels[0].Notes.Add(Note(6, 5)); // 6 * 500 = 3000 ms
        var issues = NewService().Validate(arr, Beats(), 2000);
        Assert.Contains(issues, i => i.Type == ArrangementIssueType.NoteAfterSongEnd);
    }

    [Fact]
    public void Does_Not_Flag_NoteAfterSongEnd_WhenWithinAudio()
    {
        var arr = NewArr();
        arr.Levels[0].Notes.Add(Note(1, 5));
        var issues = NewService().Validate(arr, Beats(), 4000);
        Assert.DoesNotContain(issues, i => i.Type == ArrangementIssueType.NoteAfterSongEnd);
    }

    /// <summary>
    ///     The I29 flood (live audit P0-4): a raw audio length of 0 means "no
    ///     audio decoded", not "the song ends at 0:00" — so an audio-less GP
    ///     import used to get one I29 for EVERY sound (1,011 duplicates on one
    ///     chart). No audio ⇒ zero per-sound I29s and exactly ONE summarized
    ///     D05 stating how many sounds went unchecked.
    /// </summary>
    [Theory]
    [InlineData(0.0)]      // "no audio decoded" — the flood case
    [InlineData(-1.0)]
    public void NoAudio_EmitsOneSummary_NeverAPerNoteFlood(double rawAudioLength)
    {
        var arr = NewArr();
        for (var beat = 0; beat < 6; beat++)
            arr.Levels[0].Notes.Add(Note(beat, 5));
        arr.Levels[0].Chords.Add(new Chord { Position = new FractionalPosition(6) });

        var issues = NewService().Validate(arr, Beats(), rawAudioLength);

        Assert.DoesNotContain(issues, i => i.Type == ArrangementIssueType.NoteAfterSongEnd);
        var summary = Assert.Single(issues, i => i.Type == ArrangementIssueType.SongEndUnverifiedNoAudio);
        Assert.Contains("7 sounds", summary.Message); // the honest unchecked count
        Assert.False(summary.HasTime); // non-positional — nothing to jump to
    }

    /// <summary>…and the OTHER branch: with audio attached, per-sound I29
    /// stays per-sound and the D05 summary never appears.</summary>
    [Fact]
    public void WithAudio_PerNoteI29Stays_AndNoSummaryAppears()
    {
        var arr = NewArr();
        arr.Levels[0].Notes.Add(Note(5, 5)); // 2500 ms
        arr.Levels[0].Notes.Add(Note(6, 5)); // 3000 ms
        var issues = NewService().Validate(arr, Beats(), 2000);

        Assert.Equal(2, issues.Count(i => i.Type == ArrangementIssueType.NoteAfterSongEnd));
        Assert.DoesNotContain(issues, i => i.Type == ArrangementIssueType.SongEndUnverifiedNoAudio);
    }

    /// <summary>An empty arrangement has nothing unchecked — no audio must not
    /// mint a warning on a fresh project.</summary>
    [Fact]
    public void NoAudio_EmptyArrangement_ProducesNoSummary()
    {
        var issues = NewService().Validate(NewArr(), Beats(), 0);
        Assert.Empty(issues);
    }

    [Fact]
    public void Detects_InvalidBassArrangementString()
    {
        var arr = NewArr(ArrangementType.Bass);
        arr.Levels[0].Notes.Add(Note(1, 5, 4));
        var issues = NewService().Validate(arr, Beats());
        Assert.Contains(issues, i => i.Type == ArrangementIssueType.InvalidBassArrangementString);
    }

    [Fact]
    public void DoesNot_Flag_BassString_OnLead()
    {
        var arr = NewArr();
        arr.Levels[0].Notes.Add(Note(1, 5, 5));
        var issues = NewService().Validate(arr, Beats());
        Assert.DoesNotContain(issues, i => i.Type == ArrangementIssueType.InvalidBassArrangementString);
    }

    [Fact]
    public void Detects_LinkNextMissingTargetNote()
    {
        var arr = NewArr();
        var n = Note(1, 5, sustainBeats: 1);
        n.LinkNext = true;
        arr.Levels[0].Notes.Add(n);
        var issues = NewService().Validate(arr, Beats());
        Assert.Contains(issues, i => i.Type == ArrangementIssueType.LinkNextMissingTargetNote);
    }

    [Fact]
    public void Detects_LinkNextFretMismatch()
    {
        var arr = NewArr();
        var n1 = Note(1, 5, sustainBeats: 1);
        n1.LinkNext = true;
        var n2 = Note(2, 7); // different fret => mismatch
        arr.Levels[0].Notes.Add(n1);
        arr.Levels[0].Notes.Add(n2);
        var issues = NewService().Validate(arr, Beats());
        Assert.Contains(issues, i => i.Type == ArrangementIssueType.LinkNextFretMismatch);
    }

    [Fact]
    public void Does_Not_Flag_LinkNextFretMismatch_WhenSlideToTargetMatches()
    {
        var arr = NewArr();
        var n1 = Note(1, 5, sustainBeats: 1);
        n1.LinkNext = true;
        n1.SlideTo = 7;
        var n2 = Note(2, 7);
        arr.Levels[0].Notes.Add(n1);
        arr.Levels[0].Notes.Add(n2);
        var issues = NewService().Validate(arr, Beats());
        Assert.DoesNotContain(issues, i => i.Type == ArrangementIssueType.LinkNextFretMismatch);
    }

    [Fact]
    public void Detects_HopoIntoSameNote()
    {
        var arr = NewArr();
        arr.Levels[0].Notes.Add(Note(1, 5));
        var hopo = Note(2, 5);
        hopo.Hopo = HopoType.HammerOn;
        arr.Levels[0].Notes.Add(hopo);
        var issues = NewService().Validate(arr, Beats());
        Assert.Contains(issues, i => i.Type == ArrangementIssueType.HopoIntoSameNote);
    }

    /// <summary>#401: a repeated tap, and a hammer-on from nowhere after other strings were played,
    /// repeat the fret on purpose; a pull-off onto the same fret never can.</summary>
    [Fact]
    public void HopoIntoSameNote_SkipsTaps_AndHammerOnsFromNowhere_ButNotPullOffs()
    {
        var arr = NewArr();
        var notes = arr.Levels[0].Notes;
        notes.Add(Note(1, 14));
        var tap = Note(2, 14);
        tap.Hopo = HopoType.Tap;
        notes.Add(tap);

        var first = Note(3, 10);
        first.StringIndex = 3;
        notes.Add(first);
        var between = Note(4, 14);
        between.Hopo = HopoType.Tap;
        notes.Add(between);
        var fromNowhere = Note(5, 10);
        fromNowhere.StringIndex = 3;
        fromNowhere.Hopo = HopoType.HammerOn;
        notes.Add(fromNowhere);

        var pullFrom = Note(6, 7);
        pullFrom.StringIndex = 1;
        notes.Add(pullFrom);
        var pull = Note(7, 7);
        pull.StringIndex = 1;
        pull.Hopo = HopoType.PullOff;
        notes.Add(pull);

        var flagged = NewService().Validate(arr, Beats())
            .Where(i => i.Type == ArrangementIssueType.HopoIntoSameNote).Select(i => i.Note).ToList();

        Assert.Equal([pull], flagged);
    }

    [Fact]
    public void Does_Not_Flag_Hopo_OnDifferentFret()
    {
        var arr = NewArr();
        arr.Levels[0].Notes.Add(Note(1, 5));
        var hopo = Note(2, 7);
        hopo.Hopo = HopoType.HammerOn;
        arr.Levels[0].Notes.Add(hopo);
        var issues = NewService().Validate(arr, Beats());
        Assert.DoesNotContain(issues, i => i.Type == ArrangementIssueType.HopoIntoSameNote);
    }

    [Fact]
    public void Detects_TechniqueNoteWithoutSustain()
    {
        var arr = NewArr();
        var n = Note(1, 5);
        n.Vibrato = true;
        arr.Levels[0].Notes.Add(n);
        var issues = NewService().Validate(arr, Beats());
        Assert.Contains(issues, i => i.Type == ArrangementIssueType.TechniqueNoteWithoutSustain);
    }

    [Fact]
    public void Detects_AnchorInsideHandShape()
    {
        var arr = NewArr();
        arr.Levels[0].HandShapes.Add(new HandShape
        {
            Position = new FractionalPosition(1),
            EndPosition = new FractionalPosition(3)
        });
        arr.Levels[0].Anchors.Add(new FretAnchor(new FractionalPosition(2), 5));
        var issues = NewService().Validate(arr, Beats());
        Assert.Contains(issues, i => i.Type == ArrangementIssueType.AnchorInsideHandShape);
    }

    [Fact]
    public void Detects_MutedStringInNonMutedChord()
    {
        var arr = NewArr();
        var chord = new Chord { Position = new FractionalPosition(1) };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 0, Fret = 5, Mute = MuteType.None });
        chord.ChordNotes.Add(new ChordNote { StringIndex = 1, Fret = 5, Mute = MuteType.Full });
        arr.Levels[0].Chords.Add(chord);
        var issues = NewService().Validate(arr, Beats());
        Assert.Contains(issues, i => i.Type == ArrangementIssueType.MutedStringInNonMutedChord);
    }

    [Fact]
    public void Does_Not_Flag_FullyMutedChord()
    {
        var arr = NewArr();
        var chord = new Chord { Position = new FractionalPosition(1) };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 0, Fret = 5, Mute = MuteType.Full });
        chord.ChordNotes.Add(new ChordNote { StringIndex = 1, Fret = 5, Mute = MuteType.Full });
        arr.Levels[0].Chords.Add(chord);
        var issues = NewService().Validate(arr, Beats());
        Assert.DoesNotContain(issues, i => i.Type == ArrangementIssueType.MutedStringInNonMutedChord);
    }

    [Fact]
    public void Detects_NoEndPhrase_WhenPhrasesPresentButNoEnd()
    {
        var arr = NewArr();
        arr.EventPoints.Add(new EventPoint { Position = new FractionalPosition(0), Phrase = "intro" });
        arr.EventPoints.Add(new EventPoint { Position = new FractionalPosition(2), Phrase = "verse" });
        var issues = NewService().Validate(arr, Beats());
        Assert.Contains(issues, i => i.Type == ArrangementIssueType.NoEndPhrase);
    }

    [Fact]
    public void Does_Not_Flag_NoEndPhrase_WhenEndPresent()
    {
        var arr = NewArr();
        arr.EventPoints.Add(new EventPoint { Position = new FractionalPosition(0), Phrase = "intro" });
        arr.EventPoints.Add(new EventPoint { Position = new FractionalPosition(4), Phrase = "END" });
        var issues = NewService().Validate(arr, Beats());
        Assert.DoesNotContain(issues, i => i.Type == ArrangementIssueType.NoEndPhrase);
    }

    [Fact]
    public void Detects_FirstPhraseNotEmpty_WhenNoteBeforeSecondPhrase()
    {
        var arr = NewArr();
        arr.EventPoints.Add(new EventPoint { Position = new FractionalPosition(0), Phrase = "intro" });
        arr.EventPoints.Add(new EventPoint { Position = new FractionalPosition(4), Phrase = "verse" });
        arr.Levels[0].Notes.Add(Note(1, 5));
        var issues = NewService().Validate(arr, Beats());
        Assert.Contains(issues, i => i.Type == ArrangementIssueType.FirstPhraseNotEmpty);
    }

    // ---- the OTHER phrase store ------------------------------------------
    //
    // PHRASES LIVE IN TWO STORES (Core/Utilities/PhraseMarkers): the editor
    // authors EventPoint.Phrase, while the RS-XML and psarc readers fill
    // Arrangement.Phrases + PhraseIterations. A rule keyed on EventPoint alone
    // finds ZERO phrases in every imported chart and reads as "no problems
    // found" — measured over TestData/EOF: 0 markers via EventPoint, 2,596 via
    // PhraseIterations. All three I-series phrase checks were keyed that way.

    /// <summary>An imported arrangement — phrases in the RS store only.</summary>
    private static void ImportPhrases(Arrangement arr, params (int beat, string name)[] phrases)
    {
        foreach (var (beat, name) in phrases)
        {
            var id = arr.Phrases.IndexOf(name);
            if (id < 0)
            {
                arr.Phrases.Add(name);
                id = arr.Phrases.Count - 1;
            }

            arr.PhraseIterations.Add(new PhraseIteration(new FractionalPosition(beat), id));
        }
    }

    [Fact]
    public void Detects_NoEndPhrase_OnAnImportedChart()
    {
        var arr = NewArr();
        ImportPhrases(arr, (0, "COUNT"), (2, "verse"));
        var issues = NewService().Validate(arr, Beats());
        Assert.Contains(issues, i => i.Type == ArrangementIssueType.NoEndPhrase);
    }

    [Fact]
    public void Does_Not_Flag_NoEndPhrase_OnAnImportedChartThatHasOne()
    {
        var arr = NewArr();
        ImportPhrases(arr, (0, "COUNT"), (2, "verse"), (6, "END"));
        var issues = NewService().Validate(arr, Beats());
        Assert.DoesNotContain(issues, i => i.Type == ArrangementIssueType.NoEndPhrase);
    }

    [Fact]
    public void Detects_FirstPhraseNotEmpty_OnAnImportedChart()
    {
        var arr = NewArr();
        ImportPhrases(arr, (0, "COUNT"), (4, "verse"), (6, "END"));
        arr.Levels[0].Notes.Add(Note(1, 5));
        var issues = NewService().Validate(arr, Beats());
        Assert.Contains(issues, i => i.Type == ArrangementIssueType.FirstPhraseNotEmpty);
    }

    /// <summary>
    ///     I27 counts PHRASES, not phrase ITERATIONS. Rocksmith's ceiling — and
    ///     <c>ChartExportValidator</c>'s <c>KeyTooManyPhrases</c>, which is the
    ///     same question asked at the export boundary — is on the distinct phrase
    ///     list. Counting occurrences instead makes every song longer than about
    ///     two minutes fail a limit it is nowhere near, and puts the inspector and
    ///     the export dialog in open disagreement.
    /// </summary>
    [Fact]
    public void Does_Not_Flag_MoreThan100Phrases_ForManyIterationsOfAFewPhrases()
    {
        var arr = NewArr(); // 150 markers, 4 distinct names
        var names = new[] { "verse", "chorus", "bridge", "solo" };
        for (var i = 0; i < 150; i++)
            ImportPhrases(arr, (i, names[i % names.Length]));

        var issues = NewService().Validate(arr, Beats(count: 160));
        Assert.DoesNotContain(issues, i => i.Type == ArrangementIssueType.MoreThan100Phrases);
    }

    [Fact]
    public void Detects_MoreThan100Phrases_WhenTheDistinctPhraseListExceedsTheLimit()
    {
        var arr = NewArr();
        for (var i = 0; i < 101; i++)
            ImportPhrases(arr, (i, $"phrase{i}"));

        var issues = NewService().Validate(arr, Beats(count: 110));
        Assert.Contains(issues, i => i.Type == ArrangementIssueType.MoreThan100Phrases);
    }

    /// <summary>
    ///     #322: the chart keeps the charter's phrase names, and Rocksmith gets a
    ///     legal spelling at export — so the inspector has to SAY which names the
    ///     game will see differently (D10), with the spelling the export writes.
    /// </summary>
    [Fact]
    public void Detects_PhraseNameRewrittenForRocksmith_WithTheExportedSpelling()
    {
        var arr = NewArr();
        ImportPhrases(arr, (0, "COUNT"), (1, "Solo (fast)"), (2, "サビ"), (3, "Riff"), (4, "Riff!"), (6, "END"));

        var issues = NewService().Validate(arr, Beats())
            .Where(i => i.Type == ArrangementIssueType.PhraseNameRewrittenForRocksmith)
            .ToList();

        Assert.Equal(3, issues.Count);
        Assert.Contains(issues, i => i.Message.Contains("\"Solo (fast)\"") && i.Message.Contains("\"Solo fast\""));
        Assert.Contains(issues, i => i.Message.Contains("\"サビ\"") && i.Message.Contains("\"phrase2\""));
        Assert.Contains(issues, i => i.Message.Contains("\"Riff!\"") && i.Message.Contains("\"Riff 2\""));
        Assert.Equal("D10", ArrangementIssueType.PhraseNameRewrittenForRocksmith.GetCode());
    }

    [Fact]
    public void Does_Not_Flag_PhraseNamesRocksmithAlreadyAccepts()
    {
        var arr = NewArr();
        ImportPhrases(arr, (0, "COUNT"), (1, "verse_1"), (2, "chorus #2"), (6, "END"));

        var issues = NewService().Validate(arr, Beats());

        Assert.DoesNotContain(issues, i => i.Type == ArrangementIssueType.PhraseNameRewrittenForRocksmith);
    }

    [Fact]
    public void Detects_NoteInsideNoguitarSection()
    {
        var arr = NewArr();
        arr.EventPoints.Add(new EventPoint { Position = new FractionalPosition(0), Phrase = "noguitar" });
        arr.EventPoints.Add(new EventPoint { Position = new FractionalPosition(4), Phrase = "verse" });
        arr.Levels[0].Notes.Add(Note(2, 5));
        var issues = NewService().Validate(arr, Beats());
        Assert.Contains(issues, i => i.Type == ArrangementIssueType.NoteInsideNoguitarSection);
    }

    [Fact]
    public void Detects_NoteInsideNoguitarSection_WhenOnlyTheSectionSaysNoguitar()
    {
        // The EOF shape: the noguitar lives on the SECTION, the phrase has any name.
        var arr = NewArr();
        arr.EventPoints.Add(new EventPoint { Position = new FractionalPosition(0), Phrase = "p1", Section = "noguitar" });
        arr.EventPoints.Add(new EventPoint { Position = new FractionalPosition(4), Phrase = "p2", Section = "verse" });
        arr.Levels[0].Notes.Add(Note(2, 5));
        arr.Levels[0].Notes.Add(Note(5, 5));

        var issues = NewService().Validate(arr, Beats())
            .Where(i => i.Type == ArrangementIssueType.NoteInsideNoguitarSection)
            .ToList();

        Assert.Equal(1000.0, Assert.Single(issues).TimeMs!.Value, 3);
    }

    [Fact]
    public void Detects_ToneChangeOnNote()
    {
        var arr = NewArr();
        arr.ToneChanges.Add(new ToneChange { Position = new FractionalPosition(2), ToneName = "lead" });
        arr.Levels[0].Notes.Add(Note(2, 5));
        var issues = NewService().Validate(arr, Beats());
        Assert.Contains(issues, i => i.Type == ArrangementIssueType.ToneChangeOnNote);
    }

    [Fact]
    public void Detects_NaturalHarmonicWithBend_AndMissingBendValue()
    {
        var arr = NewArr();
        var n = Note(1, 5);
        n.Harmonic = HarmonicType.Normal;
        n.BendValues.Add(new BendValue(new FractionalPosition(1), 0.0));
        arr.Levels[0].Notes.Add(n);
        var issues = NewService().Validate(arr, Beats());
        Assert.Contains(issues, i => i.Type == ArrangementIssueType.NaturalHarmonicWithBend);
        Assert.Contains(issues, i => i.Type == ArrangementIssueType.MissingBendValue);
    }

    [Fact]
    public void Detects_OverlappingBendValues()
    {
        var arr = NewArr();
        var n = Note(1, 5, sustainBeats: 2);
        n.BendValues.Add(new BendValue(new FractionalPosition(1), 1.0));
        n.BendValues.Add(new BendValue(new FractionalPosition(1), 1.5));
        arr.Levels[0].Notes.Add(n);
        var issues = NewService().Validate(arr, Beats());
        Assert.Contains(issues, i => i.Type == ArrangementIssueType.OverlappingBendValues);
    }

    [Fact]
    public void IssueOrdering_PutsGeneralIssuesFirst()
    {
        var arr = NewArr();
        // Will produce NoEndPhrase (general) and HopoIntoSameNote (timed)
        arr.EventPoints.Add(new EventPoint { Position = new FractionalPosition(0), Phrase = "intro" });
        arr.EventPoints.Add(new EventPoint { Position = new FractionalPosition(4), Phrase = "verse" });
        arr.Levels[0].Notes.Add(Note(1, 5));
        var hopo = Note(2, 5);
        hopo.Hopo = HopoType.HammerOn;
        arr.Levels[0].Notes.Add(hopo);

        var issues = NewService().Validate(arr, Beats());
        Assert.True(issues.Count >= 2);
        // First items should be general (no time)
        Assert.Null(issues[0].TimeMs);
    }

    [Fact]
    public void Service_DoesNotLogOnSuccessfulValidation()
    {
        var logger = new FakeAppLogger();
        var svc = new ArrangementValidationService(logger);
        svc.Validate(NewArr(), Beats());
        Assert.Equal(0, logger.TimedOperationsStarted);
        Assert.Equal(0, logger.TimedOperationsCompleted);
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public void Issue_CodeAndSeverity_PopulatedFromType()
    {
        var arr = NewArr();
        arr.Levels[0].Notes.Add(Note(1, 30));
        var issue = NewService().Validate(arr, Beats())
            .First(i => i.Type == ArrangementIssueType.FretNumberMoreThan24);
        Assert.Equal("I38", issue.Code);
        Assert.True(issue.HasTime);
    }

    // ----------------------------------------------------------- new checks

    [Fact]
    public void Detects_LinkNextSlideMismatch()
    {
        var arr = NewArr();
        var n1 = Note(1, 5, sustainBeats: 1);
        n1.LinkNext = true;
        n1.SlideTo = 7;
        var n2 = Note(2, 8); // slide says 7, next is 8 → slide mismatch
        arr.Levels[0].Notes.Add(n1);
        arr.Levels[0].Notes.Add(n2);
        var issues = NewService().Validate(arr, Beats());
        Assert.Contains(issues, i => i.Type == ArrangementIssueType.LinkNextSlideMismatch);
        Assert.DoesNotContain(issues, i => i.Type == ArrangementIssueType.LinkNextFretMismatch);
    }

    [Fact]
    public void Detects_LinkNextFretMismatch_WhenNoSlide()
    {
        var arr = NewArr();
        var n1 = Note(1, 5, sustainBeats: 1);
        n1.LinkNext = true;
        var n2 = Note(2, 7);
        arr.Levels[0].Notes.Add(n1);
        arr.Levels[0].Notes.Add(n2);
        var issues = NewService().Validate(arr, Beats());
        Assert.Contains(issues, i => i.Type == ArrangementIssueType.LinkNextFretMismatch);
        Assert.DoesNotContain(issues, i => i.Type == ArrangementIssueType.LinkNextSlideMismatch);
    }

    /// <summary>
    ///     Landing a Link-Next sound on a chord is LEGAL to author. RS cannot
    ///     spell the landing as a &lt;chord&gt; (a chord is a strum; a shape you
    ///     slid into was never re-picked), so the EXPORT splits it into loose
    ///     notes exactly as EOF does (EOF_SRC/rs.c:2010) — see
    ///     LinkLandingBreakup. Reporting it in the inspector was noise: 27
    ///     findings on one real chart for something already handled.
    /// </summary>
    [Fact]
    public void ANoteLinkedIntoAChord_IsNotAnIssue()
    {
        var arr = NewArr();
        var n = Note(1, 5, sustainBeats: 1);
        n.LinkNext = true;
        arr.Levels[0].Notes.Add(n);
        var chord = new Chord { Position = new FractionalPosition(2) };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 0, Fret = 5 });
        arr.Levels[0].Chords.Add(chord);
        var issues = NewService().Validate(arr, Beats());
        Assert.DoesNotContain(issues, i => i.Type == ArrangementIssueType.NoteLinkedToChord);
        Assert.DoesNotContain(issues, i => i.Type == ArrangementIssueType.LinkNextMissingTargetNote);
    }

    /// <summary>The landing chord's note on that string IS the link target, so
    /// a fret mismatch against it is still a real finding.</summary>
    [Fact]
    public void ALinkIntoAChord_StillChecksTheLandingFret()
    {
        var arr = NewArr();
        var n = Note(1, 5, sustainBeats: 1);
        n.LinkNext = true;
        arr.Levels[0].Notes.Add(n);
        var chord = new Chord { Position = new FractionalPosition(2) };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 0, Fret = 9 });
        arr.Levels[0].Chords.Add(chord);
        var issues = NewService().Validate(arr, Beats());
        Assert.Contains(issues, i => i.Type == ArrangementIssueType.LinkNextFretMismatch);
    }

    /// <summary>
    ///     Link-Next checks must walk CHORD NOTES, not just single notes:
    ///     Rocksmith2014.NET runs the identical chain check over every chord
    ///     note carrying linkNext (InstrumentalChecker.checkChords), and a
    ///     linked CHORD is how the tab importers and the paste path spell a
    ///     strum ringing into the next one. Landing on another chord is legal
    ///     (the export splits it, see LinkLandingBreakup) — but the chain
    ///     itself still has to hold.
    /// </summary>
    [Fact]
    public void AChordSlidingIntoAnotherChord_IsNotAnIssue()
    {
        var arr = NewArr();
        var first = new Chord { Position = new FractionalPosition(1) };
        first.ChordNotes.Add(new ChordNote
        {
            StringIndex = 0, Fret = 5, Sustain = new FractionalPosition(1), LinkNext = true, SlideTo = 7
        });
        var second = new Chord { Position = new FractionalPosition(2) };
        second.ChordNotes.Add(new ChordNote { StringIndex = 0, Fret = 7 });
        arr.Levels[0].Chords.Add(first);
        arr.Levels[0].Chords.Add(second);

        var issues = NewService().Validate(arr, Beats());

        Assert.DoesNotContain(issues, i => i.Type == ArrangementIssueType.NoteLinkedToChord);
        Assert.DoesNotContain(issues, i => i.Type == ArrangementIssueType.LinkNextSlideMismatch);
    }

    [Fact]
    public void AChordSlidingIntoAChordOnTheWrongFret_IsStillFlagged()
    {
        var arr = NewArr();
        var first = new Chord { Position = new FractionalPosition(1) };
        first.ChordNotes.Add(new ChordNote
        {
            StringIndex = 0, Fret = 5, Sustain = new FractionalPosition(1), LinkNext = true, SlideTo = 7
        });
        var second = new Chord { Position = new FractionalPosition(2) };
        second.ChordNotes.Add(new ChordNote { StringIndex = 0, Fret = 9 });
        arr.Levels[0].Chords.Add(first);
        arr.Levels[0].Chords.Add(second);

        var issues = NewService().Validate(arr, Beats());

        var issue = Assert.Single(issues, i => i.Type == ArrangementIssueType.LinkNextSlideMismatch);
        Assert.Same(first, issue.Chord);
    }

    [Fact]
    public void Detects_ChordNoteLinkNextWithNoTargetNote()
    {
        var arr = NewArr();
        var chord = new Chord { Position = new FractionalPosition(1) };
        chord.ChordNotes.Add(new ChordNote
        {
            StringIndex = 0, Fret = 5, Sustain = new FractionalPosition(1), LinkNext = true
        });
        arr.Levels[0].Chords.Add(chord);

        var issues = NewService().Validate(arr, Beats());

        Assert.Contains(issues, i => i.Type == ArrangementIssueType.LinkNextMissingTargetNote);
    }

    [Fact]
    public void AChordNoteLinkingCleanlyIntoANote_IsNotFlagged()
    {
        var arr = NewArr();
        var chord = new Chord { Position = new FractionalPosition(1) };
        chord.ChordNotes.Add(new ChordNote
        {
            StringIndex = 0, Fret = 5, Sustain = new FractionalPosition(1), LinkNext = true
        });
        arr.Levels[0].Chords.Add(chord);
        arr.Levels[0].Notes.Add(Note(2, 5));

        var issues = NewService().Validate(arr, Beats());

        Assert.DoesNotContain(issues, i =>
            i.Type is ArrangementIssueType.NoteLinkedToChord
                or ArrangementIssueType.LinkNextMissingTargetNote
                or ArrangementIssueType.IncorrectLinkNext
                or ArrangementIssueType.LinkNextFretMismatch);
    }

    [Fact]
    public void Detects_PhraseChangeOnLinkNextNote()
    {
        var arr = NewArr();
        var n = Note(1, 5, sustainBeats: 2);
        n.LinkNext = true;
        var n2 = Note(3, 5);
        arr.Levels[0].Notes.Add(n);
        arr.Levels[0].Notes.Add(n2);
        // Phrase change at beat 2 — within the sustain of n.
        arr.EventPoints.Add(new EventPoint { Position = new FractionalPosition(2), Phrase = "verse" });
        var issues = NewService().Validate(arr, Beats());
        Assert.Contains(issues, i => i.Type == ArrangementIssueType.PhraseChangeOnLinkNextNote);
    }

    [Fact]
    public void DoesNotDetect_PhraseChange_OnLinkNextNote_WhenMoverPhrase()
    {
        var arr = NewArr();
        var n = Note(1, 5, sustainBeats: 2);
        n.LinkNext = true;
        arr.Levels[0].Notes.Add(n);
        arr.Levels[0].Notes.Add(Note(3, 5));
        arr.EventPoints.Add(new EventPoint { Position = new FractionalPosition(2), Phrase = "mover1" });
        var issues = NewService().Validate(arr, Beats());
        Assert.DoesNotContain(issues, i => i.Type == ArrangementIssueType.PhraseChangeOnLinkNextNote);
    }

    [Fact]
    public void Detects_PositionShiftIntoPullOff()
    {
        var arr = NewArr();
        arr.Levels[0].Anchors.Add(new FretAnchor(new FractionalPosition(1), 5));
        arr.Levels[0].Anchors.Add(new FretAnchor(new FractionalPosition(2), 9));
        var pull = Note(2, 9);
        pull.Hopo = HopoType.PullOff;
        arr.Levels[0].Notes.Add(Note(1, 7));
        arr.Levels[0].Notes.Add(pull);
        var issues = NewService().Validate(arr, Beats());
        Assert.Contains(issues, i => i.Type == ArrangementIssueType.PositionShiftIntoPullOff);
    }

    [Fact]
    public void Detects_AnchorInsideHandShape_AtPhraseBoundary()
    {
        var arr = NewArr();
        var lvl = arr.Levels[0];
        lvl.HandShapes.Add(new HandShape
        {
            Position = new FractionalPosition(1),
            EndPosition = new FractionalPosition(3),
            TemplateId = 0
        });
        lvl.Anchors.Add(new FretAnchor(new FractionalPosition(2), 5));
        arr.EventPoints.Add(new EventPoint { Position = new FractionalPosition(0), Phrase = "intro" });
        arr.EventPoints.Add(new EventPoint { Position = new FractionalPosition(2), Phrase = "verse" });
        var issues = NewService().Validate(arr, Beats());
        Assert.Contains(issues, i => i.Type == ArrangementIssueType.AnchorInsideHandShapeAtPhraseBoundary);
    }

    [Fact]
    public void Detects_AnchorCloseToUnpitchedSlide()
    {
        var arr = NewArr();
        var lvl = arr.Levels[0];
        var slide = Note(1, 5, sustainBeats: 1);
        slide.UnpitchedSlide = true;
        slide.SlideTo = 9;
        lvl.Notes.Add(slide);
        // Slide ends at beat 2 (=1000ms). Anchor placed exactly there -> within 4 ms.
        lvl.Anchors.Add(new FretAnchor(new FractionalPosition(2), 9));
        var issues = NewService().Validate(arr, Beats());
        Assert.Contains(issues, i => i.Type == ArrangementIssueType.AnchorCloseToUnpitchedSlide);
    }

    [Fact]
    public void Detects_BarreOverOpenStrings()
    {
        var arr = NewArr();
        var t = new ChordTemplate
        {
            Id = 0,
            Name = "Barre",
            Frets = new Dictionary<int, int> { { 0, 5 }, { 1, 0 }, { 2, 5 } },
            Fingers = new Dictionary<int, int> { { 0, 1 }, { 1, -1 }, { 2, 1 } }
        };
        arr.ChordTemplates.Add(t);
        var chord = new Chord { Position = new FractionalPosition(1), TemplateId = 0 };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 0, Fret = 5 });
        chord.ChordNotes.Add(new ChordNote { StringIndex = 2, Fret = 5 });
        arr.Levels[0].Chords.Add(chord);
        var issues = NewService().Validate(arr, Beats());
        Assert.Contains(issues, i => i.Type == ArrangementIssueType.BarreOverOpenStrings);
    }

    [Fact]
    public void Detects_PossiblyWrongChordFingering()
    {
        var arr = NewArr();
        // String 0 fret 3 with finger 2 (lowest non-thumb finger).
        // String 1 fret 1 with finger 3 — fret 1 < lowestFingerFret (3) → suspect.
        var t = new ChordTemplate
        {
            Id = 0,
            Name = "Strange",
            Frets = new Dictionary<int, int> { { 0, 3 }, { 1, 1 } },
            Fingers = new Dictionary<int, int> { { 0, 2 }, { 1, 3 } }
        };
        arr.ChordTemplates.Add(t);
        var chord = new Chord { Position = new FractionalPosition(1), TemplateId = 0 };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 0, Fret = 3 });
        chord.ChordNotes.Add(new ChordNote { StringIndex = 1, Fret = 1 });
        arr.Levels[0].Chords.Add(chord);
        var issues = NewService().Validate(arr, Beats());
        Assert.Contains(issues, i => i.Type == ArrangementIssueType.PossiblyWrongChordFingering);
    }

    [Fact]
    public void Detects_FingeringAnchorMismatch()
    {
        var arr = NewArr();
        var lvl = arr.Levels[0];
        // Hand shape using only fingers 2/3/4 (no index/thumb), with a fret on the anchor fret.
        var t = new ChordTemplate
        {
            Id = 0,
            Name = "NoIndex",
            Frets = new Dictionary<int, int> { { 0, 5 }, { 1, 7 } },
            Fingers = new Dictionary<int, int> { { 0, 2 }, { 1, 4 } }
        };
        arr.ChordTemplates.Add(t);
        lvl.HandShapes.Add(new HandShape
        {
            Position = new FractionalPosition(1),
            EndPosition = new FractionalPosition(3),
            TemplateId = 0
        });
        lvl.Anchors.Add(new FretAnchor(new FractionalPosition(1), 5));
        var issues = NewService().Validate(arr, Beats());
        Assert.Contains(issues, i => i.Type == ArrangementIssueType.FingeringAnchorMismatch);
    }
}
