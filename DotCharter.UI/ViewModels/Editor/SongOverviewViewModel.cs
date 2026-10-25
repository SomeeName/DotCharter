using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Tone.UI.ViewModels.Shell;

namespace DotCharter.UI.ViewModels.Editor;

public partial class SongOverviewViewModel : ViewModelBase
{
    // Accidentals range: -7..+7 maps to Cb..C# (major) / Abm..A#m (minor)
    private static readonly string[] MajorKeyNames =
        ["Cb", "Gb", "Db", "Ab", "Eb", "Bb", "F", "C", "G", "D", "A", "E", "B", "F#", "C#"];

    private static readonly string[] MinorKeyNames =
        ["Ab", "Eb", "Bb", "F", "C", "G", "D", "A", "E", "B", "F#", "C#", "G#", "D#", "A#"];

    private readonly IHistoryService _historyService;

    private readonly IAppLogger _logger;
    private readonly ProjectState _projectState;

    [ObservableProperty] private int _keyAccidentalsIndex = 7; // default C
    [ObservableProperty] private bool _keyIsMinor;

    private bool _suppressKeyEvents;

    /// <summary>The Song tab; a section's or tone's place jumps through the editor's deliberate seek.</summary>
    public SongOverviewViewModel(ProjectState projectState, IHistoryService historyService, IAppLogger logger,
        ILocalizationService localization, IChartEditorSurface editor)
    {
        _projectState = projectState;
        _historyService = historyService;
        _logger = logger;
        SectionsPanel = new MarkerGroupsViewModel(MarkerKind.Section, () => _projectState.ActiveProject, editor.ExecuteSeek,
            historyService, localization, logger);
        TonesPanel = new MarkerGroupsViewModel(MarkerKind.Tone, () => _projectState.ActiveProject, editor.ExecuteSeek,
            historyService, localization, logger);

        RebuildKeyOptions();
        _projectState.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ProjectState.ActiveProject) or nameof(ProjectState.ActiveArrangement))
                Refresh();
        };
        _historyService.HistoryChanged += Refresh;
        Refresh();
    }

    /// <summary>The Sections section: the project's sections, in the Phrases panel's shape.</summary>
    public MarkerGroupsViewModel SectionsPanel { get; }

    /// <summary>The Tones section: every tone the project defines or places.</summary>
    public MarkerGroupsViewModel TonesPanel { get; }

    public ObservableCollection<string> KeyOptions { get; } = [];

    public RelayCommand RefreshCommand => new(Refresh);

    /// <summary>
    ///     Re-reads the open project into this pane.
    ///     <para>
    ///         <b>A refresh READS the model — it may never write an edit back,
    ///         and the whole body is suppressed because of it.</b> Suppressing
    ///         only <see cref="SetKeyFromModel" /> would leave the
    ///         <see cref="RebuildKeyOptions" /> call below unguarded: rewriting
    ///         the bound <see cref="KeyOptions" /> makes the inspector's ComboBox
    ///         drop its selection to <c>-1</c>, the two-way binding wrote that
    ///         back, and index <c>-1</c> is read here as <c>-8</c> accidentals —
    ///         two steps outside the −7…+7 any of these formats can spell.
    ///         It landed as a REAL undo node, so merely LOADING a psarc reported
    ///         unsaved changes, and the crash-recovery snapshot that followed
    ///         made the next launch offer to restore a session nobody had edited.
    ///     </para>
    /// </summary>
    public void Refresh()
    {
        _suppressKeyEvents = true;
        try { RefreshCore(); }
        finally { _suppressKeyEvents = false; }
    }

    private void RefreshCore()
    {
        var project = _projectState.ActiveProject;
        RebuildKeyOptions();
        SectionsPanel.Refresh();
        TonesPanel.Refresh();
        if (project == null)
        {
            SetKeyFromModel(0, false);
            return;
        }

        SetKeyFromModel(project.Chart.KeyAccidentals, project.Chart.KeyIsMinor);
    }

    /// <summary>Number of entries in <see cref="KeyOptions" /> — 7 flats … 7 sharps.</summary>
    internal const int KeyOptionCount = 15;

    /// <summary>
    ///     True when <paramref name="index" /> names a real entry of
    ///     <see cref="KeyOptions" />. A ComboBox spells "nothing is selected" as
    ///     <c>-1</c> and raises it ITSELF whenever its items change underneath
    ///     it, so an index outside the list is never an authored key signature.
    /// </summary>
    internal static bool IsKeyIndex(int index) => index is >= 0 and < KeyOptionCount;

    partial void OnKeyAccidentalsIndexChanged(int value)
    {
        // The ComboBox's own "no selection" must not survive as state — left at
        // -1 the pane shows an empty key for a chart that has one. Put the
        // model's answer back instead of authoring the index as an edit.
        if (!IsKeyIndex(value))
        {
            RestoreKeyFromModel();
            return;
        }

        OnKeyChanged();
    }

    /// <summary>Re-seats the key controls from the open project, writing nothing back.</summary>
    private void RestoreKeyFromModel()
    {
        var project = _projectState.ActiveProject;
        SetKeyFromModel(project?.Chart.KeyAccidentals ?? 0, project?.Chart.KeyIsMinor ?? false);
    }

    partial void OnKeyIsMinorChanged(bool value)
    {
        _ = value;
        RebuildKeyOptions();
        OnKeyChanged();
    }

    private void OnKeyChanged()
    {
        if (_suppressKeyEvents) return;
        if (!IsKeyIndex(KeyAccidentalsIndex)) return;
        var project = _projectState.ActiveProject;
        if (project == null) return;

        var accidentals = KeyAccidentalsIndex - 7;
        var action = new SetKeySignatureAction(project.Chart, accidentals, KeyIsMinor);
        if (action.HasEffect)
        {
            _historyService.Do(action);
            _logger.Info(AppLogCategory.Project,
                $"Key signature set. Accidentals={accidentals}, minor={KeyIsMinor}.");
        }
    }

    private void SetKeyFromModel(int accidentals, bool isMinor)
    {
        // Save/restore rather than clear: Refresh() suppresses around its WHOLE
        // body and calls this in the middle of it, so a bare `= false` here
        // would re-open the rest of the refresh to writing edits back.
        var wasSuppressed = _suppressKeyEvents;
        _suppressKeyEvents = true;
        try
        {
            var clamped = Math.Clamp(accidentals, -7, 7);
            KeyIsMinor = isMinor;
            RebuildKeyOptions();
            KeyAccidentalsIndex = clamped + 7;
        }
        finally
        {
            _suppressKeyEvents = wasSuppressed;
        }
    }

    private void RebuildKeyOptions()
    {
        var names = KeyIsMinor ? MinorKeyNames : MajorKeyNames;
        if (KeyOptions.Count == names.Length)
        {
            // RefreshCore calls this on EVERY committed edit, and the answer
            // only ever changes when major↔minor flips — so the assignment loop
            // below raised fifteen `Replace` notifications per note edit into a
            // bound ComboBox for an identical list. Rewriting KeyOptions is also
            // what drops that ComboBox's selection to -1 (see Refresh's remarks);
            // not touching it at all is the safest form of not doing that.
            if (KeyOptions.SequenceEqual(names, StringComparer.Ordinal)) return;

            for (var i = 0; i < names.Length; i++)
                KeyOptions[i] = names[i];
            return;
        }

        KeyOptions.Clear();
        foreach (var n in names) KeyOptions.Add(n);
    }
}
