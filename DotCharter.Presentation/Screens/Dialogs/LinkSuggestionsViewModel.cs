using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Core.Linking;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Dialogs;
using DotCharter.Services.Editing.Linking;

namespace DotCharter.Presentation.Screens.Dialogs;

/// <summary>
///     The summary after a Guitar Pro import found measures to link. It only answers the choice;
///     <see cref="GpLinkSuggestionOffer" /> links or opens the manager.
/// </summary>
public sealed class LinkSuggestionsViewModel : DialogViewModel<LinkSuggestionsChoice>, ILinkSuggestionsScreen,
    IDialogSetup<LinkSuggestionsRequest>
{
    private const int MaxSharedLines = 3;
    private readonly ChartEditDialogServices _edit;
    private string _headline = "";
    private IReadOnlyList<string> _details = [];

    public LinkSuggestionsViewModel(ChartEditDialogServices edit)
    {
        _edit = edit;
        LinkAllCommand = new RelayCommand(() => Close(LinkSuggestionsChoice.LinkAll));
        ReviewCommand = new RelayCommand(() => Close(LinkSuggestionsChoice.Review));
        SkipCommand = new RelayCommand(() => Close(LinkSuggestionsChoice.Skip));
    }

    public string Headline
    {
        get => _headline;
        private set => SetProperty(ref _headline, value);
    }

    public IReadOnlyList<string> Details
    {
        get => _details;
        private set => SetProperty(ref _details, value);
    }

    public ICommand LinkAllCommand { get; }

    public ICommand ReviewCommand { get; }

    public ICommand SkipCommand { get; }

    public void Configure(LinkSuggestionsRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var l = _edit.Localization;
        var suggestions = request.Suggestions;
        Headline = l.Plural("LinkSuggest_GroupsOne", "LinkSuggest_Groups", suggestions.Count, suggestions.Sum(s => s.Runs.Count));
        var details = new List<string>(SharedLines(suggestions));
        if (RepeatLine(suggestions) is { } repeat) details.Add(repeat);
        Details = details;
    }

    /// <summary>"Rhythm and Lead share 48 of 60 measures." for each pair of parts some suggestion links together.</summary>
    private IEnumerable<string> SharedLines(IReadOnlyList<LinkSuggestion> suggestions)
    {
        var project = _edit.State.ActiveProject;
        if (project == null) yield break;
        var total = MeasureGrid.Build(project.Beats).Count;
        var parts = project.Arrangements.Where(a => suggestions.Any(s => s.Runs.Any(r => ReferenceEquals(r.Arrangement, a)))).ToList();
        var lines = 0;
        for (var i = 0; i < parts.Count; i++)
        {
            for (var j = i + 1; j < parts.Count; j++)
            {
                var (a, b) = (parts[i], parts[j]);
                var shared = suggestions
                    .Where(s => s.Runs.Any(r => ReferenceEquals(r.Arrangement, a)) && s.Runs.Any(r => ReferenceEquals(r.Arrangement, b)))
                    .Sum(s => s.MeasureCount * s.Runs.Count(r => ReferenceEquals(r.Arrangement, a)));
                if (shared == 0) continue;
                if (lines++ == MaxSharedLines) yield break;
                yield return _edit.Localization.Format("LinkSuggest_Shared", a.Name, b.Name, shared, total);
            }
        }
    }

    /// <summary>"Chorus repeats 3× in each." for the named suggestion that repeats most often in one part.</summary>
    private string? RepeatLine(IReadOnlyList<LinkSuggestion> suggestions)
    {
        var best = suggestions
            .Where(s => !string.IsNullOrWhiteSpace(s.Name))
            .Select(s => (Suggestion: s, Counts: s.Runs.GroupBy(r => r.Arrangement).Select(g => (Arrangement: g.Key, Count: g.Count())).ToList()))
            .Where(x => x.Counts.Max(c => c.Count) >= 2)
            .OrderByDescending(x => x.Counts.Max(c => c.Count))
            .FirstOrDefault();
        if (best.Suggestion == null) return null;
        var times = best.Counts.Max(c => c.Count);
        if (best.Counts.Count > 1 && best.Counts.All(c => c.Count == times))
            return _edit.Localization.Format("LinkSuggest_RepeatsEach", best.Suggestion.Name, times);
        var where = best.Counts.First(c => c.Count == times).Arrangement.Name;
        return _edit.Localization.Format("LinkSuggest_RepeatsIn", best.Suggestion.Name, times, where);
    }
}
