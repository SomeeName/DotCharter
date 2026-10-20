using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Presentation.Screens.Editor;
using DotCharter.Services.Ai.Chat;

namespace DotCharter.UI.ViewModels.Editor.Inspector;

/// <summary>
/// The Issues tab's AI row for the finding the charter last jumped to, exposed as
///     <c>InspectorIssuesViewModel.IssueAi</c>, beside the deterministic fix menu (<see cref="IssueFixViewModel" />)
///     and Auto-fix, which stay the first choice.
///     <list type="bullet">
///         <item><b>Fix with AI</b> — only for a finding a job of the AI chart pass can fix
///         (<see cref="AiChatFindings.JobFor(ArrangementIssueType)" />; the phrase findings D03 I10 I25 I26 take
/// the sections &amp; phrases job on the whole arrangement); any other one shows why instead of the
///         button. The request goes to the AI chat as a message: a scoped pass on the finding's phrase with the
///         finding, its code and the rule text in the prompt, HELD for the review card, judged by the same rule
///         gate, which also rejects a chunk that leaves the targeted finding. Accepting it is one undo step.</item>
///         <item><b>Fix all N in this phrase with AI</b> — every fixable finding of the same level in that phrase.</item>
/// <item><b>Explain with AI</b> — any finding: the chat's Ask / explain job answers in the
///         chat and writes nothing.</item>
///     </list>
///     Like the fix menu it holds a finding, not a selection: a recompute keeps the offer while the same
///     finding stands and withdraws it once it is gone.
/// </summary>
public sealed partial class IssueAiViewModel : ObservableObject
{
    private readonly ProjectState _state;
    private readonly ILocalizationService _localization;
    private readonly Func<IAiChatFindingsDoor?> _door;
    private ArrangementIssue? _issue;
    private Arrangement? _arrangement;
    private ChartLevel? _level;
    private IReadOnlyList<ArrangementIssue> _phrase = [];

    public IssueAiViewModel(ProjectState state, ILocalizationService localization, Func<IAiChatFindingsDoor?> door)
    {
        _state = state;
        _localization = localization;
        _door = door;
    }

    /// <summary>The finding on offer, or null.</summary>
    public ArrangementIssue? Issue => _issue;

    /// <summary>True while a finding is on offer: Explain is always there for it.</summary>
    public bool HasIssue => _issue != null;

    /// <summary>True when a job of the AI chart pass can fix the finding.</summary>
    public bool CanFix => _issue != null && AiChatFindings.CanFix(_issue);

    /// <summary>True when a finding is on offer that no job can fix: the row says so instead.</summary>
    public bool HasNoJob => _issue != null && !CanFix;

    /// <summary>"No AI job can fix D03 …".</summary>
    public string NoJobText => HasNoJob ? _localization.Format("Inspector_IssueAi_NoJob", _issue!.Code) : "";

    /// <summary>True when the finding's phrase holds more than one fixable finding.</summary>
    public bool HasPhraseFix => CanFix && _phrase.Count > 1;

    /// <summary>The fixable findings of the finding's level in its phrase (the finding included).</summary>
    public IReadOnlyList<ArrangementIssue> PhraseFindings => _phrase;

    public string FixPhraseLabel => HasPhraseFix ? _localization.Format("Inspector_IssueAi_FixPhrase", _phrase.Count) : "";

    /// <summary>Offers the AI row for <paramref name="issue" /> on <paramref name="level" />, with the other
    /// fixable findings of <paramref name="issues" /> in its phrase; null withdraws it.</summary>
    public void Offer(ArrangementIssue? issue, Arrangement? arrangement, ChartLevel? level,
        IReadOnlyList<ArrangementIssue> issues)
    {
        _issue = issue;
        _arrangement = arrangement;
        _level = level ?? _state.ActiveLevel;
        _phrase = issue == null || arrangement == null || _level == null || !AiChatFindings.CanFix(issue)
            ? []
            : SamePhrase(issue, arrangement, _level, issues);
        if (issue == null) _arrangement = null;
        OnPropertyChanged(string.Empty);
        FixCommand.NotifyCanExecuteChanged();
        FixPhraseCommand.NotifyCanExecuteChanged();
        ExplainCommand.NotifyCanExecuteChanged();
    }

    /// <summary>After a recompute: keep the offer while the same finding stands, else withdraw it.</summary>
    public void Refresh(IReadOnlyList<ArrangementIssue> issues, Arrangement? arrangement)
    {
        if (_issue is not { } shown) return;
        var same = issues.FirstOrDefault(i => i.Type == shown.Type && i.TimeMs == shown.TimeMs
                                              && ReferenceEquals(i.Note, shown.Note) && ReferenceEquals(i.Chord, shown.Chord)
                                              && ReferenceEquals(i.HandShape, shown.HandShape) && ReferenceEquals(i.Anchor, shown.Anchor));
        Offer(same, same == null ? null : arrangement, _level, issues);
    }

    [RelayCommand(CanExecute = nameof(CanFix))]
    private Task FixAsync() => _issue == null ? Task.CompletedTask : AskFix([_issue]);

    [RelayCommand(CanExecute = nameof(HasPhraseFix))]
    private Task FixPhraseAsync() => AskFix(_phrase);

    [RelayCommand(CanExecute = nameof(HasIssue))]
    private async Task ExplainAsync()
    {
        if (_issue == null || _arrangement == null || _level == null || _door() is not { } door) return;
        if (AiChatFindings.ExplainRequest(_issue, _arrangement, _level, _state.Beats, _localization, door.PickedModelId) is { } request)
            await door.AskAsync(request);
    }

    private async Task AskFix(IReadOnlyList<ArrangementIssue> issues)
    {
        if (_arrangement == null || _level == null || _door() is not { } door) return;
        if (AiChatFindings.FixRequest(issues, _arrangement, _level, _state.Beats, _localization, door.PickedModelId) is { } request)
            await door.AskAsync(request);
    }

    /// <summary>The fixable findings on <paramref name="level" /> timed inside the finding's phrase.</summary>
    private List<ArrangementIssue> SamePhrase(ArrangementIssue issue, Arrangement arrangement, ChartLevel level,
        IReadOnlyList<ArrangementIssue> issues)
    {
        var beats = _state.Beats;
        if (AiChatFindings.Scope(arrangement, level, beats, [issue]) is not { } scope) return [issue];
        var from = scope.From is { } f ? f.GetPositionMs(beats) - 0.5 : double.MinValue;
        var to = scope.To is { } t ? t.GetPositionMs(beats) + 0.5 : double.MaxValue;
        // One job must fix them all: a phrase finding never joins a hand-position or finger one.
        return [.. issues.Where(i => AiChatFindings.CanFix(i) && AiChatFindings.JobFor([issue, i]) is not null
                                     && i.TimeMs is { } ms && ms >= from && ms <= to
                                     && (ReferenceEquals(i, issue) || OnLevel(i, arrangement, level)))];
    }

    private static bool OnLevel(ArrangementIssue issue, Arrangement arrangement, ChartLevel level) =>
        ReferenceEquals(InspectorIssuesViewModel.LevelOwning(arrangement, issue) ?? level, level);
}
