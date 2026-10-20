using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Services.Ai.Match;

namespace DotCharter.UI.ViewModels.PlayMode.ToneSections;

/// <summary>One entry of the stem picker: the contract's own stem id, and a word
/// for it in the player's language.</summary>
/// <param name="Id">One of <see cref="ToneStems" />.</param>
/// <param name="Label">The localized name shown in the picker.</param>
public sealed record ToneStemOption(string Id, string Label);

/// <summary>
///     One section of the song: "0:10 to 0:30, guitar, clean", and everything
///     that happens to it — the candidates a match found, the one the player
///     chose, and the narrator's words about it.
///
///     <para>
///         <b>The times are TEXT, and the text is the truth.</b> Parsing happens
///         on every keystroke and the parsed value is what
///         <see cref="ToSection" /> hands the matcher; an unparseable box leaves
///         <see cref="IsValid" /> false and the row out of the run rather than
///         quietly contributing 0:00 to 0:00. The two "set from playhead"
///         buttons write the same text a user could have typed, so the box never
///         shows a value the parser would refuse.
///     </para>
///     <para>
///         <b>Selecting a candidate publishes it as the PANEL's result.</b> That
///         is the same integration "Match from audio" has: Hear it, Save as tone
///         and Discard are not re-implemented per section and never learn that
///         the rig came from one.
///     </para>
/// </summary>
public sealed partial class ToneSectionRowViewModel : ObservableObject
{
    private readonly ILocalizationService _loc;
    private readonly Func<double> _playheadMs;
    private readonly Action<ToneSectionRowViewModel> _remove;
    private readonly Func<ToneSectionRowViewModel, Task> _explain;
    private readonly Action<ToneSectionRowViewModel> _publish;
    private readonly Func<bool> _canNarrate;

    /// <summary>Builds a row. Every collaborator is a delegate for the same
    /// reason the panel's own are: a row is constructed by the list and must not
    /// reach back into it.</summary>
    /// <param name="id">Stable id; the section's id in the contract.</param>
    /// <param name="label">The tone's working name — it becomes the preset's.</param>
    /// <param name="stem">The stem this section is matched against.</param>
    /// <param name="from">Where the section starts.</param>
    /// <param name="to">Where it ends.</param>
    /// <param name="stems">The picker's options, shared with every row.</param>
    /// <param name="playheadMs">The transport's current position.</param>
    /// <param name="remove">Drops this row from the list.</param>
    /// <param name="explain">Asks the narrator about the selected candidate.</param>
    /// <param name="publish">Publishes the selected candidate as the panel's result.</param>
    /// <param name="canNarrate">Whether a narrator is registered at all.</param>
    /// <param name="loc">Localization.</param>
    public ToneSectionRowViewModel(
        string id,
        string label,
        string stem,
        TimeSpan from,
        TimeSpan to,
        IReadOnlyList<ToneStemOption> stems,
        Func<double> playheadMs,
        Action<ToneSectionRowViewModel> remove,
        Func<ToneSectionRowViewModel, Task> explain,
        Action<ToneSectionRowViewModel> publish,
        Func<bool> canNarrate,
        ILocalizationService loc)
    {
        Id = id;
        Stems = stems;
        _playheadMs = playheadMs;
        _remove = remove;
        _explain = explain;
        _publish = publish;
        _canNarrate = canNarrate;
        _loc = loc;

        _label = label;
        _selectedStem = stems.FirstOrDefault(s => s.Id == stem) ?? stems[0];
        _fromText = ToneSectionTime.Format(from);
        _toText = ToneSectionTime.Format(to);
        Revalidate();
    }

    /// <summary>Stable across a re-match, which is how a set result finds its row
    /// again.</summary>
    public string Id { get; }

    /// <summary>The picker's options, shared with every other row — one list, so
    /// the vocabulary cannot drift between rows.</summary>
    public IReadOnlyList<ToneStemOption> Stems { get; }

    [ObservableProperty] private string _label;

    [ObservableProperty] private ToneStemOption _selectedStem;

    [ObservableProperty] private string _fromText;

    [ObservableProperty] private string _toText;

    partial void OnFromTextChanged(string value) => Revalidate();

    partial void OnToTextChanged(string value) => Revalidate();

    /// <summary>The parsed start. Meaningless while <see cref="IsValid" /> is
    /// false.</summary>
    public TimeSpan From { get; private set; }

    /// <summary>The parsed end.</summary>
    public TimeSpan To { get; private set; }

    /// <summary>True when both boxes parse, the end is after the start, and the
    /// span is long enough for the feature extractor to average over
    /// (<see cref="ToneSection.MinDuration" />). Only valid rows go into a
    /// match.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTimeError))]
    private bool _isValid;

    /// <summary>Why the row is not valid, in one sentence, or empty.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTimeError))]
    private string _timeError = string.Empty;

    /// <summary>True when <see cref="TimeError" /> has something to say.</summary>
    public bool HasTimeError => TimeError.Length > 0;

    /// <summary>"0:20" — how long the section is, for the row's own eyebrow.</summary>
    [ObservableProperty] private string _durationText = string.Empty;

    /// <summary>Where this section stands in the run: waiting, matched, or the
    /// one sentence that says why it failed. A set never fails as a whole because
    /// one part could not be isolated, so this is PER ROW.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    private string _statusText = string.Empty;

    /// <summary>True when <see cref="StatusText" /> has something to show.</summary>
    public bool HasStatus => StatusText.Length > 0;

    /// <summary>True when the row's status is a FAILURE rather than progress —
    /// the template colours it differently, and a binding cannot ask.</summary>
    [ObservableProperty] private bool _isFailed;

    /// <summary>This section's candidates, ranked best first. Empty collapses the
    /// row's list, which is what keeps an unmatched section one line tall.</summary>
    public ObservableCollection<ToneMatchCandidateViewModel> Candidates { get; } = [];

    /// <summary>True once a match has answered for this section.</summary>
    public bool HasCandidates => Candidates.Count > 0;

    /// <summary>The player's pick. Setting it publishes the candidate as the
    /// panel's result — see the class remarks.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExplainCommand))]
    private ToneMatchCandidateViewModel? _selectedCandidate;

    partial void OnSelectedCandidateChanged(ToneMatchCandidateViewModel? value)
    {
        // Clearing the list sets this to null; republishing then would put a
        // discarded tone back on screen.
        if (value is null) return;
        Explanation = string.Empty;
        _publish(this);
    }

    /// <summary>The narrator's words about the selected candidate, or empty. One
    /// call per "Explain", never one per candidate.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasExplanation))]
    private string _explanation = string.Empty;

    /// <summary>True when there is a narration to show.</summary>
    public bool HasExplanation => Explanation.Length > 0;

    /// <summary>True while the narrator is being asked.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExplainCommand))]
    private bool _isExplaining;

    private bool CanExplain => SelectedCandidate != null && !IsExplaining && _canNarrate();

    /// <summary>Asks the narrator what the chosen rig is doing.</summary>
    [RelayCommand(CanExecute = nameof(CanExplain))]
    private Task Explain() => _explain(this);

    /// <summary>Writes the transport's position into the FROM box, as the same
    /// text a user could have typed.</summary>
    [RelayCommand]
    private void SetFromPlayhead() =>
        FromText = ToneSectionTime.Format(TimeSpan.FromMilliseconds(Math.Max(0, _playheadMs())));

    /// <summary>Writes the transport's position into the TO box.</summary>
    [RelayCommand]
    private void SetToPlayhead() =>
        ToText = ToneSectionTime.Format(TimeSpan.FromMilliseconds(Math.Max(0, _playheadMs())));

    /// <summary>Drops this section from the list.</summary>
    [RelayCommand]
    private void Remove() => _remove(this);

    /// <summary>Replaces this row's candidates with a finished match, best first,
    /// and SELECTS the best one — a ranked list whose top row the player must
    /// click is a step that only ever has one answer.</summary>
    /// <param name="result">The section's match, or null when it failed.</param>
    /// <param name="host">Supplies the chain summary, so a row never reaches the
    /// module catalogue itself.</param>
    internal void PublishCandidates(ToneMatchResult? result, IToneSectionHost host)
    {
        Candidates.Clear();
        SelectedCandidate = null;
        Explanation = string.Empty;

        if (result is not null)
        {
            // RANKING distance — see ToneDesignViewModel.PublishCandidates.
            var ranked = result.Candidates.OrderBy(c => c.RankingDistance).ToList();
            for (var i = 0; i < ranked.Count; i++)
            {
                // The SCORE rides on the candidate (W5b) — a view model cannot build
                // a scale out of a distance, which is exactly what the old
                // `MatchPercent(distance, floor)` tried to do and why every row read
                // 100 %.
                Candidates.Add(new ToneMatchCandidateViewModel(
                    ranked[i],
                    i,
                    host.ChainSummary(ranked[i].Rig),
                    ToneDesignViewModel.UnknownMatchCategory,
                    result.Notes,
                    _loc));
            }
        }

        OnPropertyChanged(nameof(HasCandidates));
        if (Candidates.Count > 0) SelectedCandidate = Candidates[0];
    }

    /// <summary>The contract's view of this row. Only ever called on a row whose
    /// <see cref="IsValid" /> is true.</summary>
    public ToneSection ToSection() => new(Id, Label.Trim(), SelectedStem.Id, From, To);

    private void Revalidate()
    {
        if (!ToneSectionTime.TryParse(FromText, out var from) ||
            !ToneSectionTime.TryParse(ToText, out var to))
        {
            Fail("ToneSections_Fault_BadTime");
            return;
        }

        From = from;
        To = to;

        if (to <= from)
        {
            Fail("ToneSections_Fault_EndBeforeStart");
            return;
        }

        if (to - from < ToneSection.MinDuration)
        {
            // The floor is the contract's, not this view's: under it the
            // separator's edge artefacts dominate whatever the section contains.
            TimeError = _loc.Format(
                "ToneSections_Fault_TooShort", (int)ToneSection.MinDuration.TotalSeconds);
            IsValid = false;
            DurationText = ToneSectionTime.Format(to - from);
            return;
        }

        TimeError = string.Empty;
        IsValid = true;
        DurationText = ToneSectionTime.Format(to - from);
    }

    private void Fail(string key)
    {
        TimeError = _loc[key];
        IsValid = false;
        DurationText = string.Empty;
    }
}
