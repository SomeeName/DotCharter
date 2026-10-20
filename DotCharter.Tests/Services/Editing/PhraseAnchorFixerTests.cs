using DotCharter.Services.Editing.Collisions;
using DotCharter.Services.Editing.Fixing;

namespace DotCharter.Tests.Services.Editing;

/// <summary>
///     The two tools that MINT an FHP anchor at a phrase start, held to the two
///     rules a fixer keeps hitting.
///     <para>
///         <b>(1) PHRASES LIVE IN TWO STORES.</b> The editors author
///         <c>EventPoint.Phrase</c>; the RS-XML and psarc readers fill
///         <c>Arrangement.Phrases</c> + <c>PhraseIterations</c>. Measured over the
///         89 hand-finished RS2 exports in <c>TestData/EOF</c>: <b>0</b> markers
///         via <c>EventPoint</c>, <b>2,596</b> via <c>PhraseIterations</c> — so a
///         tool keyed on one store is silently dead on every imported chart, which
///         is the only kind that has thousands of phrases to fix.
///     </para>
///     <para>
///         <b>(2) A tool that DERIVES a fret must refuse one below the capo.</b>
///         The lowest legal anchor is <c>capo + 1</c> (inspector rule L02,
///         <c>CapoAnchorRangeRule</c>); a fixer that writes fret 1 under a capo
///         mints the exact defect the inspector then reports. Capo 0 must stay a
///         strict no-op, which is nearly every chart.
///     </para>
/// </summary>
public sealed class PhraseAnchorFixerTests
{
    private static List<BeatMarker> Beats(int count = 16, double bpm = 120)
    {
        var ms = 60000.0 / bpm;
        return
        [
            .. Enumerable.Range(0, count)
                .Select(i => new BeatMarker { Time = i * ms, Bpm = bpm, IsMeasure = i % 4 == 0 })
        ];
    }

    private static AppConfig Config() => new()
    {
        MinNoteDistanceMode = MinNoteDistanceMode.BeatFraction,
        MinNoteDistanceDivisor = 64
    };

    /// <summary>An arrangement the way the RS-XML / psarc readers leave it:
    /// phrases in `Phrases` + `PhraseIterations`, and NOT one `EventPoint`.</summary>
    private static Arrangement ImportedArrangement(int capo = 0)
    {
        var arrangement = new Arrangement { Name = "Lead", Capo = capo };
        arrangement.Phrases.Add("intro");
        arrangement.Phrases.Add("verse");
        arrangement.PhraseIterations.Add(new PhraseIteration
        {
            Position = new FractionalPosition(0), PhraseId = 0
        });
        arrangement.PhraseIterations.Add(new PhraseIteration
        {
            Position = new FractionalPosition(8), PhraseId = 1
        });

        // Sounds sit ON the phrase starts, the way a real chart does — the
        // spacing enforcer walks anchors toward their sounds, so an anchor
        // parked in an empty bar does not stay where it was minted.
        var level = new ChartLevel();
        level.Notes.Add(new GuitarNote { Position = new FractionalPosition(0), Fret = 7, StringIndex = 0 });
        level.Notes.Add(new GuitarNote { Position = new FractionalPosition(8), Fret = 7, StringIndex = 1 });
        level.Anchors.Add(new FretAnchor(new FractionalPosition(0), 7, 4));
        arrangement.Levels.Add(level);
        return arrangement;
    }

    // ── ChartImprovementService.EnsurePhraseStartAnchors ─────────────────────

    [Fact]
    public void Improve_AddsPhraseStartAnchors_OnAnIMPORTEDChart()
    {
        var project = new ChartProject();
        foreach (var beat in Beats()) project.Beats.Add(beat);
        var arrangement = ImportedArrangement();
        project.Arrangements.Add(arrangement);
        // Keyed on EventPoint.Phrase alone this did nothing at all here — the
        // arrangement carries not one EventPoint. (Improve itself may ADD one: the
        // END phrase an imported chart lacks, #284.)
        Assert.DoesNotContain(arrangement.EventPoints, e => !string.IsNullOrWhiteSpace(e.Phrase));

        new ChartImprovementService().ImproveProject(project, Config(), 0);

        Assert.Contains(arrangement.Levels[0].Anchors, a => a.Position == new FractionalPosition(8));
    }

    [Fact]
    public void Improve_StillAddsThemForEditorAuthoredPhrasesAndSections()
    {
        // Vacuity keeper for the store change: the EventPoint half must not
        // have been traded away for the PhraseIteration half.
        var project = new ChartProject();
        foreach (var beat in Beats()) project.Beats.Add(beat);

        var arrangement = new Arrangement { Name = "Lead" };
        arrangement.EventPoints.Add(new EventPoint { Position = new FractionalPosition(4), Phrase = "verse" });
        arrangement.EventPoints.Add(new EventPoint { Position = new FractionalPosition(8), Section = "chorus" });
        var level = new ChartLevel();
        level.Notes.Add(new GuitarNote { Position = new FractionalPosition(1), Fret = 7, StringIndex = 0 });
        level.Anchors.Add(new FretAnchor(new FractionalPosition(1), 7, 4));
        arrangement.Levels.Add(level);
        project.Arrangements.Add(arrangement);

        new ChartImprovementService().ImproveProject(project, Config(), 0);

        Assert.Contains(level.Anchors, a => a.Position == new FractionalPosition(4));
        Assert.Contains(level.Anchors, a => a.Position == new FractionalPosition(8));
    }

    [Fact]
    public void Improve_NeverMintsAnAnchorOnOrBelowTheCapo()
    {
        var project = new ChartProject();
        foreach (var beat in Beats()) project.Beats.Add(beat);

        var arrangement = ImportedArrangement(capo: 4);
        // An imported chart can legitimately carry a sub-capo anchor; copying
        // it forward would propagate the L02 defect to every phrase start.
        arrangement.Levels[0].Anchors[0] = new FretAnchor(new FractionalPosition(0), 1, 4);
        project.Arrangements.Add(arrangement);

        new ChartImprovementService().ImproveProject(project, Config(), 0);

        // Not vacuous: something MUST have been minted, or the loop below is
        // satisfied by an empty collection.
        var minted = arrangement.Levels[0].Anchors
            .Where(a => a.Position != new FractionalPosition(0))
            .ToList();
        Assert.NotEmpty(minted);

        // The pre-existing one is the inspector's problem, not this tool's.
        foreach (var anchor in minted)
        {
            Assert.True(anchor.Fret > arrangement.Capo,
                $"minted an anchor at fret {anchor.Fret} under a capo at fret {arrangement.Capo} (rule L02)");
        }
    }

    // ── ChartSpacingEnforcer.RemoveRedundantAnchors ─────────────────────────

    /// <summary>
    ///     The undoer. A phrase-start anchor restates the position in force by
    ///     construction, so it is always "identical" to the previous one — and
    ///     with the protection keyed on <c>EventPoint</c> alone, the enforcer
    ///     deleted on the next save exactly what the two fixers had added.
    /// </summary>
    [Fact]
    public void TheEnforcer_KeepsARestatingAnchorOnAnIMPORTEDPhraseBoundary()
    {
        var arrangement = ImportedArrangement();
        var level = arrangement.Levels[0];
        level.Anchors.Add(new FretAnchor(new FractionalPosition(8), 7, 4)); // identical to the one at 0

        ChartSpacingEnforcer.EnforceArrangement(arrangement, Beats(), Config());

        Assert.Contains(level.Anchors, a => a.Position == new FractionalPosition(8));
    }

    [Fact]
    public void TheEnforcer_StillDropsARestatingAnchorAwayFromAnyBoundary()
    {
        // Vacuity keeper: the protection must not have swallowed the pass.
        var arrangement = ImportedArrangement();
        var level = arrangement.Levels[0];
        level.Notes.Add(new GuitarNote { Position = new FractionalPosition(4), Fret = 7, StringIndex = 2 });
        level.Anchors.Add(new FretAnchor(new FractionalPosition(4), 7, 4)); // no phrase at beat 4

        ChartSpacingEnforcer.EnforceArrangement(arrangement, Beats(), Config());

        Assert.DoesNotContain(level.Anchors, a => a.Position == new FractionalPosition(4));
    }

    // ── ArrangementAutoFixService.AddMissingAnchorsAtPhraseStarts ────────────

    [Fact]
    public void AutoFix_NeverMintsAnAnchorOnOrBelowTheCapo()
    {
        var arrangement = ImportedArrangement(capo: 4);
        // No anchor at all before the first phrase: the fixer falls back to its
        // own default, which used to be a hard-coded fret 1.
        arrangement.Levels[0].Anchors.Clear();

        new ArrangementAutoFixService().Fix(arrangement, new ArrangementFixResult());

        var minted = arrangement.Levels[0].Anchors.Single(a => a.Position == new FractionalPosition(0));
        Assert.True(minted.Fret > arrangement.Capo,
            $"minted an anchor at fret {minted.Fret} under a capo at fret {arrangement.Capo} (rule L02)");
    }

    [Fact]
    public void AutoFix_AtCapoZero_IsAStrictNoOp_AndStillMintsFret1()
    {
        var arrangement = ImportedArrangement();
        arrangement.Levels[0].Anchors.Clear();

        new ArrangementAutoFixService().Fix(arrangement, new ArrangementFixResult());

        var minted = arrangement.Levels[0].Anchors.Single(a => a.Position == new FractionalPosition(0));
        Assert.Equal(1, minted.Fret);
    }
}
