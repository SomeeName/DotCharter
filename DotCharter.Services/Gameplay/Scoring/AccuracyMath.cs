namespace DotCharter.Services.Gameplay.Scoring;

/// <summary>
///     The ONE definition of accuracy from a hit/miss tally — and, the part that
///     actually matters, the ONE decision about what it means when NOTHING has
///     been judged yet.
///     <para>
///         <b><c>0/0</c> is 0, never 1.</b> A per-type
///         <c>judged == 0 ? 1.0 : hits / judged</c> would choose 1.0. That reads as <i>100%</i> on screen, so a player who has
///         done nothing is shown as perfect — and a run that judges nothing
///         (ended from the pause menu during the intro) is not a corner case. Accuracy is
///         UNDEFINED at zero attempts, and of the two things a display can do
///         with that, "perfect" is the one that is a lie.
///     </para>
///     <para>
///         Changing the convention is safe because the 1.0 was never load
///         bearing: every consumer that could act on it already refuses to look
///         until something has been judged —
///         <c>SpeedLadder.CompletePass</c> (<c>judgedSounds &lt;= 0</c>),
///         <c>DynamicDifficultyEngine.CompletePhrase</c> (<c>judged &lt;= 0</c>),
///         <c>PracticeProfileService.Record</c> (<c>judged &lt;= 0</c>) and its
///         <c>FoldPhraseMastery</c> (<c>outcome.Judged &lt;= 0</c>),
///         <c>LivePlaySessionReport.WorstSection</c> (<c>!s.WasPlayed</c>) and
///         <c>LivePlaySectionGraph.HeatBrush</c> (<c>Hits + Misses == 0</c>).
///         The 1.0 only ever reached a READOUT. Keep it that way: a new consumer
///         that cannot cope with 0 must gate on <see cref="IsMeasured" />, not
///         re-introduce a private zero convention.
///     </para>
/// </summary>
public static class AccuracyMath
{
    /// <summary>
    ///     Whether an accuracy figure means anything yet — i.e. whether at least
    ///     one sound has been judged. A readout gates its PLACEHOLDER on this
    ///     ("—" rather than a number), and any consumer that would act on the
    ///     figure gates on it too.
    /// </summary>
    public static bool IsMeasured(int hits, int misses) => hits + misses > 0;

    /// <summary>
    ///     Whether a STORED best accuracy means anything yet.
    ///     <para>
    ///         A separate method from <see cref="IsMeasured(int,int)" /> because
    ///         the input is different: the persisted
    ///         <c>PracticeArrangementProfile.BestAccuracy</c> keeps no tally, and
    ///         <c>PracticeProfileService.Record</c> deliberately declines to raise
    ///         it for a run below
    ///         <c>PracticeProfileService.MinCoverageForBestAccuracy</c> — so a
    ///         player whose runs are all section drills or mid-song quits has
    ///         sessions recorded and a best accuracy still sitting at its 0
    ///         default. <b>0 there means UNMEASURED, not "played terribly"</b>,
    ///         and a readout that renders it as a confident <c>F</c> / <c>0.0%</c>
    ///         is the same lie the <c>judged == 0 ? 1.0</c> convention told with
    ///         the sign flipped.
    ///     </para>
    ///     <para>
    ///         It is the predicate the Get Ready screen already used
    ///         (<c>stats is { BestAccuracy: &gt; 0 }</c>) while the song row, the
    ///         local leaderboard and the session history did not — three readouts
    ///         disagreeing with a fourth about one store. The one representable
    ///         ambiguity is deliberate: a qualifying run that really scored 0%
    ///         stores 0 and reads as unmeasured. Telling those apart needs a
    ///         schema change, and "no grade yet" is the safe side of it.
    ///     </para>
    /// </summary>
    public static bool IsMeasuredBest(double bestAccuracy) => bestAccuracy > 0;

    /// <summary>
    ///     What a readout prints instead of a number when nothing has been
    ///     measured — the same em dash the results screen, and the take history
    ///     already use. Not localized, for the reason those are
    ///     not: it is a typographic placeholder, not a sentence.
    /// </summary>
    public const string UnmeasuredLabel = "—";

    /// <summary>Hits over judged sounds, in 0..1. Zero judged is 0.</summary>
    public static double FromCounts(int hits, int misses)
    {
        var judged = hits + misses;
        return judged <= 0 ? 0.0 : hits / (double)judged;
    }
}
