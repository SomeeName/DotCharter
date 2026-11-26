using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Services.Editing.Anchors;
using DotCharter.Services.Editing.Tones;

namespace DotCharter.UI.ViewModels.Editor.Inspector;

/// <summary>
/// The fix menu for the finding the charter last jumped to on the Issues tab, exposed as
///     <c>InspectorIssuesViewModel.IssueFix</c>. Today it answers E04 and I46 — a grip whose index finger
///     disagrees with the hand position — with <see cref="IndexAnchorRepair" />: the DEFAULT first
///     (re-finger the grip the way the arrangement already plays it under this anchor, when it does;
///     otherwise move the anchor), and the other one beside it. Each is one undo step. A chord under a
///     hand shape that plays its frets the way the anchor wants is offered that shape's fingering first.
///     <para>
///         It holds a finding, not a selection: a recompute keeps the offer while the same finding
///         (same rule, same element) still stands and withdraws it once it is gone, so a button never
///         applies a repair to a disagreement that is gone.
///     </para>
///     <para>
///         I30 — a tone change on a note — offers <see cref="ToneChangeNudge" />: move the change one
///         32nd note earlier on the beat grid. When a guard stops it the button stays, disabled, with the
///         reason under it (<see cref="BlockedReason" />). The press computes the move again on the live
///         arrangement and beat grid: an undo between the offer and the debounced re-validation may have
///         put a change on the landing spot, and then the button turns disabled with that reason instead.
///     </para>
///     <para>
///         D17/D18 — a sustain or hand shape across a phrase, section or END — offer
///         <see cref="BoundaryCrossing.Trim" />: end it on the boundary, one undo step, only where that trim
///         is unambiguous. The press computes the trim again on the live level.
///     </para>
///     <para>
///         D50-D52 — an FHP before the first note, between notes or inside a sustain — offer
///         <see cref="FhpPlacement.MoveToNextSound" /> through the same path: onto the next note or chord, or
///         removed when that sound already has its own, one undo step.
///     </para>
///     <para>
///         D40 — a note or chord 1 to 3 ms off the beat grid — offers <see cref="BeatGridSnap.Snap" />: onto the
///         nearest grid line with what starts with it, one undo step, computed again on the live level.
///     </para>
///     <para>
///         D15/D90 — a hand shape or sustain ending closer to the next note than a 32nd note — and D91 — a
///         sustain under 5 ms — offer <see cref="ChartSpacingFix.For" />: end it a 32nd note before the
///         next note, or clear the stray sustain, one undo step, again computed on the live level.
///     </para>
///     <para>
///         D110 — spots the import guessed — offers <see cref="AudioCheckMarks" />: "Checked" clears the spot the
///         finding points at (it then moves on to the next), "All checked" clears them all, one undo step each. Fix
///         all never applies either: a spot is checked by listening, not in bulk unheard.
///     </para>
/// /// </summary>
public sealed partial class IssueFixViewModel : ObservableObject
{
    private readonly IHistoryService _history;
    private readonly ILocalizationService _localization;
    private ArrangementIssue? _issue;
    private ChartLevel? _level;
    private IndexAnchorRepair? _repair;
    private ToneChangeNudge? _nudge;
    private bool _trim;
    private bool _snap;
    private IList<BeatMarker>? _beats;
    private Arrangement? _arrangement;
    private ChartProject? _project;
    private LyricFixProposal? _lyricFix;
    private bool _audioCheck;

    public IssueFixViewModel(IHistoryService history, ILocalizationService localization)
    {
        _history = history;
        _localization = localization;
    }

    /// <summary>The repair on offer, or null.</summary>
    public IndexAnchorRepair? Repair => _repair;

    /// <summary>The tone-change move on offer for an I30, or null.</summary>
    public ToneChangeNudge? Nudge => _nudge;

    public bool HasFix => _repair != null || _nudge != null || _lyricFix != null || _trim || _snap || _audioCheck;

    /// <summary>Why the default fix cannot run, or "" when it can.</summary>
    public string BlockedReason => _nudge is { CanApply: false } nudge ? BlockedText(nudge.Block) : "";

    public bool IsBlocked => BlockedReason.Length > 0;

    /// <summary>True when both fixes exist; the alternative is always the anchor move.</summary>
    public bool HasAlternative => _audioCheck || _repair is { PrefersRefinger: true, MoveAnchorTo: not null };

    /// <summary>"Fix E04:".</summary>
    public string Title => _issue == null ? "" : _localization.Format("Inspector_IssueFix_Title", _issue.Code);

    private bool MovesAnchor => _issue?.Type is ArrangementIssueType.AnchorBeforeFirstNote
        or ArrangementIssueType.AnchorBetweenNotes or ArrangementIssueType.AnchorInsideSustain;

    public string DefaultLabel => _lyricFix != null
        ? _localization.Format(_lyricFix.IsSplit ? "Inspector_IssueFix_LyricSplit" : "Inspector_IssueFix_LyricText", _lyricFix.Text)
        : _trim && _issue?.Type == ArrangementIssueType.HarmonicVibrato ? _localization["Inspector_IssueFix_RemoveVibrato"]
        : _audioCheck ? _localization["Inspector_IssueFix_AudioChecked"]
        : _snap ? _localization["Inspector_IssueFix_SnapToGrid"]
        : _trim ? _localization[MovesAnchor ? "Inspector_IssueFix_MoveAnchorToNextNote" : TrimKeys(_issue).Label]
        : _nudge != null ? _localization["Inspector_IssueFix_ToneEarlier"]
        : _repair == null ? "" : _repair.PrefersRefinger ? RefingerLabel(_repair) : MoveLabel(_repair);

    public string DefaultTip => _lyricFix != null
        ? _localization[_lyricFix.IsSplit ? "Inspector_IssueFix_LyricSplitTip" : "Inspector_IssueFix_LyricTextTip"]
        : _trim && _issue?.Type == ArrangementIssueType.HarmonicVibrato ? _localization["Inspector_IssueFix_RemoveVibratoTip"]
        : _audioCheck ? _localization["Inspector_IssueFix_AudioCheckedTip"]
        : _snap ? _localization["Inspector_IssueFix_SnapToGridTip"]
        : _trim ? _localization[MovesAnchor ? "Inspector_IssueFix_MoveAnchorToNextNoteTip" : TrimKeys(_issue).Tip]
        : _nudge != null ? _localization["Inspector_IssueFix_ToneEarlierTip"]
        : _repair == null ? "" : _repair.PrefersRefinger ? RefingerTip(_repair) : MoveTip(_repair);

    public string AlternativeLabel => _audioCheck ? _localization["Inspector_IssueFix_AudioAllChecked"]
        : HasAlternative ? MoveLabel(_repair!) : "";

    public string AlternativeTip => _audioCheck ? _localization["Inspector_IssueFix_AudioAllCheckedTip"]
        : HasAlternative ? MoveTip(_repair!) : "";

    /// <summary>
    ///     Whether this menu would offer a fix for <paramref name="issue" /> on <paramref name="level" />: the SAME rule
    ///     check <see cref="Offer" /> runs, so a finding row draws its Fix exactly when the menu has one (a grip with nothing
    ///     to repair gets none).
    /// </summary>
    public static bool CanOffer(ArrangementIssue? issue, Arrangement? arrangement, ChartLevel? level, IList<BeatMarker>? beats,
        ChartProject? project = null)
    {
        if (LyricStyleFix.For(issue, project) != null || AudioCheck(issue, arrangement) != null)
            return true;
        var (repair, nudge) = Find(issue, arrangement, level, beats);
        return repair != null || nudge != null || Trim(issue, arrangement, level, beats) != null || Snap(issue, level, beats) != null;
    }

    private static (IndexAnchorRepair? Repair, ToneChangeNudge? Nudge) Find(ArrangementIssue? issue, Arrangement? arrangement,
        ChartLevel? level, IList<BeatMarker>? beats)
    {
        if (issue is { Type: ArrangementIssueType.IndexFingerNotOnAnchorFret or ArrangementIssueType.FingeringAnchorMismatch, Anchor: { } anchor }
            && arrangement != null && level != null)
        {
            return (IndexAnchorRepair.For(arrangement, level, issue.Chord, issue.HandShape, anchor), null);
        }

        if (issue is { Type: ArrangementIssueType.ToneChangeOnNote, TimeMs: { } timeMs } && arrangement != null && beats != null)
            return (null, ToneChangeNudge.For(arrangement, beats, timeMs));
        return (null, null);
    }

    private static IChartAction? Trim(ArrangementIssue? issue, Arrangement? arrangement, ChartLevel? level,
        IList<BeatMarker>? beats) =>
        issue != null && arrangement != null && level != null
            ? BoundaryCrossing.Trim(arrangement, level, issue) ?? FhpPlacement.MoveToNextSound(level, issue)
                ?? HarmonicVibratoFix.For(issue, level) ?? (beats == null ? null : ChartSpacingFix.For(level, issue, beats))
            : null;

    /// <summary>The label and tip keys of the trim on offer.</summary>
    private static (string Label, string Tip) TrimKeys(ArrangementIssue? issue) => issue?.Type switch
    {
        ArrangementIssueType.SustainUnderFiveMs => ("Inspector_IssueFix_ClearSustain", "Inspector_IssueFix_ClearSustainTip"),
        ArrangementIssueType.SustainTooCloseToNext or ArrangementIssueType.HandShapeGap =>
            ("Inspector_IssueFix_TrimToGap", "Inspector_IssueFix_TrimToGapTip"),
        _ => ("Inspector_IssueFix_TrimToBoundary", "Inspector_IssueFix_TrimToBoundaryTip")
    };

    private static IChartAction? AudioCheck(ArrangementIssue? issue, Arrangement? arrangement) =>
        issue?.Type == ArrangementIssueType.NeedsAudioCheck && arrangement != null ? AudioCheckMarks.CheckFirst(arrangement) : null;

    private static IChartAction? Snap(ArrangementIssue? issue, ChartLevel? level, IList<BeatMarker>? beats) =>
        issue != null && level != null && beats != null ? BeatGridSnap.Snap(level, issue, beats) : null;

    /// <summary>
    ///     Offers the fixes for <paramref name="issue" /> on <paramref name="level" />, or withdraws the
    ///     offer when the finding has none (any other rule, or nothing to repair).
    /// </summary>
    public void Offer(ArrangementIssue? issue, Arrangement? arrangement, ChartLevel? level,
        IList<BeatMarker>? beats = null, ChartProject? project = null)
    {
        _project = project;
        _lyricFix = LyricStyleFix.For(issue, project);
        _issue = null;
        _level = null;
        _repair = null;
        _nudge = null;
        _trim = false;
        _snap = false;
        _audioCheck = AudioCheck(issue, arrangement) != null;
        _beats = beats;
        _arrangement = arrangement;
        (_repair, _nudge) = Find(issue, arrangement, level, beats);
        _trim = _repair == null && _nudge == null && Trim(issue, arrangement, level, beats) != null;
        _snap = _repair == null && _nudge == null && !_trim && Snap(issue, level, beats) != null;
        if (_repair != null || _nudge != null || _lyricFix != null || _trim || _snap || _audioCheck)
        {
            _issue = issue;
            if (_repair != null || _trim || _snap) _level = level;
        }

        OnPropertyChanged(string.Empty);
        ApplyDefaultCommand.NotifyCanExecuteChanged();
    }

    /// <summary>After a recompute: keep the offer while the same finding stands, else withdraw it.</summary>
    public void Refresh(IReadOnlyList<ArrangementIssue> issues, Arrangement? arrangement)
    {
        if (_issue is not { } shown) return;
        var same = issues.FirstOrDefault(i => i.Type == shown.Type && ReferenceEquals(i.Chord, shown.Chord)
                                              && ReferenceEquals(i.HandShape, shown.HandShape)
                                              && ReferenceEquals(i.Note, shown.Note)
                                              && Equals(i.Lyric, shown.Lyric) && i.MessageKey == shown.MessageKey);
        Offer(same, arrangement, _level, _beats, _project);
    }

    /// <summary>The default repair of <paramref name="issue" /> on the live arrangement, or null when it has none (the
    /// same choice <see cref="ApplyDefault" /> makes): the Findings page's "Fix all" counts and applies these.</summary>
    internal static IChartAction? DefaultFix(ArrangementIssue issue, Arrangement? arrangement, ChartLevel? level,
        IList<BeatMarker>? beats, ChartProject? project = null)
    {
        if (LyricStyleFix.For(issue, project) is { } lyric) return lyric.Action;
        if (arrangement == null) return null;
        if (Trim(issue, arrangement, level, beats) is { } trim) return trim;
        if (Snap(issue, level, beats) is { } snap) return snap;
        if (issue is { Type: ArrangementIssueType.IndexFingerNotOnAnchorFret or ArrangementIssueType.FingeringAnchorMismatch, Anchor: { } anchor })
            return level == null ? null : IndexAnchorRepair.For(arrangement, level, issue.Chord, issue.HandShape, anchor)?.Default(level);
        return issue is { Type: ArrangementIssueType.ToneChangeOnNote, TimeMs: { } timeMs } && beats != null
            ? ToneChangeNudge.For(arrangement, beats, timeMs)?.Action()
            : null;
    }

    private bool CanApplyDefault() => _lyricFix != null || _audioCheck || _trim || _snap || (_nudge?.CanApply ?? _repair != null);

    [RelayCommand(CanExecute = nameof(CanApplyDefault))]
    private void ApplyDefault()
    {
        // A lyric fix is recomputed on the live project, so an edit since the offer is never overwritten.
        if (_lyricFix != null) Apply(LyricStyleFix.For(_issue, _project)?.Action);
        else if (_audioCheck) Apply(AudioCheck(_issue, _arrangement));
        else if (_trim) Apply(Trim(_issue, _arrangement, _level, _beats));
        else if (_snap) Apply(Snap(_issue, _level, _beats));
        else if (_nudge != null) ApplyNudge();
        else if (_repair != null && _level != null) Apply(_repair.Default(_level));
    }

    [RelayCommand]
    private void ApplyAlternative()
    {
        if (_audioCheck && _arrangement != null) Apply(AudioCheckMarks.CheckAll(_arrangement));
        else if (_repair != null && _level != null) Apply(_repair.Alternative(_level));
    }

    /// <summary>Re-checks every guard on the live arrangement first; a move blocked since the offer
    ///     is not applied, and the offer shows the new reason.</summary>
    private void ApplyNudge()
    {
        if (_issue?.TimeMs is not { } timeMs || _arrangement == null || _beats == null) return;
        _nudge = ToneChangeNudge.For(_arrangement, _beats, timeMs);
        if (_nudge is { CanApply: true } nudge)
        {
            Apply(nudge.Action());
            return;
        }

        OnPropertyChanged(string.Empty);
        ApplyDefaultCommand.NotifyCanExecuteChanged();
    }

    private void Apply(IChartAction? action)
    {
        if (action == null) return;
        _history.Do(action);
        Offer(null, null, null);
    }

    /// <summary>"1-3": the fingers of the fretted strings, lowest string first.</summary>
    internal static string Fingering(ChordTemplate template) =>
        string.Join('-', template.Frets.Keys.Order()
            .Select(s => template.Fingers.TryGetValue(s, out var f) && f >= 0 ? f.ToString(System.Globalization.CultureInfo.InvariantCulture) : "x"));

    private string BlockedText(ToneChangeNudgeBlock block) => block switch
    {
        ToneChangeNudgeBlock.ChartStart => _localization["Inspector_IssueFix_ToneBlockedChartStart"],
        ToneChangeNudgeBlock.ToneChange => _localization["Inspector_IssueFix_ToneBlockedToneChange"],
        ToneChangeNudgeBlock.PhraseStart => _localization["Inspector_IssueFix_ToneBlockedPhraseStart"],
        ToneChangeNudgeBlock.Note => _localization["Inspector_IssueFix_ToneBlockedNote"],
        ToneChangeNudgeBlock.SeveralChanges => _localization["Inspector_IssueFix_ToneBlockedSeveralChanges"],
        _ => ""
    };

    private string RefingerLabel(IndexAnchorRepair repair) => repair.FromHandShape
        ? _localization.Format("Inspector_IssueFix_UseShapeFingering", Fingering(repair.Refinger!))
        : _localization.Plural("Inspector_IssueFix_RefingerOne", "Inspector_IssueFix_Refinger", repair.Evidence,
            Fingering(repair.Refinger!));

    private string RefingerTip(IndexAnchorRepair repair) => _localization.Format(
        repair.FromHandShape ? "Inspector_IssueFix_UseShapeFingeringTip" : "Inspector_IssueFix_RefingerTip",
        repair.Anchor.Fret, Fingering(repair.Refinger!));

    private string MoveLabel(IndexAnchorRepair repair) =>
        _localization.Format("Inspector_IssueFix_MoveAnchor", repair.MoveAnchorTo ?? repair.Anchor.Fret);

    private string MoveTip(IndexAnchorRepair repair) =>
        _localization.Format("Inspector_IssueFix_MoveAnchorTip", repair.Anchor.Fret, repair.MoveAnchorTo ?? repair.Anchor.Fret);
}
