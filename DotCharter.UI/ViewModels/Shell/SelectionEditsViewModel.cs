using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Services.Editing.Selection.Patterns;

namespace DotCharter.UI.ViewModels.Shell;

/// <summary>
///     The verbs that act on the current selection and say what they did on the status rail:
///     select similar, a saved pattern, the technique toggles (and their group forms) and
///     double / half time. The Edit menu, the command palette and the keys all come here.
///     The menu rows are enabled by <see cref="HasSelection" />; the commands themselves have no
///     CanExecute, because a key press may act on the implied hand-shape or pen target and the
///     verb says "nothing selected" itself when there is none.
/// </summary>
public sealed partial class SelectionEditsViewModel : ObservableObject
{
    private readonly IGlobalEditingCoordinator _global;
    private readonly IHighwayEditingService _highway;
    private readonly ILocalizationService _localization;
    private readonly ProjectState _projectState;
    private readonly ISelectionService _selection;
    private readonly MatchPatternStore _patterns;

    [ObservableProperty] private bool _hasSelection;

    public SelectionEditsViewModel(IGlobalEditingCoordinator global, IHighwayEditingService highway,
        ISelectionService selection, ProjectState projectState, ILocalizationService localization,
        MatchPatternStore patterns)
    {
        _patterns = patterns;
        _global = global;
        _highway = highway;
        _selection = selection;
        _projectState = projectState;
        _localization = localization;
        selection.SelectionChanged += (_, _) => HasSelection = _highway.HasTechniqueTarget;
        HasSelection = _highway.HasTechniqueTarget;
    }

    /// <summary>The user's saved patterns, one menu row each (refreshed when the Select submenu opens).</summary>
    public ObservableCollection<SavedPatternItem> SavedPatterns { get; } = [];

    public bool HasSavedPatterns => SavedPatterns.Count > 0;

    public void RefreshSavedPatterns()
    {
        SavedPatterns.Clear();
        foreach (var pattern in _patterns.List())
        {
            var captured = pattern;
            SavedPatterns.Add(new SavedPatternItem(pattern.Name, () => SelectPattern(captured)));
        }

        OnPropertyChanged(nameof(HasSavedPatterns));
    }

    /// <summary>Adds what is alike to the selection and reports the counts.</summary>
    public void SelectSimilar() => Report(_global.SelectLikePattern());

    /// <summary>Runs a saved pattern over the selection and reports the counts.</summary>
    public void SelectPattern(MatchPattern pattern) => Report(_global.SelectByPattern(pattern));

    private void Report(SelectionAddition added)
    {
        var (text, severity) = SelectionAdditionStatus.Describe(added, _localization);
        _projectState.SetMessage(text, severity);
    }

    /// <summary>Runs one technique verb and reports what the selected sounds carry afterwards.</summary>
    public void Toggle(TechniqueSpec spec, Action<IHighwayEditingService> verb)
    {
        verb(_highway);
        var states = spec.StatesOf(_selection.SelectedNotes, _selection.SelectedChords).ToList();
        var (text, severity) = TechniqueToggleStatus.Describe(spec, states, _localization);
        _projectState.SetMessage(text, severity);
    }

    [RelayCommand]
    private void ToggleHopo() => Toggle(TechniqueSpec.Hopo, h => h.ToggleSelectedHopo());

    [RelayCommand]
    private void ToggleHarmonic() => Toggle(TechniqueSpec.Harmonic, h => h.ToggleSelectedHarmonic());

    /// <summary>Named for the technique: the plain mute command name is taken by a dead command of the pedalboard.</summary>
    [RelayCommand]
    private void ToggleMuteTechnique() => Toggle(TechniqueSpec.Mute, h => h.ToggleSelectedMute());

    [RelayCommand]
    private void ToggleLinkNext() => Toggle(TechniqueSpec.LinkNext, h => h.ToggleSelectedLinkNext());

    [RelayCommand]
    private void ToggleVibrato() => Toggle(TechniqueSpec.Vibrato, h => h.ToggleSelectedVibrato());

    [RelayCommand]
    private void ToggleTremolo() => Toggle(TechniqueSpec.Tremolo, h => h.ToggleSelectedTremolo());

    [RelayCommand]
    private void PickSlide() => _highway.PickSlide();

    [RelayCommand]
    private void TogglePicking() => Toggle(TechniqueSpec.Picking, h => h.ToggleSelectedPicking());

    [RelayCommand]
    private void ToggleAccent() => Toggle(TechniqueSpec.Accent, h => h.ToggleSelectedAccent());

    [RelayCommand]
    private void ToggleIgnore() => Toggle(TechniqueSpec.Ignore, h => h.ToggleSelectedIgnore());

    [RelayCommand]
    private void TogglePassOtherNotes() => Toggle(TechniqueSpec.PassOtherNotes, h => h.ToggleSelectedPassOtherNotes());

    [RelayCommand]
    private void ToggleHopoGroup() => Toggle(TechniqueSpec.Hopo, h => h.ToggleSelectedHopoGroup());

    [RelayCommand]
    private void ToggleHarmonicGroup() => Toggle(TechniqueSpec.Harmonic, h => h.ToggleSelectedHarmonicGroup());

    [RelayCommand]
    private void ToggleMuteGroup() => Toggle(TechniqueSpec.Mute, h => h.ToggleSelectedMuteGroup());

    [RelayCommand]
    private void ToggleLinkNextGroup() => Toggle(TechniqueSpec.LinkNext, h => h.ToggleSelectedLinkNextGroup());

    [RelayCommand]
    private void ToggleVibratoGroup() => Toggle(TechniqueSpec.Vibrato, h => h.ToggleSelectedVibratoGroup());

    [RelayCommand]
    private void ToggleTremoloGroup() => Toggle(TechniqueSpec.Tremolo, h => h.ToggleSelectedTremoloGroup());

    [RelayCommand]
    private void TogglePickingGroup() => Toggle(TechniqueSpec.Picking, h => h.ToggleSelectedPickingGroup());

    [RelayCommand]
    private void ToggleAccentGroup() => Toggle(TechniqueSpec.Accent, h => h.ToggleSelectedAccentGroup());

    [RelayCommand]
    private void ToggleIgnoreGroup() => Toggle(TechniqueSpec.Ignore, h => h.ToggleSelectedIgnoreGroup());

    [RelayCommand]
    private void TogglePassOtherNotesGroup() =>
        Toggle(TechniqueSpec.PassOtherNotes, h => h.ToggleSelectedPassOtherNotesGroup());

    /// <summary>Double time reports its own outcome (<c>TempoScaleCoordinator</c>).</summary>
    [RelayCommand]
    private void DoubleTime() => _global.ApplyDoubleTimeToSelection();

    [RelayCommand]
    private void HalfTime() => _global.ApplyHalfTimeToSelection();
}

/// <summary>One saved pattern as a menu row: its name and the command that runs it.</summary>
public sealed class SavedPatternItem
{
    public SavedPatternItem(string name, Action run)
    {
        Name = name;
        RunCommand = new RelayCommand(run);
    }

    public string Name { get; }
    public IRelayCommand RunCommand { get; }
}
