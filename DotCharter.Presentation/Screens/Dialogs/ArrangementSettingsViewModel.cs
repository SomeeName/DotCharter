using System.Collections.ObjectModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Core.Domain.Project;
using DotCharter.Core.Utilities;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Dialogs;
using DotCharter.Services.Editing.Arrangements;
using DotCharter.Tone.Infrastructure.Configuration;

namespace DotCharter.Presentation.Screens.Dialogs;

/// <summary>
///     Arrangement ▸ Settings, on the active arrangement or a new one. One instance per
///     open; the vocabulary and the write are <see cref="ArrangementSettingsEdit" />, and a
///     Save is ONE undo node (a new arrangement's add-and-switch, or a recorded in-place edit).
/// </summary>
public sealed class ArrangementSettingsViewModel : DialogViewModel<bool>, IArrangementSettingsScreen,
    IDialogSetup<ArrangementSettingsRequest>, ICancelableDialog
{
    private readonly ChartEditDialogServices _edit;
    private readonly ObservableCollection<TuningStep> _tuningOffsets = [];
    private Arrangement? _target;
    private bool _isNew;
    private string _openedTuningType = "";
    private bool _tuningPresetTouched;
    private bool _seedingTuningPreset;

    private string _name = "";
    private ArrangementType _type = ArrangementType.Lead;
    private ArrangementSubtype _subtype = ArrangementSubtype.Main;
    private int _capo;
    private double _centOffset;
    private int _strings = 6;
    private string _tuningPreset = "E Standard";
    private IArrangementPathOption? _selectedPath;
    private bool _hasScrollSpeed;
    private double _scrollSpeed = ArrangementSettingsEdit.DefaultScrollSpeed;
    private bool _pickedBass;
    private bool _overrideDerivedFlags;

    public ArrangementSettingsViewModel(ChartEditDialogServices edit)
    {
        _edit = edit;
        PathOptions = [.. ArrangementSettingsEdit.Paths.Select(p => new PathOption(edit.Localization[p.LabelKey], p.RouteMask))];
        AuthoredFlags = [.. ArrangementSettingsEdit.AuthoredFlags.Select(f => new FlagItem(edit.Localization[f.LabelKey]))];
        DerivedFlags = [.. ArrangementSettingsEdit.DerivedFlags.Select(f => new FlagItem(edit.Localization[f.LabelKey]))];
        SaveCommand = new RelayCommand(Save);
        CancelCommand = new RelayCommand(() => TryCancel());
    }

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    public ArrangementType Type
    {
        get => _type;
        set
        {
            if (SetProperty(ref _type, value)) OnPropertyChanged(nameof(IsBass));
        }
    }

    public IReadOnlyList<ArrangementType> ArrangementTypes { get; } =
        [.. Enum.GetValues<ArrangementType>().Where(t => t != ArrangementType.Vocal)];

    public ArrangementSubtype Subtype
    {
        get => _subtype;
        set => SetProperty(ref _subtype, value);
    }

    public IReadOnlyList<ArrangementSubtype> ArrangementSubtypes { get; } = Enum.GetValues<ArrangementSubtype>();

    public int Capo
    {
        get => _capo;
        set => SetProperty(ref _capo, value);
    }

    public double CentOffset
    {
        get => _centOffset;
        set => SetProperty(ref _centOffset, value);
    }

    public int Strings
    {
        get => _strings;
        set
        {
            if (!SetProperty(ref _strings, value)) return;
            while (_tuningOffsets.Count < value) _tuningOffsets.Add(new TuningStep(0));
            while (_tuningOffsets.Count > value) _tuningOffsets.RemoveAt(_tuningOffsets.Count - 1);
            LabelTuningRow();
        }
    }

    public string TuningPreset
    {
        get => _tuningPreset;
        set
        {
            if (!SetProperty(ref _tuningPreset, value) || _seedingTuningPreset) return;
            _tuningPresetTouched = true;
            if (ArrangementSettingsEdit.PresetOffsets(value) is not { } offsets) return;
            for (var i = 0; i < _tuningOffsets.Count; i++)
                _tuningOffsets[i].Step = i < offsets.Length ? offsets[i] : 0;
        }
    }

    public IReadOnlyList<string> TuningPresets => ArrangementSettingsEdit.TuningPresets;

    public IReadOnlyList<ITuningStep> TuningOffsets => _tuningOffsets;

    public IReadOnlyList<IArrangementPathOption> PathOptions { get; }

    public IArrangementPathOption? SelectedPath
    {
        get => _selectedPath;
        set => SetProperty(ref _selectedPath, value);
    }

    public bool HasScrollSpeed
    {
        get => _hasScrollSpeed;
        set => SetProperty(ref _hasScrollSpeed, value);
    }

    public double ScrollSpeed
    {
        get => _scrollSpeed;
        set => SetProperty(ref _scrollSpeed, value);
    }

    public bool IsBass => Type == ArrangementType.Bass;

    public bool PickedBass
    {
        get => _pickedBass;
        set => SetProperty(ref _pickedBass, value);
    }

    public IReadOnlyList<IArrangementFlag> AuthoredFlags { get; }

    public bool OverrideDerivedFlags
    {
        get => _overrideDerivedFlags;
        set => SetProperty(ref _overrideDerivedFlags, value);
    }

    public IReadOnlyList<IArrangementFlag> DerivedFlags { get; }

    public ICommand SaveCommand { get; }

    public ICommand CancelCommand { get; }

    public void Configure(ArrangementSettingsRequest request)
    {
        _isNew = request.CreateNew;
        _target = _isNew
            ? ArrangementSettingsEdit.NewArrangement(_edit.State.ActiveProject?.Arrangements.Count ?? 0)
            : _edit.Editor.ActiveArrangement;
        if (_target == null) return;

        Name = _target.Name;
        Type = _isNew ? ArrangementType.Lead : _target.ArrangementType;
        Subtype = _isNew ? ArrangementSubtype.Main : _target.Subtype;
        Capo = _target.Capo;
        CentOffset = _target.CentOffset;
        _tuningOffsets.Clear();
        foreach (var t in _target.TuningPitches) _tuningOffsets.Add(new TuningStep(t.Step));
        Strings = _isNew ? 6 : _target.StringCount;
        while (_tuningOffsets.Count < Strings) _tuningOffsets.Add(new TuningStep(0));
        LabelTuningRow();

        // Seeding the picker must not rewrite the arrangement's own offsets, and the type
        // it ARRIVED with is remembered so an untouched picker writes it back (see Apply).
        _openedTuningType = _target.TuningType ?? "";
        _tuningPresetTouched = false;
        _seedingTuningPreset = true;
        TuningPreset = ArrangementSettingsEdit.TuningTypeToPreset(_openedTuningType);
        _seedingTuningPreset = false;
        LoadRocksmithProperties(_target);
    }

    public bool TryCancel()
    {
        Close(false);
        return true;
    }

    /// <summary>The flags show what the deriver would ship NOW, so pinning starts from the
    ///     derived answer (DLC Builder's non-destructive "Edit" seed).</summary>
    private void LoadRocksmithProperties(Arrangement arrangement)
    {
        HasScrollSpeed = arrangement.ScrollSpeed.HasValue;
        ScrollSpeed = Math.Clamp(arrangement.ScrollSpeed ?? ArrangementSettingsEdit.DefaultScrollSpeed,
            ArrangementSettingsEdit.MinScrollSpeed, ArrangementSettingsEdit.MaxScrollSpeed);
        PickedBass = arrangement.PickedBass;
        var route = ArrangementSettingsEdit.AuthoredRoute(arrangement);
        SelectedPath = PathOptions.Cast<PathOption>().FirstOrDefault(o => o.RouteMask == route) ?? PathOptions[0];
        OverrideDerivedFlags = arrangement.Properties.ManualOverride;

        var derived = ArrangementPropertiesDeriver.Derive(arrangement);
        for (var i = 0; i < DerivedFlags.Count; i++)
            DerivedFlags[i].IsSet = ArrangementSettingsEdit.DerivedFlags[i].Get(derived) != 0;
        for (var i = 0; i < AuthoredFlags.Count; i++)
            AuthoredFlags[i].IsSet = ArrangementSettingsEdit.AuthoredFlags[i].Get(arrangement.Properties) != 0;
    }

    private void Save()
    {
        if (_target == null)
        {
            Close(false);
            return;
        }

        var fields = new ArrangementSettingsFields(Name, Type, Subtype, Capo, CentOffset, Strings, TuningPreset,
            _tuningPresetTouched, _openedTuningType, [.. _tuningOffsets.Select(t => t.Step)], HasScrollSpeed,
            ScrollSpeed, PickedBass, (SelectedPath as PathOption)?.RouteMask ?? 0,
            [.. AuthoredFlags.Select(f => f.IsSet)], OverrideDerivedFlags, [.. DerivedFlags.Select(f => f.IsSet)]);

        var editor = _edit.Editor;
        if (_isNew)
        {
            ArrangementSettingsEdit.Apply(_target, fields);
            if (_edit.State.ActiveProject is { } project)
                _edit.History.Do(new AddArrangementAction(editor, project.Arrangements, _target));
            _edit.Logger.Info(AppLogCategory.Project, $"Arrangement '{_target.Name}' added.");
        }
        else
        {
            // Snapshot BEFORE the write; the cloner is the one sanctioned snapshot path. The
            // write has already happened when the node is recorded, so it is pushed, not done.
            var before = ChartProjectCloner.CloneArrangement(_target);
            ArrangementSettingsEdit.Apply(_target, fields);
            var after = ChartProjectCloner.CloneArrangement(_target);
            _edit.History.PushAction(
                new MutateArrangementSettingsAction(_target, before, after, editor.RepublishActiveArrangement));
            editor.RepublishActiveArrangement();
        }

        Close(true);
    }

    /// <summary>Names every box after its string, lowest first; the count decides the reference.</summary>
    private void LabelTuningRow()
    {
        var labels = TuningUtilities.GetStringLabels(_tuningOffsets.Count);
        for (var i = 0; i < _tuningOffsets.Count; i++)
        {
            var number = _tuningOffsets.Count - i;
            _tuningOffsets[i].Place(i, _tuningOffsets.Count, labels[i],
                _edit.Localization.Format("Dialog_TuningStringName", number, labels[i]));
        }
    }

    private sealed class TuningStep(int step) : ObservableObject, ITuningStep
    {
        private int _step = step;
        private int _index;
        private int _count;

        public int Step
        {
            get => _step;
            set
            {
                if (SetProperty(ref _step, value)) OnPropertyChanged(nameof(ResultingNote));
            }
        }

        public string StringLabel { get; private set; } = "";

        public string ResultingNote => TuningUtilities.GetResultingNote(_count, _index, _step);

        public string AccessibleName { get; private set; } = "";

        public void Place(int index, int count, string label, string accessibleName)
        {
            _index = index;
            _count = count;
            StringLabel = label;
            AccessibleName = accessibleName;
            OnPropertyChanged(nameof(StringLabel));
            OnPropertyChanged(nameof(ResultingNote));
            OnPropertyChanged(nameof(AccessibleName));
        }
    }

    private sealed record PathOption(string Label, int RouteMask) : IArrangementPathOption;

    private sealed class FlagItem(string label) : ObservableObject, IArrangementFlag
    {
        private bool _isSet;

        public string Label { get; } = label;

        public bool IsSet
        {
            get => _isSet;
            set => SetProperty(ref _isSet, value);
        }
    }
}
