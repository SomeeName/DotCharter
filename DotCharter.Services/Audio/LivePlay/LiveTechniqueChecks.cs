namespace DotCharter.Services.Audio.LivePlay;

/// <summary>
///     The hits <see cref="LiveNoteMatcher" /> has credited and whose bend, slide or vibrato the
/// contour judge still has to rule on.
///     <para>
///         <b>The credit never waits for the ruling.</b> A hit is shown the moment the CQT confirms
///         it (owner rule: judging never makes hit feedback later), but a bend's evidence is its
///         PEAK and a slide's is its arrival — up to a second and a half later. So the hit is
///         credited at once, paid as a technique mismatch (<see cref="ExpectedSound.PaidVerdict" />),
///         and ruled on here once the playhead passes its evidence:
///         <see cref="TechniqueContourVerdict.NotMet" /> leaves it a
///         <see cref="LivePlayVerdict.HitWrongTechnique" />; Met or Unknown pays the rest
///         (<see cref="LivePlayStats.UpgradeTechnique" />). The score never moves down and a ruling
///         never turns a hit into a miss.
///     </para>
/// </summary>
internal sealed class LiveTechniqueChecks
{
    /// <summary>How long past its evidence a check waits for the audio to reach the CQT before
    /// it gives up as <see cref="TechniqueContourVerdict.Unknown" />.</summary>
    public const double AudioWaitMs = 1500;

    private readonly List<ExpectedSound> _pending = [];

    /// <summary>The rulings of the last <see cref="ResolveDue" /> or <see cref="ResolveAll" />.
    /// Reused: consume before the next call.</summary>
    public List<LiveTechniqueJudgment> Resolved { get; } = [];

    /// <summary>Whether a hit just credited has a technique to wait for: one the chart asks for
    /// AND the player switched on (<paramref name="judged" />).</summary>
    public static bool Wants(ExpectedSound sound, LivePlayVerdict verdict, bool judging, ContourTechniques judged) =>
        judging && verdict == LivePlayVerdict.Hit && (sound.TechniqueContour.Techniques & judged) != 0;

    /// <summary>Starts waiting on a hit just credited as <see cref="LivePlayVerdict.Hit" />.
    /// The sound's expectation is narrowed to the <paramref name="judged" /> techniques first,
    /// so a switched-off technique is never judged and never lengthens the wait: a
    /// bend with vibrato, vibrato off, is ruled on its bend alone. Idempotent — a sound credited
    /// again after a seek narrows to the same set.</summary>
    public void Add(ExpectedSound sound, ContourTechniques judged)
    {
        sound.TechniqueContour = sound.TechniqueContour with
        {
            Techniques = sound.TechniqueContour.Techniques & judged
        };
        sound.AwaitingTechnique = true;
        _pending.Add(sound);
    }

    /// <summary>Rules on every check whose evidence the playhead has passed. A judge that has
    /// not heard the audio yet (null) is asked again on later ticks, until
    /// <see cref="AudioWaitMs" /> runs out.</summary>
    public void ResolveDue(double playheadMs, Func<ExpectedSound, TechniqueContourVerdict?> judge, LivePlayStats stats)
    {
        Resolved.Clear();
        var kept = 0;
        for (var i = 0; i < _pending.Count; i++)
        {
            var sound = _pending[i];
            if (!sound.AwaitingTechnique) continue;
            var due = sound.TimeMs + sound.TechniqueContour.EvidenceAfterMs;
            var verdict = playheadMs >= due ? judge(sound) : null;
            if (verdict == null && playheadMs < due + AudioWaitMs)
            {
                _pending[kept++] = sound;
                continue;
            }

            Resolve(sound, verdict ?? TechniqueContourVerdict.Unknown, stats);
        }

        _pending.RemoveRange(kept, _pending.Count - kept);
    }

    /// <summary>The run is ending: every check is ruled on with what the judge can say now;
    /// one it cannot is Unknown — a full hit.</summary>
    public void ResolveAll(Func<ExpectedSound, TechniqueContourVerdict?>? judge, LivePlayStats stats)
    {
        Resolved.Clear();
        foreach (var sound in _pending)
        {
            if (sound.AwaitingTechnique)
                Resolve(sound, judge?.Invoke(sound) ?? TechniqueContourVerdict.Unknown, stats);
        }

        _pending.Clear();
    }

    /// <summary>
    ///     A seek, a loop pass or a reload: a kept hit's audio does not line up with its chart
    ///     time, so its check ends Unknown — a full hit, which the stats replay that follows
    ///     pays. A sound whose verdict the seek CLEARED forgets its ruling too.
    /// </summary>
    public void Forget(ExpectedSound sound, bool verdictCleared)
    {
        if (verdictCleared) sound.ContourVerdict = null;
        else if (sound.AwaitingTechnique) sound.ContourVerdict = TechniqueContourVerdict.Unknown;
        sound.AwaitingTechnique = false;
    }

    /// <summary>Drops every check; the sounds' own flags are the caller's (see <see cref="Forget" />).</summary>
    public void Clear()
    {
        _pending.Clear();
        Resolved.Clear();
    }

    private void Resolve(ExpectedSound sound, TechniqueContourVerdict verdict, LivePlayStats stats)
    {
        sound.AwaitingTechnique = false;
        sound.ContourVerdict = verdict;
        if (verdict == TechniqueContourVerdict.NotMet)
            sound.Verdict = LivePlayVerdict.HitWrongTechnique; // already paid as one
        else
            stats.UpgradeTechnique(sound.AwaitingGrade, sound.TimingTier, sound.CreditMultiplier);
        Resolved.Add(new LiveTechniqueJudgment(sound, verdict, sound.CreditStreak));
    }
}
