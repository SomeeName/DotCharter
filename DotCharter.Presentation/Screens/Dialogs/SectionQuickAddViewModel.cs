using CommunityToolkit.Mvvm.ComponentModel;
using DotCharter.Core.Domain.Events;
using DotCharter.Core.Domain.Project;
using DotCharter.Core.Utilities;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Services.Editing.Events;
using DotCharter.Services.State;
using DotCharter.Tone.Infrastructure.Configuration;

namespace DotCharter.Presentation.Screens.Dialogs;

/// <summary>One section name the quick-add popover offers: the stored key and the label shown.</summary>
public sealed record SectionSuggestion(string Key, string Display);

/// <summary>
///     The section popover on the timeline's Sections row: a name box with the known section names as
///     suggestions, Enter commits. The commit is the dialog's own edit (<see cref="EventMarkerEdit" />,
///     one <c>CompositeAction</c>), so it is one undo step and keeps whatever else the marker there carries.
///     "More..." hands the typed name to the full event dialog (<see cref="MoreRequest" />).
/// </summary>
public sealed class SectionQuickAddViewModel : ObservableObject
{
    private readonly ChartEditDialogServices _edit;
    private readonly IEventEditingService _events;
    private Arrangement? _target;
    private bool _isOpen;
    private double _placementMs;
    private string _name = "";
    private IReadOnlyList<SectionSuggestion> _suggestions = [];

    public SectionQuickAddViewModel(ChartEditDialogServices edit, IEventEditingService events)
    {
        _edit = edit;
        _events = events;
    }

    public bool IsOpen
    {
        get => _isOpen;
        private set => SetProperty(ref _isOpen, value);
    }

    /// <summary>The song time the popover hangs at: the snapped grid time of the click.</summary>
    public double PlacementMs
    {
        get => _placementMs;
        private set => SetProperty(ref _placementMs, value);
    }

    /// <summary>What the box says; typing narrows <see cref="Suggestions" />.</summary>
    public string Name
    {
        get => _name;
        set
        {
            if (!SetProperty(ref _name, value)) return;
            Suggestions = Filter(value);
            OnPropertyChanged(nameof(CanCommit));
        }
    }

    public IReadOnlyList<SectionSuggestion> Suggestions
    {
        get => _suggestions;
        private set => SetProperty(ref _suggestions, value);
    }

    /// <summary>True when the box names a section Enter can write.</summary>
    public bool CanCommit => Resolve(Name) != null;

    /// <summary>
    ///     Opens the popover at <paramref name="timeMs" /> of <paramref name="target" /> (the active arrangement when
    ///     null). The box starts on the marker's own section there, else the last one used: sections repeat.
    /// </summary>
    public void Begin(double timeMs, Arrangement? target)
    {
        var editor = _edit.Editor;
        var beats = editor.Beats;
        _target = target ?? editor.ActiveArrangement;
        var snapped = editor.SnapToGrid(timeMs);
        PlacementMs = snapped.GetPositionMs(beats);
        var existing = _target == null ? null : EventMarkerEdit.EventAt(_target, PlacementMs, beats);
        Name = existing?.Section is { Length: > 0 } own ? own : _events.LastSection;
        Suggestions = Filter(Name);
        OnPropertyChanged(nameof(CanCommit));
        IsOpen = true;
        _edit.Logger.Info(AppLogCategory.Events,
            $"Section quick-add opened at requestedMs={timeMs:0.###}, snappedMs={PlacementMs:0.###}, name='{Name}'.");
    }

    /// <summary>Writes the section as one undo step and closes. False (staying open) when the box names no known section.</summary>
    public bool Commit()
    {
        if (!IsOpen || Resolve(Name) is not { } key) return false;
        var editor = _edit.Editor;
        var beats = editor.Beats;
        var lookup = _target ?? editor.ActiveArrangement;
        var timeMs = PlacementMs;
        var position = TimelineMath.PositionFromMs(timeMs, beats);
        var existing = lookup == null ? null : EventMarkerEdit.EventAt(lookup, timeMs, beats);
        var tone = lookup == null ? null : EventMarkerEdit.NearestTone(lookup.ToneChanges, timeMs, beats);
        if (tone != null) timeMs = tone.Position.GetPositionMs(beats);

        var phrase = existing == null || string.IsNullOrWhiteSpace(existing.Phrase) || existing.Phrase == existing.Section
            ? key
            : existing.Phrase;
        var solo = !string.IsNullOrWhiteSpace(phrase) && lookup != null &&
                   lookup.PhraseInfos.TryGetValue(phrase, out var info) && info.Solo;
        var fields = new EventMarkerFields(key, phrase, existing?.Status ?? SectionStatus.None,
            existing?.Events ?? _events.LastEvents, solo, tone?.ToneName ?? "");

        var remembered = existing == null ? _events.LastArrangementNames.ToHashSet(StringComparer.Ordinal) : [];
        var targets = (_edit.State.ActiveProject?.Arrangements ?? [])
            .Where(a => ReferenceEquals(a, lookup) || remembered.Contains(a.Name)).ToList();
        if (targets.Count == 0 && lookup != null) targets.Add(lookup);

        var actions = new List<IChartAction>();
        foreach (var arrangement in targets)
            EventMarkerEdit.AppendActions(arrangement, position, timeMs, beats, fields, actions);

        _events.RememberLastMarker(key, phrase);
        _events.RememberLastTargets(fields.Events, targets.Select(t => t.Name));
        if (actions.Count > 0 && _edit.History.Do(new CompositeAction(actions) { LabelKey = "History_EditEvents" }))
        {
            editor.RequestRedraw();
            _edit.Logger.Info(AppLogCategory.Events,
                $"Section quick-add committed at {timeMs:0.###} ms. Section='{key}', phrase='{phrase}', actions={actions.Count}.");
        }

        Close();
        return true;
    }

    /// <summary>Closes without writing anything.</summary>
    public void Cancel()
    {
        if (!IsOpen) return;
        _edit.Logger.Info(AppLogCategory.Events, "Section quick-add canceled.");
        Close();
    }

    /// <summary>
    ///     Closes the popover and returns the full dialog's request, carrying the typed section when it names a
    ///     known one. Nothing is written: the dialog's Save is the commit.
    /// </summary>
    public EventRequest? MoreRequest()
    {
        if (!IsOpen) return null;
        var request = new EventRequest(PlacementMs, _target, Resolve(Name));
        Close();
        return request;
    }

    private void Close()
    {
        _target = null;
        IsOpen = false;
    }

    /// <summary>The stored key the text names: a key or label exactly, else the first suggestion that starts with it.</summary>
    private static string? Resolve(string text)
    {
        var typed = text.Trim();
        if (typed.Length == 0) return null;
        return RsSectionCatalog.Normalize(typed) ?? Filter(typed).FirstOrDefault()?.Key;
    }

    private static List<SectionSuggestion> Filter(string text)
    {
        var typed = text.Trim();
        var all = RsSectionCatalog.Names.Select(n => new SectionSuggestion(n, RsSectionCatalog.DisplayName(n))).ToList();
        if (typed.Length == 0) return all;
        var starts = all.Where(s => s.Display.StartsWith(typed, StringComparison.OrdinalIgnoreCase) ||
                                    s.Key.StartsWith(typed, StringComparison.OrdinalIgnoreCase));
        var contains = all.Where(s => s.Display.Contains(typed, StringComparison.OrdinalIgnoreCase));
        return [.. starts.Concat(contains).Distinct()];
    }
}
