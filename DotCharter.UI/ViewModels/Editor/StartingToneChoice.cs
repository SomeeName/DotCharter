using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DotCharter.Services.Editing.Tones;

namespace DotCharter.UI.ViewModels.Editor;

/// <summary>
///     The Song tab's starting-tone chooser (inspector design D, the Tones card): the tone the ACTIVE arrangement
///     starts in, picked from the tones the Tones list names. A pick is written at once, one undo node
///     (<see cref="SetStartingToneAction" />); a re-read never writes (a picker that drops its selection to -1 while
///     its list changes is put back on the arrangement's answer). It follows an arrangement switch on its own, so the
///     rest of the Song pane re-reads nothing then.
/// </summary>
public sealed partial class StartingToneChoice : ObservableObject
{
    private readonly ProjectState _state;
    private readonly IHistoryService _history;
    private readonly Func<IEnumerable<string>> _toneNames;
    private bool _reading;

    public StartingToneChoice(ProjectState state, IHistoryService history, Func<IEnumerable<string>> toneNames)
    {
        _state = state;
        _history = history;
        _toneNames = toneNames;
        state.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ProjectState.ActiveArrangement)) Refresh();
        };
    }

    [ObservableProperty] private int _selectedIndex = -1;
    [ObservableProperty] private bool _hasArrangement;

    /// <summary>The tones to start in: every tone the Tones list names, the current one first when it names none.</summary>
    public ObservableCollection<string> Options { get; } = [];

    /// <summary>Re-reads the active arrangement's starting tone over the tones the Tones list names.</summary>
    public void Refresh() => Refresh(_toneNames());

    private void Refresh(IEnumerable<string> toneNames)
    {
        _reading = true;
        try
        {
            var target = _state.ActiveArrangement;
            var current = target?.StartingTone.Trim() ?? "";
            HasArrangement = target is not null;
            var names = toneNames.Where(n => n.Length > 0).ToList();
            if (current.Length > 0 && !names.Contains(current, StringComparer.OrdinalIgnoreCase)) names.Insert(0, current);
            if (!Options.SequenceEqual(names, StringComparer.Ordinal))
            {
                Options.Clear();
                foreach (var name in names) Options.Add(name);
            }

            SelectedIndex = names.FindIndex(n => string.Equals(n, current, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            _reading = false;
        }
    }

    partial void OnSelectedIndexChanged(int value)
    {
        if (_reading) return;
        var target = _state.ActiveArrangement;
        if (target is null || value < 0 || value >= Options.Count)
        {
            Refresh([.. Options]);
            return;
        }

        var action = new SetStartingToneAction(target, Options[value]);
        if (action.HasEffect) _history.Do(action);
    }
}
