using DotCharter.Tone.Ai;
using DotCharter.Services.Ai.Match;
using DotCharter.Services.Ai.Match.Similarity;
using DotCharter.Tone.Audio.Dsp.Presets;

namespace DotCharter.UI.ViewModels.PlayMode;

/// <summary>
///     One candidate row. Everything on it is derived ONCE — a template that
///     recomputed a chain summary or a percentage per binding pass would do it on
///     the UI thread, once per row, on every pass.
///     <para>
///         <b>No headline number without the breakdown behind it (W5b).</b>
///         <see cref="MatchPercent" /> is nullable and <see cref="IsRated" /> is the
///         state a template switches on: a percentage, or UNRATED with a reason.
///         Zero is a measurement ("no better than a plain amp and cab"); unrated is
///         not a number at all, and the two must not render the same.
///         <see cref="Breakdown" /> carries the per-feature-block rows the panel
///         binds under the pill, and <see cref="ScaleText" /> says what the two ends
///         of the scale mean.
///     </para>
/// </summary>
public sealed class ToneMatchCandidateViewModel
{
    public ToneMatchCandidateViewModel(
        ToneMatchCandidate candidate,
        int rank,
        string chainSummary,
        DspToneCategory category,
        IReadOnlyList<string> notes,
        ILocalizationService loc,
        Func<bool>? isCurrent = null)
    {
        ValidityCheck = isCurrent ?? (static () => true);
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(loc);

        Rank = rank;
        Distance = candidate.Distance;
        Score = candidate.Score;
        MatchPercent = candidate.Score?.Percent;
        IsRated = MatchPercent is not null;
        UnratedReason = candidate.Score?.UnratedReason;
        UnratedText = IsRated
            ? string.Empty
            : ToneDesignViewModel.UnratedReasonText(UnratedReason, loc);
        ChainSummary = chainSummary;
        Origin = candidate.Origin;
        Title = loc.Format("ToneMatch_CandidateTitle", rank + 1);
        MatchText = ToneDesignViewModel.MatchText(candidate.Score, loc);
        ScaleText = loc["ToneMatch_ScaleExplains"];

        // The caveat, not the score: a candidate at or inside the passage's own
        // variation is as close as this metric can SAY, and the remaining percent is
        // not reachable by searching harder.
        SelfDistancePercent = candidate.Score?.SelfDistancePercent;
        WithinTargetNoise = candidate.Score?.WithinTargetNoise ?? false;
        NoiseCaveatText = WithinTargetNoise && SelfDistancePercent is { } floor
            ? loc.Format("ToneMatch_WithinNoise", floor)
            : string.Empty;

        Breakdown = candidate.Score is null
            ? []
            : candidate.Score.WorstBlocks(4)
                .Select(b => new ToneMatchBlockViewModel(
                    b.Name,
                    (int)Math.Round(Math.Clamp(b.Share, 0, 1) * 100),
                    !double.IsNaN(b.ReferenceContribution) && b.Contribution > b.ReferenceContribution,
                    loc))
                .ToList();

        BreakdownText = Breakdown.Count == 0
            ? string.Empty
            : string.Join(", ", Breakdown.Select(b => b.Text));

        // The tone a matched candidate becomes if the player keeps it. USER
        // origin, like every other tone this panel produces: the factory apply
        // path loudness-normalizes, and a matched tone re-levelled on its way to
        // the chain is not the tone that was matched.
        Tone = new DspTonePreset(
            $"match-{rank + 1}",
            Title,
            category,
            Rationale(candidate, loc, BreakdownText),
            candidate.Rig,
            DspToneOrigin.User);

        // Shaped as a design result so the existing result block, and the four
        // actions under it, take it without knowing where it came from. The
        // confidence pill shows the match % — the same number as the row — and 0
        // for an UNRATED one, which the pill's own text then explains rather than
        // implying a measured zero.
        AsDesignResult = new ToneDesignResult(
            Tone,
            Tone.Description,
            (MatchPercent ?? 0) / 100.0,
            notes,
            string.Empty,
            new AiUsage(candidate.Origin, 0, 0));
    }

    /// <summary>The rationale line the row and the saved tone both carry: the
    /// percentage (or the unrated reason), where it came from, and WHICH feature
    /// blocks it lost on — so the number is never shown alone.</summary>
    private static string Rationale(ToneMatchCandidate candidate, ILocalizationService loc, string breakdown)
    {
        var body = candidate.Score?.Percent is { } percent
            ? loc.Format("ToneMatch_Rationale", percent, candidate.Origin)
            : loc.Format("ToneMatch_RationaleUnrated",
                ToneDesignViewModel.UnratedReasonText(candidate.Score?.UnratedReason, loc), candidate.Origin);
        return breakdown.Length == 0 ? body : body + " " + loc.Format("ToneMatch_RationaleBlocks", breakdown);
    }

    public int Rank { get; }
    public string Title { get; }
    public string ChainSummary { get; }
    public double Distance { get; }

    /// <summary>The matcher's whole score object, for a consumer that wants the raw
    /// anchors. Null when the run produced no score.</summary>
    public MatchScore? Score { get; }

    /// <summary>0–100, or NULL for unrated. Never conflate null with 0.</summary>
    public int? MatchPercent { get; }

    /// <summary>True when <see cref="MatchPercent" /> is a number.</summary>
    public bool IsRated { get; }

    /// <summary>The <see cref="MatchUnrated" /> code, or null when rated.</summary>
    public string? UnratedReason { get; }

    /// <summary>The unrated reason in the app's language; empty when rated.</summary>
    public string UnratedText { get; }

    /// <summary>The pill text: "46 % match", or "Unrated — …".</summary>
    public string MatchText { get; }

    /// <summary>One line saying what 0 % and 100 % mean on this scale.</summary>
    public string ScaleText { get; }

    /// <summary>Where the passage's own variation lands on the scale, or null.</summary>
    public int? SelfDistancePercent { get; }

    /// <summary>True when this candidate is at or inside that variation.</summary>
    public bool WithinTargetNoise { get; }

    /// <summary>The caveat line for that case; empty otherwise.</summary>
    public string NoiseCaveatText { get; }

    /// <summary>The per-feature-block rows behind the headline, worst first.</summary>
    public IReadOnlyList<ToneMatchBlockViewModel> Breakdown { get; }

    /// <summary>The same breakdown as one line, for a row that has no room for a
    /// list.</summary>
    public string BreakdownText { get; }

    public string Origin { get; }
    /// <summary>The prior result's ownership check, retained when this candidate is selected.</summary>
    public Func<bool> ValidityCheck { get; }

    public DspTonePreset Tone { get; }
    public ToneDesignResult AsDesignResult { get; }
}

/// <summary>
///     One feature block under a candidate's headline: what it is called, how much
///     of the remaining distance it is, and whether the candidate is still WORSE
///     there than an information-free amp-and-cab.
/// </summary>
public sealed class ToneMatchBlockViewModel
{
    public ToneMatchBlockViewModel(string block, int sharePercent, bool worseThanReference, ILocalizationService loc)
    {
        ArgumentNullException.ThrowIfNull(loc);
        Block = block;
        SharePercent = sharePercent;
        WorseThanReference = worseThanReference;
        // A missing key resolves to the KEY itself (LocalizationService.GetString),
        // so a block the resx has not named yet shows its raw id rather than
        // `ToneMatch_Block_foo` — a new feature block is a doc change, not a crash.
        var key = $"ToneMatch_Block_{block}";
        var named = loc[key];
        Label = string.Equals(named, key, StringComparison.Ordinal) ? block : named;
        Text = loc.Format("ToneMatch_BlockShare", Label, sharePercent);
    }

    /// <summary>The block's id, as <c>ToneFeatureLayout</c> names it.</summary>
    public string Block { get; }

    /// <summary>A player-facing name for it.</summary>
    public string Label { get; }

    /// <summary>How much of this candidate's remaining distance sits in this block.</summary>
    public int SharePercent { get; }

    /// <summary>True when the reference rig does BETTER here — the honest "the
    /// search did not get this part".</summary>
    public bool WorseThanReference { get; }

    /// <summary>The bound line: "Dynamics 41 %".</summary>
    public string Text { get; }
}
