// The AI chart pass, phase 3: the review card over the timeline — one dense row per proposed section (or,
// for the chat's sections & phrases job, per section/phrase change; for its Tones job, per tone, with
// the proposed gear chain and Audition) that seeks when clicked, with Accept / Reject toggles, the keyboard's
// current row, Accept all, Reject all, Apply (one undo step) and Discard — and the marks it publishes.
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Services.Ai.Chart;
using DotCharter.Tone.UI.ViewModels.Shell;
using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.UI.ViewModels.Shell;

/// <summary>
/// The review of the AI chart pass's proposals. The pass HOLDS its proposals
///     (<see cref="AiChartPassRun.Review" />); this card lists one row per section, and the
///     editor marks what the sections still on the table would change: the same
/// <see cref="ChordUseHighlight" /> the chord panel's bulk-edit preview feeds,
///     published through <see cref="ChartEditorViewModel.PublishReviewHighlight" /> and read
///     off <see cref="AiChartReview.Marks" />, the composition the write itself uses.
///     <para>
///         Non-modal on purpose: the charter scrubs, plays and even edits the chart while it
///         is open. The marks follow every history step; a section edited meanwhile is stale
///         and drops out of the marks and the write.
///     </para>
/// </summary>
public sealed partial class AiChartReviewViewModel : ViewModelBase
{
    private readonly AiChartPassRun _run;
    private readonly ChartEditorViewModel _editor;
    private readonly ILocalizationService _loc;
    private readonly ProjectState _state;
    private readonly IAiToneAudition? _audition;
    private AiChartReview? _review;

    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private string _subtitle = string.Empty;
    [ObservableProperty] private string _countsText = string.Empty;
    [ObservableProperty] private string _applyText = string.Empty;
    [ObservableProperty] private string _failedText = string.Empty;
    [ObservableProperty] private string _hintText = string.Empty;
    [ObservableProperty] private bool _hasFailed;
    [ObservableProperty] private AiChartReviewRowViewModel? _currentRow;

    public AiChartReviewViewModel(AiChartPassRun run, ChartEditorViewModel editor, IHistoryService history,
        ProjectState state, ILocalizationService loc, IAiToneAudition? audition = null)
    {
        _run = run;
        _editor = editor;
        _loc = loc;
        _state = state;
        _audition = audition;
        _run.ReviewChanged += OnReviewChanged;
        history.HistoryChanged += RefreshMarks;
        state.PropertyChanged += OnStateChanged;
        Load(_run.Review);
    }

    /// <summary>One row per proposed section, in song order.</summary>
    public ObservableCollection<AiChartReviewRowViewModel> Rows { get; } = [];

    /// <summary>The highlight published last — test seam.</summary>
    internal ChordUseHighlight Published { get; private set; } = ChordUseHighlight.None;

    /// <summary>Accepts every section not decided yet.</summary>
    [RelayCommand]
    private void AcceptAll()
    {
        if (_review is null) return;
        _review.AcceptAll();
        Refresh();
    }

    /// <summary>Rejects every section not decided yet; an accepted one stays accepted.</summary>
    [RelayCommand]
    private void RejectAll()
    {
        if (_review is null) return;
        _review.RejectAll();
        Refresh();
    }

    /// <summary>Writes the accepted sections as ONE undo step and closes the review.</summary>
    [RelayCommand(CanExecute = nameof(CanApply))]
    private void Apply() => _run.ApplyReview();

    private bool CanApply() => _review is { AcceptedCount: > 0 };

    /// <summary>Drops every proposal; the chart is not touched.</summary>
    [RelayCommand]
    private void Discard() => _run.DiscardReview();

    internal void Decide(AiChartReviewRowViewModel row, AiChartDecision decision)
    {
        if (_review is null) return;
        if (row.Item is { } item) _review.Decide(item, decision);
        else if (row.ToneItem is { } tone) _review.Decide(tone, decision);
        else _review.Decide(row.Chunk!, decision);
        Refresh();
    }

    /// <summary>Go to: the row becomes the keyboard's current row and the playhead moves to it.</summary>
    internal void Show(AiChartReviewRowViewModel row)
    {
        CurrentRow = row;
        _editor.ExecuteSeek(row.StartMs);
    }

    /// <summary>↑ / ↓: moves the current row by <paramref name="delta" /> and goes to it.
    /// With no current row either key starts at the first. Stops at the ends.</summary>
    /// <returns>The row now current, or null with no rows.</returns>
    public AiChartReviewRowViewModel? Step(int delta)
    {
        if (Rows.Count == 0) return null;
        var index = CurrentRow is { } current && Rows.IndexOf(current) is >= 0 and var at
            ? Math.Clamp(at + delta, 0, Rows.Count - 1)
            : 0;
        Show(Rows[index]);
        return Rows[index];
    }

    /// <summary>A: accepts the current row. Never toggles — a second A keeps it accepted.</summary>
    public void AcceptCurrent()
    {
        if (CurrentRow is { } row) Decide(row, AiChartDecision.Accepted);
    }

    /// <summary>R: rejects the current row.</summary>
    public void RejectCurrent()
    {
        if (CurrentRow is { } row) Decide(row, AiChartDecision.Rejected);
    }

    partial void OnCurrentRowChanged(AiChartReviewRowViewModel? oldValue, AiChartReviewRowViewModel? newValue)
    {
        if (oldValue is not null) oldValue.IsCurrent = false;
        if (newValue is not null) newValue.IsCurrent = true;
    }

    /// <summary>Plays a proposed tone's section once with the proposed body on the rig. A refusal
    /// (no rig, no body yet) goes to the status rail; nothing is written either way.</summary>
    internal void Audition(AiToneRowViewModel tone)
    {
        var item = tone.Item;
        var why = _audition is null
            ? _loc["AiChartReview_AuditionUnavailable"]
            : _audition.Start(item.Key, item.ProposedJson, item.Window.FromMs, item.Window.ToMs);
        if (why is not null) _state.SetMessage(why, StatusSeverity.Warning);
    }

    /// <summary>Whether a tone row's Audition can play, and why not.</summary>
    internal string? AuditionBlocker(AiToneReviewItem item) =>
        _audition is null ? _loc["AiChartReview_AuditionUnavailable"]
        : item.CurrentJson is null ? _loc["AiChartReview_AuditionNeedsBody"]
        : null;

    private void OnReviewChanged(object? sender, EventArgs e) => Load(_run.Review);

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ProjectState.ActiveProject) or nameof(ProjectState.ActiveArrangement)
            or nameof(ProjectState.ActiveLevel))
        {
            RefreshMarks();
        }
    }

    private void Load(AiChartReview? review)
    {
        // An audition belongs to the review it came from: a new review, Apply or Discard ends it.
        _audition?.Stop();
        _review = review;
        CurrentRow = null;
        Rows.Clear();
        if (review is not null)
        {
            foreach (var chunk in review.Chunks)
                Rows.Add(new AiChartReviewRowViewModel(this, chunk, RowDetail(chunk)));
            foreach (var item in review.Items)
            {
                Rows.Add(new AiChartReviewRowViewModel(this, item, AiStructureText.Label(item.Change, _loc),
                    AiStructureText.Detail(item, _loc)));
            }

            foreach (var tone in review.Tones)
                Rows.Add(new AiChartReviewRowViewModel(this, new AiToneRowViewModel(this, tone, _loc)));
        }

        IsOpen = review is not null;
        Subtitle = review is null ? string.Empty : $"{JobText(review.Job)} · {review.Arrangement.Name}";
        var failed = review?.Result.Rejected.Count ?? 0;
        HasFailed = failed > 0;
        FailedText = failed == 0 ? string.Empty
            : review!.Job switch
            {
                AiChartJob.SectionsAndPhrases => _loc.Plural("AiStructure_FailedOne", "AiStructure_Failed", failed),
                AiChartJob.Tones => _loc.Plural("AiTones_FailedOne", "AiTones_Failed", failed),
                _ => _loc.Plural("AiChartReview_FailedOne", "AiChartReview_Failed", failed)
            };
        Refresh();
    }

    private void Refresh()
    {
        foreach (var row in Rows) row.Refresh(_loc);
        var review = _review;
        CountsText = review is null
            ? string.Empty
            : _loc.Format("AiChartReview_Counts", review.AcceptedCount, review.RejectedCount, review.PendingCount);
        ApplyText = _loc.Format("AiChartReview_Apply", review?.AcceptedCount ?? 0);
        HintText = review is null
            ? string.Empty
            // Tones mark nothing on the timeline: the card says what to do with them instead.
            : string.Join(Environment.NewLine, Subtitle,
                _loc[review.Job == AiChartJob.Tones ? "AiChartReview_HintTones" : "AiChartReview_Hint"],
                _loc["AiChartReview_Keys"]);
        ApplyCommand.NotifyCanExecuteChanged();
        RefreshMarks();
    }

    /// <summary>Re-reads the marks off the review: after a decision, and after every history
    /// step (an undo replaces chords, an edit can make a section stale).</summary>
    private void RefreshMarks()
    {
        var highlight = ChordUseHighlight.None;
        if (_review is { Items.Count: > 0 } structure)
        {
            // The sections & phrases job: the ribbon's markers, off the write's own composition,
            // and the hand positions a proposed phrase brings on their levels.
            var marks = structure.StructureMarks();
            highlight = ChordUseHighlight.ForEvents(structure.Arrangement, marks.Sections, marks.Phrases, marks.Proposed,
                [.. marks.Anchors.Select(a => (a.Level, a.Anchor))]);
        }
        else if (_review is { } review)
        {
            var targets = review.Marks();
            highlight = ChordUseHighlight.ForLevel(review.Arrangement, review.Level, targets.Chords,
                targets.HandShapes, targets.RemovedAnchors, targets.AddedAnchors);
        }

        Published = highlight;
        _editor.PublishReviewHighlight(highlight);
    }

    private string JobText(AiChartJob job) => _loc[job switch
    {
        AiChartJob.FhpAndFingering => "AiChartReview_JobFhpFingering",
        AiChartJob.FhpOnly => "AiChartReview_JobFhp",
        AiChartJob.FingeringOnly => "AiChartReview_JobFingering",
        AiChartJob.SectionsAndPhrases => "AiChartReview_JobStructure",
        AiChartJob.Tones => "AiChartReview_JobTones",
        _ => "AiChartReview_JobNames"
    }];

    private string RowDetail(AiChartReviewChunk chunk)
    {
        var changes = chunk.Changes;
        var parts = new List<string>(3);
        if (chunk.HandPositionsChanged > 0)
            parts.Add(_loc.Plural("AiChartReview_AnchorsOne", "AiChartReview_Anchors", chunk.HandPositionsChanged));
        if (changes.ChordsRefingered > 0)
            parts.Add(_loc.Plural("AiChartReview_FingeringsOne", "AiChartReview_Fingerings", changes.ChordsRefingered));
        if (changes.NamesChanged > 0)
            parts.Add(_loc.Plural("AiChartReview_NamesOne", "AiChartReview_Names", changes.NamesChanged));
        return string.Join(" · ", parts);
    }
}

/// <summary>One proposed section of the review: where it is, what it changes, and the
/// charter's decision.</summary>
public sealed partial class AiChartReviewRowViewModel : ObservableObject
{
    private readonly AiChartReviewViewModel _owner;

    [ObservableProperty] private bool _isAccepted;
    [ObservableProperty] private bool _isRejected;
    [ObservableProperty] private bool _isCurrent;
    [ObservableProperty] private string _stateText = string.Empty;
    [ObservableProperty] private string _accessibleName = string.Empty;
    [ObservableProperty] private string _tip = string.Empty;

    internal AiChartReviewRowViewModel(AiChartReviewViewModel owner, AiChartReviewChunk chunk, string detail)
    {
        _owner = owner;
        Chunk = chunk;
        StartMs = chunk.StartMs;
        Detail = detail;
        // The last section runs to the end of the level: its start says where it is.
        Span = chunk.EndMs is { } end ? $"{Clock(chunk.StartMs)} – {Clock(end)}" : Clock(chunk.StartMs);
        var named = !string.IsNullOrWhiteSpace(chunk.Phrases);
        Name = named ? chunk.Phrases : Span;
        Title = named ? $"{Span} · {chunk.Phrases}" : Span;
        Summary = string.Join(" · ", new[] { named ? Span : string.Empty, detail }.Where(p => p.Length > 0));
        Where = named ? (chunk.EndMs is { } to ? $"{Clock(chunk.StartMs)}–{Clock(to)}" : Clock(chunk.StartMs)) : string.Empty;
    }

    /// <summary>A section &amp; phrase change row: its label ("Section · Chorus") is the heading,
    /// its time the span, what it does ("Renamed from “Verse”") the detail.</summary>
    internal AiChartReviewRowViewModel(AiChartReviewViewModel owner, AiStructureReviewItem item, string label, string detail)
    {
        _owner = owner;
        Item = item;
        StartMs = item.StartMs;
        Detail = detail;
        Span = AiStructureText.Clock(item.StartMs);
        Name = label;
        Title = $"{Span} · {label}";
        Summary = $"{Span} · {detail}";
        Where = Span;
    }

    /// <summary>A proposed tone row, in the dense row: the tone's name, the song window it was generated
    /// from, the proposed chain in short; Go to seeks to where its change is placed. <see cref="Tone" /> carries
    /// the full chains (the row's tooltip) and Audition.</summary>
    internal AiChartReviewRowViewModel(AiChartReviewViewModel owner, AiToneRowViewModel tone)
    {
        _owner = owner;
        Tone = tone;
        ToneItem = tone.Item;
        var window = tone.Item.Window;
        StartMs = window.PlacedMs;
        Detail = tone.ShortText;
        Span = AiStructureText.Clock(StartMs);
        Name = tone.Item.Name;
        Title = $"{Span} · {Name}";
        Summary = $"{Span} · {Detail}";
        Where = $"{Clock(window.FromMs)}–{Clock(window.ToMs)}";
    }

    /// <summary>The proposed tone's chains and Audition; null for every other row, so its block is
    /// not even built there.</summary>
    public AiToneRowViewModel? Tone { get; }

    /// <summary>The proposed tone, or null for a section or structure row.</summary>
    internal AiToneReviewItem? ToneItem { get; }

    /// <summary>The proposed section, or null for a structure row.</summary>
    internal AiChartReviewChunk? Chunk { get; }

    /// <summary>The proposed section or phrase change, or null for a section row.</summary>
    internal AiStructureReviewItem? Item { get; }

    /// <summary>Where Go to seeks.</summary>
    internal double StartMs { get; }

    /// <summary>"0:12.5 – 0:31.0 · verse": the row's accessible name, with <see cref="Detail" />.</summary>
    public string Title { get; }

    /// <summary>The row's heading: the section's phrase names ("verse"), or its span when it has none.</summary>
    public string Name { get; }

    /// <summary>"0:12.5 – 0:31.0", or "0:12.5" for the last section.</summary>
    public string Span { get; }

    /// <summary>"2 hand positions · 5 fingerings".</summary>
    public string Detail { get; }

    /// <summary>The span (unless it IS the heading) and the detail, in one line.</summary>
    public string Summary { get; }

    /// <summary>The dense row's time column: "0:12.5–0:31.0" with no spaces, or empty when the
    /// span already is the row's name.</summary>
    public string Where { get; }

    /// <summary>The ✓ toggle: accepts a row, and puts an accepted one back to deciding.</summary>
    [RelayCommand]
    private void Accept() => _owner.Decide(this, IsAccepted ? AiChartDecision.Pending : AiChartDecision.Accepted);

    /// <summary>The ✕ toggle: rejects a row, and puts a rejected one back to deciding.</summary>
    [RelayCommand]
    private void Reject() => _owner.Decide(this, IsRejected ? AiChartDecision.Pending : AiChartDecision.Rejected);

    /// <summary>Moves the playhead to the section's start.</summary>
    [RelayCommand]
    private void Show() => _owner.Show(this);

    internal void Refresh(ILocalizationService loc)
    {
        var decision = Item?.Decision ?? ToneItem?.Decision ?? Chunk!.Decision;
        IsAccepted = decision == AiChartDecision.Accepted;
        IsRejected = decision == AiChartDecision.Rejected;
        StateText = loc[decision switch
        {
            AiChartDecision.Accepted => "AiChartReview_StateAccepted",
            AiChartDecision.Rejected => "AiChartReview_StateRejected",
            _ => "AiChartReview_StatePending"
        }];
        AccessibleName = $"{Title} · {Detail} · {StateText}";
        // The row trims its name and its detail; hovering it reads both whole, then what a click does. A tone
        // row reads its whole proposed chain beside the current one, with every knob, and its evidence.
        Tip = Tone is { } tone
            ? string.Join(Environment.NewLine, Title, $"{loc["AiChartReview_ToneProposed"]}: {tone.ProposedText}",
                $"{loc["AiChartReview_ToneCurrent"]}: {tone.CurrentText}", tone.Evidence, loc["AiChartReview_Show_Tip"])
            : $"{Title} · {Detail}{Environment.NewLine}{loc["AiChartReview_Show_Tip"]}";
    }

    /// <summary>The row's accessible name.</summary>
    public override string ToString() => $"{Title} · {Detail}";

    private static string Clock(double ms) =>
        TimeSpan.FromMilliseconds(Math.Max(0, ms)).ToString(@"m\:ss\.f", CultureInfo.InvariantCulture);
}
