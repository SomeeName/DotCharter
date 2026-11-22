using System.ComponentModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using DotCharter.Services.Editing.Tempo.RecordingAlignment;
using DotCharter.UI.Diagnostics;

namespace DotCharter.UI.ViewModels.Shell;

/// <summary>The editing verbs that can say why they are unavailable: most follow the selection, and
///     <see cref="OpenAlignToRecordingDialog" /> follows the chart (it needs notes to match against the recording).</summary>
public enum EditingVerb
{
    SelectLikePattern,
    SnapSelected,
    SnapAll,
    IncreaseLength,
    DecreaseLength,
    Refret,
    GenerateFhpForSelection,
    GenerateHandShape,
    GenerateArpeggio,
    SplitHandShape,
    GripsFromHeldNotes,
    HandShapesFromLetRing,
    AcceptSuggestedHandShapes,
    AcceptSuggestedArpeggios,
    OpenAlignToRecordingDialog,
}

/// <summary>
///     Why an editing verb cannot run right now: <c>null</c> when it can, else a localized
///     sentence in the wording of the services' "skipped:" log lines. ONE source for the menu
///     tooltip, the command palette's disabled-row subtitle and the status-bar message a
///     keybind leaves behind (<see cref="Run" />); the verbs' CanExecute is "no reason".
/// </summary>
public sealed class EditingAvailability : ObservableObject
{
    public const string KeyNoArrangement = "Disabled_NoArrangement";
    public const string KeyNoSoundSelected = "Disabled_NoSoundSelected";
    public const string KeyNoTemplateSelected = "Disabled_NoTemplateSelected";
    public const string KeyNoMovableSelection = "Disabled_NoMovableSelection";
    public const string KeyNoLengthTarget = "Disabled_NoLengthTarget";
    public const string KeyGenerateFhpSelection = "Menu_Generate_FhpSelection";
    public const string KeyGenerateFhpArrangement = "Menu_Generate_FhpArrangement";
    public const string KeyAlignNeedsNotes = "Dialog_AlignToRecording_NeedsNotes";

    private readonly ILocalizationService _localization;
    private readonly ISelectionService _selection;
    private readonly ProjectState _projectState;

    public EditingAvailability(ISelectionService selection, ProjectState projectState, ILocalizationService localization)
    {
        _selection = selection;
        _projectState = projectState;
        _localization = localization;
        Enabled = new EnabledFlags(this);
        selection.SelectionChanged += (_, _) => Refresh();
        projectState.PropertyChanged += OnProjectStateChanged;
    }

    /// <summary>The same verbs as a bool per name, for a menu item's IsEnabled: <c>EditingAvailability.Enabled[SnapSelected]</c>.</summary>
    public EnabledFlags Enabled { get; }

    /// <summary>The reason by verb name, for a XAML tooltip: <c>EditingAvailability[SnapSelected]</c>.</summary>
    public string? this[string verb]
    {
        get
        {
            // "Verb|Tip_Key": a menu item that already carries a static tip shows it while available.
            var parts = verb.Split('|');
            if (!Enum.TryParse<EditingVerb>(parts[0], out var parsed)) return null;
            return ReasonFor(parsed) ?? (parts.Length > 1 ? _localization[parts[1]] : null);
        }
    }

    /// <summary>Null when <paramref name="verb" /> can run, else why it cannot.</summary>
    public string? ReasonFor(EditingVerb verb) => KeyFor(verb) is { } key ? _localization[key] : null;

    /// <summary>
    ///     The reason for a view-model command, found by the method it was generated from
    ///     (the verb's name); null for any command that is not one of the editing verbs.
    /// </summary>
    public string? ReasonFor(ICommand command) =>
        Enum.TryParse<EditingVerb>(CommandIdResolver.Resolve(command, null, null), out var verb) ? ReasonFor(verb) : null;

    /// <summary>
    ///     For the keybind dispatcher: true when the verb behind <paramref name="label" /> (the
    ///     keybind's property name) is unavailable, after saying why on the status rail.
    /// </summary>
    public bool Blocks(string label)
    {
        if (!Enum.TryParse<EditingVerb>(label, out var verb) || ReasonFor(verb) is not { } reason) return false;
        _projectState.SetMessage(reason, StatusSeverity.Warning);
        return true;
    }

    public bool IsAvailable(EditingVerb verb) => KeyFor(verb) == null;

    /// <summary>
    ///     Tools ▸ Generate ▸ Fret hand positions is ONE row for both scopes: true when it will act
    ///     on the selected notes and chords, false when on the whole arrangement.
    /// </summary>
    public bool GenerateFhpActsOnSelection => NoteOrChord();

    /// <summary>That row's text, which names the scope it will act on.</summary>
    public string GenerateFhpLabel =>
        _localization[GenerateFhpActsOnSelection ? KeyGenerateFhpSelection : KeyGenerateFhpArrangement];

    /// <summary>Runs that row: the scope the label names decides which of the two generators runs.</summary>
    public void GenerateFhp(Action forSelection, Action forArrangement) =>
        (GenerateFhpActsOnSelection ? forSelection : forArrangement)();

    /// <summary>Re-reads the row's text; the menu calls it as the group opens, which also follows a language switch.</summary>
    public void RefreshGenerateFhpLabel() => OnPropertyChanged(nameof(GenerateFhpLabel));

    /// <summary>
    ///     Runs <paramref name="action" /> when the verb is available; otherwise says the reason on
    ///     the status rail (a keybind that did nothing must not do it silently).
    /// </summary>
    public void Run(EditingVerb verb, Action action)
    {
        if (ReasonFor(verb) is { } reason)
            _projectState.SetMessage(reason, StatusSeverity.Warning);
        else
            action();
    }

    private void OnProjectStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ProjectState.ActiveArrangement) or null or "") Refresh();
    }

    /// <summary>Re-announces every verb. A verb whose reason follows the CHART (its notes), not the selection,
    ///     has no event to hang on, so the menu that shows it calls this as it opens.</summary>
    public void Refresh()
    {
        OnPropertyChanged("Item[]");
        RefreshGenerateFhpLabel();
        Enabled.Refresh();
    }

    private string? KeyFor(EditingVerb verb) => verb switch
    {
        EditingVerb.SelectLikePattern => HasTemplate() ? null : KeyNoTemplateSelected,
        EditingVerb.SnapSelected or EditingVerb.GenerateFhpForSelection or EditingVerb.GenerateHandShape or EditingVerb.GenerateArpeggio
            or EditingVerb.SplitHandShape or EditingVerb.AcceptSuggestedHandShapes
            or EditingVerb.AcceptSuggestedArpeggios or EditingVerb.HandShapesFromLetRing => NoteOrChord() ? null : KeyNoSoundSelected,
        EditingVerb.IncreaseLength or EditingVerb.DecreaseLength => HasLengthTarget() ? null : KeyNoLengthTarget,
        EditingVerb.GripsFromHeldNotes => _selection.SelectedHandShapes.Count > 0 ? null : "Disabled_NoHandShapesSelected",
        EditingVerb.OpenAlignToRecordingDialog => _projectState.ActiveProject is not { } project ? KeyNoArrangement
            : RecordingAlignmentPlan.HasNotes(project) ? null : KeyAlignNeedsNotes,
        _ => _projectState.ActiveArrangement != null ? null : KeyNoArrangement,
    };

    private bool NoteOrChord() => _selection.SelectedNotes.Count + _selection.SelectedChords.Count > 0;

    private bool HasTemplate() =>
        NoteOrChord() || _selection.SelectedHandShapes.Count + _selection.SelectedAnchors.Count > 0;

    private bool HasLengthTarget() =>
        NoteOrChord() || _selection.SelectedHandShapes.Count > 0 || _selection.SelectedLyric != null;

    /// <summary>A bool indexer by verb name; raises "Item[]" when availability changes.</summary>
    public sealed class EnabledFlags(EditingAvailability owner) : ObservableObject
    {
        public bool this[string verb] => !Enum.TryParse<EditingVerb>(verb, out var parsed) || owner.IsAvailable(parsed);

        internal void Refresh() => OnPropertyChanged("Item[]");
    }
}
