using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Services.Editing.Tones;
using DotCharter.Tone.UI.ViewModels.Shell;

namespace DotCharter.UI.ViewModels.Editor;

public partial class RenameableItem : ObservableObject
{
    [ObservableProperty] private int _count;
    [ObservableProperty] private string _newName = "";
    [ObservableProperty] private string _originalName = "";

    /// <summary>A tone key that is placed or declared but has no definition: it plays the
    /// game's default tone, and the export reports it. Always false for sections and phrases.</summary>
    [ObservableProperty] private bool _isUndefined;

    /// <summary>Why the last Apply was refused, printed under the row; cleared by the next keystroke.</summary>
    [ObservableProperty][NotifyPropertyChangedFor(nameof(HasError))] private string _error = "";

    public bool HasError => Error.Length > 0;

    partial void OnNewNameChanged(string value) => Error = "";
}

public partial class SongOverviewViewModel : ViewModelBase
{
    // Accidentals range: -7..+7 maps to Cb..C# (major) / Abm..A#m (minor)
    private static readonly string[] MajorKeyNames =
        ["Cb", "Gb", "Db", "Ab", "Eb", "Bb", "F", "C", "G", "D", "A", "E", "B", "F#", "C#"];

    private static readonly string[] MinorKeyNames =
        ["Ab", "Eb", "Bb", "F", "C", "G", "D", "A", "E", "B", "F#", "C#", "G#", "D#", "A#"];

    private readonly IHistoryService _historyService;

    private readonly IAppLogger _logger;
    private readonly ILocalizationService _localization;
    private readonly ProjectState _projectState;

    [ObservableProperty] private int _keyAccidentalsIndex = 7; // default C
    [ObservableProperty] private bool _keyIsMinor;

    private bool _suppressKeyEvents;

    public SongOverviewViewModel(ProjectState projectState, IHistoryService historyService, IAppLogger logger,
        ILocalizationService localization)
    {
        _projectState = projectState;
        _historyService = historyService;
        _logger = logger;
        _localization = localization;

        RebuildKeyOptions();
        _projectState.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ProjectState.ActiveProject) or nameof(ProjectState.ActiveArrangement))
                Refresh();
        };
        _historyService.HistoryChanged += Refresh;
        Refresh();
    }

    public ObservableCollection<RenameableItem> Tones { get; } = [];
    public ObservableCollection<RenameableItem> Sections { get; } = [];
    public ObservableCollection<RenameableItem> Phrases { get; } = [];

    public ObservableCollection<string> KeyOptions { get; } = [];

    public RelayCommand<RenameableItem> RenameToneCommand => new(item => ApplyRename(item, ItemKind.Tone));
    public RelayCommand<RenameableItem> RenameSectionCommand => new(item => ApplyRename(item, ItemKind.Section));
    public RelayCommand<RenameableItem> RenamePhraseCommand => new(item => ApplyRename(item, ItemKind.Phrase));
    public RelayCommand<RenameableItem> DeleteToneCommand => new(item => ApplyDelete(item, ItemKind.Tone));
    public RelayCommand<RenameableItem> DeleteSectionCommand => new(item => ApplyDelete(item, ItemKind.Section));
    public RelayCommand<RenameableItem> DeletePhraseCommand => new(item => ApplyDelete(item, ItemKind.Phrase));
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
        if (project == null)
        {
            Tones.Clear();
            Sections.Clear();
            Phrases.Clear();
            SetKeyFromModel(0, false);
            return;
        }

        SetKeyFromModel(project.Chart.KeyAccidentals, project.Chart.KeyIsMinor);

        var sectionMap = new Dictionary<string, int>(StringComparer.Ordinal);
        var phraseMap = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var arr in project.Arrangements)
        {
            foreach (var ev in arr.EventPoints)
            {
                if (!string.IsNullOrWhiteSpace(ev.Section))
                    sectionMap[ev.Section] = sectionMap.GetValueOrDefault(ev.Section) + 1;
                if (!string.IsNullOrWhiteSpace(ev.Phrase))
                    phraseMap[ev.Phrase] = phraseMap.GetValueOrDefault(ev.Phrase) + 1;
            }
        }

        ReplaceToneItems(Tones, project);
        ReplaceItems(Sections, sectionMap);
        ReplaceItems(Phrases, phraseMap);
    }

    /// <summary>
    ///     Publishes <paramref name="source" /> into <paramref name="target" />,
    ///     and raises NOTHING when the pane is already showing exactly that.
    ///     <para>
    ///         <see cref="RefreshCore" /> runs on EVERY committed edit
    ///         (<c>HistoryChanged</c>), so it must not <c>Clear()</c> all three
    ///         collections and re-<c>Add</c> a fresh <see cref="RenameableItem" />
    ///         per distinct name — one <c>CollectionChanged</c> per item, into
    ///         three bound <c>ItemsControl</c>s that drop and re-realize every
    ///         container for each. A note edit changes no tone, section or phrase
    ///         NAME and no count, so essentially every such rebuild would
    ///         publish the identical list.
    ///     </para>
    ///     <para>
    ///         The walk that produces the maps is kept — it is the cheap half
    ///         (arrangement metadata, not sounds) and it is what makes the
    ///         comparison possible. Rebuilding fresh instances is also what
    ///         discarded a half-typed rename: <see cref="RenameableItem.NewName" />
    ///         is two-way bound, and the row it was being typed into was replaced
    ///         on the next unrelated edit.
    ///     </para>
    /// </summary>
    private static void ReplaceItems(ObservableCollection<RenameableItem> target, Dictionary<string, int> source)
    {
        var ordered = source.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase).ToList();
        if (Matches(target, ordered)) return;

        target.Clear();
        foreach (var kvp in ordered)
            target.Add(new RenameableItem { OriginalName = kvp.Key, NewName = kvp.Key, Count = kvp.Value });
    }

    /// <summary>
    /// The project's tones — every key it DEFINES or references, once, case-folded the
    ///     way a rename matches. A defined tone nobody placed yet is listed too (count 0), and a
    ///     placed key with no definition carries <see cref="RenameableItem.IsUndefined" />. The
    ///     count is what it always was: the tone changes and starting tones that name it.
    /// </summary>
    private static void ReplaceToneItems(ObservableCollection<RenameableItem> target, ChartProject project)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var arr in project.Arrangements)
        {
            foreach (var tone in arr.ToneChanges)
            {
                if (string.IsNullOrWhiteSpace(tone.ToneName)) continue;
                counts[tone.ToneName.Trim()] = counts.GetValueOrDefault(tone.ToneName.Trim()) + 1;
            }

            if (!string.IsNullOrWhiteSpace(arr.StartingTone))
                counts[arr.StartingTone.Trim()] = counts.GetValueOrDefault(arr.StartingTone.Trim()) + 1;
        }

        var ordered = ToneKeyEdits.Keys(project)
            .OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase)
            .Select(k => (k.Key, Count: counts.GetValueOrDefault(k.Key), IsUndefined: !k.IsDefined))
            .ToList();
        if (target.Count == ordered.Count && ordered.Select((k, i) =>
                string.Equals(target[i].OriginalName, k.Key, StringComparison.Ordinal) &&
                target[i].Count == k.Count && target[i].IsUndefined == k.IsUndefined).All(same => same))
        {
            return;
        }

        target.Clear();
        foreach (var (key, count, isUndefined) in ordered)
            target.Add(new RenameableItem { OriginalName = key, NewName = key, Count = count, IsUndefined = isUndefined });
    }

    /// <summary>The displayed rows already ARE this list: same names in the same
    /// order with the same counts. <c>NewName</c> is deliberately not compared —
    /// it is the user's in-progress edit, not derived state.</summary>
    private static bool Matches(ObservableCollection<RenameableItem> target, List<KeyValuePair<string, int>> ordered)
    {
        if (target.Count != ordered.Count) return false;
        for (var i = 0; i < ordered.Count; i++)
        {
            if (!string.Equals(target[i].OriginalName, ordered[i].Key, StringComparison.Ordinal) ||
                target[i].Count != ordered[i].Value)
            {
                return false;
            }
        }

        return true;
    }

    private void ApplyRename(RenameableItem? item, ItemKind kind)
    {
        if (item == null) return;
        var project = _projectState.ActiveProject;
        if (project == null) return;
        var oldName = item.OriginalName;
        var newName = item.NewName.Trim();
        if (string.IsNullOrEmpty(newName) || oldName == newName) return;

        IChartAction? action = null;
        switch (kind)
        {
            case ItemKind.Tone:
            {
                // ONE verb renames the key in all five places it is stored, or
                // refuses with the reason printed under the row: never a merge.
                var plan = ToneKeyEdits.PlanRename(project, oldName, newName);
                if (!plan.IsReady)
                {
                    item.Error = RefusalText(plan);
                    if (item.HasError)
                        _logger.Info(AppLogCategory.Project, $"Tone rename '{oldName}' -> '{newName}' refused: {plan.Outcome}.");
                    return;
                }

                action = plan.Action;
                break;
            }
            case ItemKind.Section:
            {
                var events = project.Arrangements.SelectMany(a => a.EventPoints)
                    .Where(e => e.Section == oldName).ToList();
                action = new RenameStringFieldAction<EventPoint>(events, (e, v) => e.Section = v, oldName, newName);
                break;
            }
            case ItemKind.Phrase:
            {
                var events = project.Arrangements.SelectMany(a => a.EventPoints)
                    .Where(e => e.Phrase == oldName).ToList();
                action = new RenameStringFieldAction<EventPoint>(events, (e, v) => e.Phrase = v, oldName, newName);
                break;
            }
        }

        if (action == null || !action.HasEffect) return;
        _historyService.Do(action);
        _logger.Info(AppLogCategory.Project, $"Renamed {kind} '{oldName}' -> '{newName}'.");
        Refresh();
    }

    private void ApplyDelete(RenameableItem? item, ItemKind kind)
    {
        if (item == null) return;
        var project = _projectState.ActiveProject;
        if (project == null) return;
        var name = item.OriginalName;
        if (string.IsNullOrEmpty(name)) return;

        var composite = new List<IChartAction>();
        switch (kind)
        {
            case ItemKind.Tone:
                // The definition, the slots and the declared names go with the changes.
                if (ToneKeyEdits.BuildDelete(project, name) is { } delete) composite.Add(delete);
                break;

            case ItemKind.Section:
            case ItemKind.Phrase:
                foreach (var arr in project.Arrangements)
                {
                    // Remove event points whose *only* remaining payload is the deleted field; clear the field on the rest.
                    var removeAll = new List<EventPoint>();
                    var clearField = new List<EventPoint>();
                    foreach (var ev in arr.EventPoints)
                    {
                        var match = kind == ItemKind.Section ? ev.Section == name : ev.Phrase == name;
                        if (!match) continue;
                        var otherEmpty = kind == ItemKind.Section
                            ? string.IsNullOrEmpty(ev.Phrase)
                            : string.IsNullOrEmpty(ev.Section);
                        if (otherEmpty) removeAll.Add(ev);
                        else clearField.Add(ev);
                    }

                    if (removeAll.Count > 0)
                        composite.Add(new RemoveEventPointsAction(arr.EventPoints, removeAll));
                    if (clearField.Count > 0)
                    {
                        Action<EventPoint, string> setter = kind == ItemKind.Section
                            ? (e, v) => e.Section = v
                            : (e, v) => e.Phrase = v;
                        composite.Add(new RenameStringFieldAction<EventPoint>(
                            clearField, setter, name, string.Empty));
                    }
                }

                break;
        }

        var action = new CompositeAction(composite) { LabelKey = "History_Delete" };
        if (!action.HasEffect) return;
        _historyService.Do(action);
        _logger.Info(AppLogCategory.Project, $"Bulk-deleted {kind} '{name}'.");
        Refresh();
    }

    /// <summary>The printed reason a tone rename was refused; empty for "nothing to do".</summary>
    private string RefusalText(ToneKeyRenamePlan plan) => plan.Outcome switch
    {
        ToneKeyRenameOutcome.InvalidKey => _localization["Dialog_Tones_KeyInvalid"],
        ToneKeyRenameOutcome.Taken => string.Format(CultureInfo.CurrentCulture,
            _localization["Inspector_Tones_RenameTaken"], plan.NewKey),
        _ => ""
    };

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

    private enum ItemKind
    {
        Tone,
        Section,
        Phrase
    }
}
