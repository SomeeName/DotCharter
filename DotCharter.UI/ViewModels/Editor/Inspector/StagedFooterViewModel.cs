using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace DotCharter.UI.ViewModels.Editor.Inspector;

/// <summary>What the pinned footer is asking, if anything (design D's confirmation bar).</summary>
public enum StagedAsk
{
    None,

    /// <summary>Apply reaches more than one sound, skips one, writes one in part or replaces a bend: Confirm / Cancel.</summary>
    Review,

    /// <summary>A Move step on the selection reaches several sounds or leaves one where it is: Confirm / Cancel.</summary>
    Move,

    /// <summary>The page is being left with a draft: Stay / Discard / Apply.</summary>
    Leave
}

/// <summary>
///     The Note tab's pinned footer over the staged pages (design D): the scope and how many sounds Apply
///     writes, the Apply button, the review sentence with Confirm / Cancel when Apply asks first, and the leave
///     guard (Stay / Discard / Apply) when a section switch or a queue step would leave a page with a draft. While
///     it asks, the pages take no input (the view binds <see cref="IsAsking" />); the timeline still does, so a
///     selection or history change withdraws the question and the action it held (<see cref="Dismiss" />): a Confirm
///     never lands on a selection it was not asked about. A draft left on another sound (a timeline click, a level
///     switch) is named and one click away (<see cref="ElsewhereText" />, <see cref="JumpToDraftCommand" />).
///     The plan behind the count, the button and its tip is worked out once per re-read.
/// </summary>
public sealed partial class StagedFooterViewModel : ObservableObject
{
    private readonly InspectorStagedPages _owner;
    private readonly SoundSectionsViewModel _sections;
    private readonly SoundQueueViewModel _queue;
    private Action? _afterAsk;
    private Action? _confirmed;
    private (BulkMove Move, int Delta)? _move;
    private StagedEditReview? _review;
    private IReadOnlyList<ITimedElement>? _elsewhere;

    internal StagedFooterViewModel(InspectorStagedPages owner, SoundSectionsViewModel sections, SoundQueueViewModel queue)
    {
        _owner = owner;
        _sections = sections;
        _queue = queue;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAsking), nameof(IsReviewing), nameof(IsLeaving), nameof(IsIdle))]
    private StagedAsk _asking;

    /// <summary>The review sentence (or the leave question) while the footer asks.</summary>
    [ObservableProperty] private string _askText = string.Empty;

    private InspectorStagedEdits Engine => _owner.Engine;

    private ILocalizationService Loc => _owner.Localization;

    /// <summary>The staged page on screen, or null over a section that writes at once.</summary>
    public StagedPage? Page => PageOf(_sections.Section, Engine.Source);

    /// <summary>Which staged page a section is for this source.</summary>
    public static StagedPage? PageOf(SoundSection section, ITimedElement? source) => section switch
    {
        SoundSection.Placement when source is GuitarNote => StagedPage.Placement,
        SoundSection.Selection => StagedPage.Selection,
        SoundSection.Technique when source is not null => StagedPage.Technique,
        SoundSection.Bends when source is not null => StagedPage.Bends,
        _ => null
    };

    /// <summary>The scope part of the footer: over a staged page, whether edits wait for Apply or are written at once.</summary>
    public bool ShowsApply => Page is not null;

    /// <summary>The Apply button: only while edits wait for it (Settings ▸ Editor's Apply step).</summary>
    public bool ShowsApplyButton => ShowsApply && Engine.ApplyStep;

    public bool IsAsking => Asking != StagedAsk.None;

    public bool IsIdle => !IsAsking;

    /// <summary>Confirm / Cancel (an Apply review or a Move step).</summary>
    public bool IsReviewing => Asking is StagedAsk.Review or StagedAsk.Move;

    /// <summary>Stay / Discard / Apply.</summary>
    public bool IsLeaving => Asking == StagedAsk.Leave;

    /// <summary>How many sounds Apply writes now.</summary>
    public int Count => Current.Count;

    /// <summary>Apply is offered while the draft lands somewhere or the review has a sound to name (a draft that lands
    /// nowhere still says why).</summary>
    public bool CanApply => Current.Text.Length > 0;

    /// <summary>How many other sounds hold a draft (left by a timeline click or a level switch).</summary>
    public int ElsewhereCount => Elsewhere.Count;

    public bool ShowsElsewhere => Engine.ApplyStep && ElsewhereCount > 0;

    /// <summary>"1 draft on another sound": the button that goes back to it.</summary>
    public string ElsewhereText => ElsewhereCount == 0
        ? string.Empty
        : Loc.Plural("Inspector_Staged_ElsewhereOne", "Inspector_Staged_Elsewhere", ElsewhereCount);

    /// <summary>A note's Technique can widen to every selected note: the scope button shows then.</summary>
    public bool CanWidenNoteScope => Page == StagedPage.Technique && Engine.Source is GuitarNote &&
                                     _owner.Editor.Selection.SelectedNotes.Count > 1;

    /// <summary>The scope as plain text: not when it is the note scope's button, nor under the "Apply to" picker,
    /// which already says what Apply reaches and how many.</summary>
    public bool ShowsScopeText => !CanWidenNoteScope && !_sections.ShowApplyBar;

    /// <summary>The scope and the count beside Apply: "This note · 1", "Selected · 5"; over a chord the "Apply to"
    /// ladder names the scope, so only the count. Without the Apply step the count is how many sounds an edit reaches.</summary>
    public string ScopeText
    {
        get
        {
            var reach = Engine.ApplyStep || Page is not { } shown ? Count : Engine.Targets(shown).Count;
            var count = reach.ToString(System.Globalization.CultureInfo.CurrentCulture);
            var scope = Page switch
            {
                StagedPage.Placement or StagedPage.Bends => Loc["Inspector_Staged_ScopeNote"],
                StagedPage.Selection => Loc["Inspector_Staged_ScopeSelected"],
                StagedPage.Technique when Engine.Source is GuitarNote => Loc[Engine.TechniqueOnSelectedNotes
                    ? "Inspector_Staged_ScopeNotes"
                    : "Inspector_Staged_ScopeNote"],
                _ => null
            };
            return scope is null ? "· " + count : scope + " · " + count;
        }
    }

    /// <summary>The Apply button's tooltip: what it writes, or why it writes nothing.</summary>
    public string ApplyTip => Current is { Count: > 0 } review ? review.Text : Loc["Inspector_Staged_NothingToApply"];

    /// <summary>The page's review, worked out once per re-read.</summary>
    private StagedEditReview Current => _review ??= Page is { } page ? Engine.Review(page) : StagedEditReview.None;

    private IReadOnlyList<ITimedElement> Elsewhere => _elsewhere ??= Engine.DraftsElsewhere();

    internal void Reread()
    {
        _review = null;
        _elsewhere = null;
        if (Asking == StagedAsk.Review && Page is not null) AskText = Current.Text;
        OnPropertyChanged(nameof(Page));
        OnPropertyChanged(nameof(ShowsApply));
        OnPropertyChanged(nameof(ShowsApplyButton));
        OnPropertyChanged(nameof(Count));
        OnPropertyChanged(nameof(CanApply));
        OnPropertyChanged(nameof(CanWidenNoteScope));
        OnPropertyChanged(nameof(ScopeText));
        OnPropertyChanged(nameof(ShowsScopeText));
        OnPropertyChanged(nameof(ApplyTip));
        OnPropertyChanged(nameof(ElsewhereCount));
        OnPropertyChanged(nameof(ShowsElsewhere));
        OnPropertyChanged(nameof(ElsewhereText));
    }

    /// <summary>The selection or the history changed under a question: withdraw it, and the action it held.</summary>
    internal void Dismiss()
    {
        if (IsAsking) Clear();
    }

    /// <summary>Apply: at once for one sound with nothing passed by, else the review sentence asks first.</summary>
    [RelayCommand]
    private void Apply()
    {
        if (Page is not { } page) return;
        var review = Engine.Review(page);
        if (review.Text.Length == 0) return;
        if (!review.NeedsConfirm)
        {
            Write(page);
            return;
        }

        Ask(StagedAsk.Review, review.Text, null);
    }

    /// <summary>Confirm: writes what the sentence said (an Apply or a Move step).</summary>
    [RelayCommand]
    private void Confirm()
    {
        // A question the timeline withdrew (Dismiss) confirms nothing.
        if (!IsReviewing) return;
        var move = _move;
        var confirmed = _confirmed;
        var page = Page;
        Clear();
        if (confirmed is not null) confirmed();
        else if (move is { } step) Engine.ApplyMove(step.Move, step.Delta);
        else if (page is { } p) Write(p);
    }

    /// <summary>Cancel (or Stay): back to the page, the draft as it was.</summary>
    [RelayCommand]
    private void Cancel()
    {
        Clear();
        _owner.Refresh();
    }

    /// <summary>Leave the page without its draft.</summary>
    [RelayCommand]
    private void Discard()
    {
        if (!IsLeaving) return;
        var then = _afterAsk;
        if (Page is { } page) Engine.Discard(page);
        Clear();
        _owner.Refresh();
        then?.Invoke();
    }

    /// <summary>Write the draft, then leave.</summary>
    [RelayCommand]
    private void ApplyAndLeave()
    {
        if (!IsLeaving) return;
        var then = _afterAsk;
        if (Page is { } page) Write(page);
        Clear();
        then?.Invoke();
    }

    /// <summary>A section strip click, through the leave guard.</summary>
    [RelayCommand]
    private void ChooseSection(SoundSection section) => Leave(() => _sections.ChooseCommand.Execute(section));

    /// <summary>The footer's ‹: the previous sound the queue filter matches, through the leave guard.</summary>
    [RelayCommand]
    private void PreviousMatch() => Leave(() => _queue.PreviousMatchCommand.Execute(null));

    /// <summary>The footer's ›: the next sound the queue filter matches, through the leave guard.</summary>
    [RelayCommand]
    private void NextMatch() => Leave(() => _queue.NextMatchCommand.Execute(null));

    /// <summary>Back to the first other sound that holds a draft (another level or arrangement too), through the leave guard.</summary>
    [RelayCommand]
    private void JumpToDraft() => Leave(() =>
    {
        if (Engine.DraftsElsewhere() is [var sound, ..]) _owner.Show(sound);
    });

    /// <summary>The note Technique scope: this note, or every selected note.</summary>
    [RelayCommand]
    private void ToggleNoteScope()
    {
        Engine.TechniqueOnSelectedNotes = !Engine.TechniqueOnSelectedNotes;
        _owner.Refresh();
    }

    /// <summary>Runs <paramref name="go" /> now, or asks Stay / Discard / Apply first when the page holds a draft.</summary>
    public void Leave(Action go)
    {
        if (IsAsking) return;
        if (Page is not { } page || Engine.CanLeave(page))
        {
            go();
            return;
        }

        var review = Engine.Review(page);
        var text = review.Count > 0 ? review.Text : Loc["Inspector_Staged_NothingToApply"];
        Ask(StagedAsk.Leave, Loc.Format("Inspector_Staged_Leave", text), go);
    }

    /// <summary>A Selection "Move" step: at once when it reaches one sound and leaves none, else Confirm / Cancel.</summary>
    internal void Move(BulkMove move, int delta)
    {
        if (IsAsking) return;
        var review = Engine.ReviewMove(move, delta);
        if (review.Count == 0) return;
        if (!review.NeedsConfirm)
        {
            Engine.ApplyMove(move, delta);
            return;
        }

        _move = (move, delta);
        Ask(StagedAsk.Move, review.Text, null);
    }

    /// <summary>Asks <paramref name="text" /> with Confirm / Cancel for a page that writes at once (Findings' "Fix all"):
    /// Confirm runs <paramref name="then" />; a selection or history change withdraws it as any other question.</summary>
    internal void AskToConfirm(string text, Action then)
    {
        if (IsAsking) return;
        _confirmed = then;
        Ask(StagedAsk.Review, text, null);
    }

    private void Write(StagedPage page)
    {
        Engine.Apply(page);
        _owner.Refresh();
    }

    private void Ask(StagedAsk kind, string text, Action? then)
    {
        _afterAsk = then;
        AskText = text;
        Asking = kind;
    }

    private void Clear()
    {
        _afterAsk = null;
        _confirmed = null;
        _move = null;
        Asking = StagedAsk.None;
        AskText = string.Empty;
    }
}
