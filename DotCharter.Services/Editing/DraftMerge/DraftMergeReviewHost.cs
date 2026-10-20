using DotCharter.Services.State;

namespace DotCharter.Services.Editing.DraftMerge;

/// <summary>What a merge verb on the draft review did.</summary>
public enum DraftMergeOutcome
{
    /// <summary>Written: one undo node.</summary>
    Merged,

    /// <summary>Nothing written: every phrase asked for had sounds by now and was not chosen for
    /// replacing. Those rows left the review.</summary>
    Stale,

    /// <summary>Nothing written: the review's arrangement or level is gone. The review closed.</summary>
    Gone,

    /// <summary>Nothing written: no review is held, or the phrase is not one of its.</summary>
    None
}

/// <summary>
/// Holds the ONE chart-from-audio draft under review between the dialog that makes it
///     (File ▸ Chart from audio, "Merge into the current arrangement") and the merge card over the
///     timeline that decides it phrase by phrase. A merge writes through <see cref="IHistoryService" />
///     as one undo node per verb (<see cref="DraftMergeWriter" />); a reject only drops the row. The
///     review closes when its last row goes, on Discard, or when a new one is held.
/// </summary>
public sealed class DraftMergeReviewHost(IHistoryService history, IFretAnchorGenerationService anchors)
{
    /// <summary>The review on the card, or null.</summary>
    public DraftMergeReview? Review { get; private set; }

    /// <summary>Raised after every change: a review held, a row merged or rejected, the review closed.</summary>
    public event EventHandler? Changed;

    /// <summary>Puts <paramref name="review" /> on the card, replacing any review held before. A review with
    /// no phrases is not held.</summary>
    public void Hold(DraftMergeReview review)
    {
        ArgumentNullException.ThrowIfNull(review);
        Review = review.Phrases.Count > 0 ? review : null;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Closes the review; the chart is not touched.</summary>
    public void Discard()
    {
        if (Review is null) return;
        Review = null;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Merges <paramref name="phrase" />: the phrase's notes become the draft's, as ONE undo node.</summary>
    public DraftMergeOutcome Accept(DraftMergePhrase phrase)
    {
        ArgumentNullException.ThrowIfNull(phrase);
        return Review is { } review && review.Phrases.Contains(phrase) ? Merge(review, [phrase]) : DraftMergeOutcome.None;
    }

    /// <summary>Merges every phrase still listed, as ONE undo node.</summary>
    public DraftMergeOutcome AcceptAll() => Review is { } review ? Merge(review, [.. review.Phrases]) : DraftMergeOutcome.None;

    /// <summary>Drops <paramref name="phrase" /> from the review; nothing is written.</summary>
    public void Reject(DraftMergePhrase phrase)
    {
        ArgumentNullException.ThrowIfNull(phrase);
        if (Review is not { } review || !review.Remove(phrase)) return;
        if (review.Phrases.Count == 0) Review = null;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private DraftMergeOutcome Merge(DraftMergeReview review, IReadOnlyList<DraftMergePhrase> phrases)
    {
        if (!review.IsLive)
        {
            Review = null;
            Changed?.Invoke(this, EventArgs.Empty);
            return DraftMergeOutcome.Gone;
        }

        var action = DraftMergeWriter.Build(review, phrases, anchors, out _);
        var outcome = action is not null && history.Do(action) ? DraftMergeOutcome.Merged : DraftMergeOutcome.Stale;
        // Every phrase asked for leaves: merged, or refused because it has sounds now that the
        // charter never chose to replace.
        foreach (var phrase in phrases) review.Remove(phrase);
        if (review.Phrases.Count == 0) Review = null;
        Changed?.Invoke(this, EventArgs.Empty);
        return outcome;
    }
}
