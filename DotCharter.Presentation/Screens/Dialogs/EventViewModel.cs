using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Core.Domain.Events;
using DotCharter.Core.Domain.Project;
using DotCharter.Core.Utilities;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Dialogs;
using DotCharter.Services.Editing.Events;
using DotCharter.Services.State;
using DotCharter.Tone.Infrastructure.Configuration;

namespace DotCharter.Presentation.Screens.Dialogs;

/// <summary>
///     One event marker: section, status, phrase and solo, tone, RS event codes and
///     target arrangements. One instance per open; the save is ONE undo step built by
///     <see cref="EventMarkerEdit" />. Structure is placed in runs, so opening on empty
///     ground repeats the last marker's answer (<see cref="IEventEditingService" />).
/// </summary>
public sealed class EventViewModel : DialogViewModel<bool>, IEventScreen, IDialogSetup<EventRequest>, ICancelableDialog
{
    private static readonly string[] StatusKeys =
        ["SectionStatus_None", "SectionStatus_Todo", "SectionStatus_Draft", "SectionStatus_Review", "SectionStatus_Done"];

    private readonly ChartEditDialogServices _edit;
    private readonly IEventEditingService _events;
    private readonly List<string> _foreignEventCodes = [];
    private IReadOnlyList<SectionOption> _sectionOptions = [];
    private IReadOnlyList<string> _toneOptions = [];
    private IReadOnlyList<EventCodeToggle> _eventToggles = [];
    private IReadOnlyList<ArrangementTarget> _arrangements = [];
    private double _eventTimeMs;
    private SectionOption? _selectedSection;
    private string _sectionName = "";
    private int _sectionStatusIndex;
    private string _phraseName = "";
    private bool _soloPhrase;
    private string _toneName = "";

    /// <summary>True while the phrase is whatever the section picker put there; the user typing clears it.</summary>
    private bool _phraseIsAutoFilled;

    private bool _suppressPhraseTracking;

    public EventViewModel(ChartEditDialogServices edit, IEventEditingService events)
    {
        _edit = edit;
        _events = events;
        SectionStatusOptions = [.. StatusKeys.Select(k => edit.Localization[k])];
        SaveCommand = new RelayCommand(Save);
        CancelCommand = new RelayCommand(() => TryCancel());
    }

    public IReadOnlyList<IEventSectionOption> SectionOptions => _sectionOptions;

    public IEventSectionOption? SelectedSection
    {
        get => _selectedSection;
        set
        {
            if (!SetProperty(ref _selectedSection, value as SectionOption)) return;
            SectionName = _selectedSection?.Key ?? "";
            // Fills only an empty or auto-filled phrase: the ~7 % of sections whose
            // phrase is named differently are the charter's and never clobbered.
            if (!_phraseIsAutoFilled && !string.IsNullOrWhiteSpace(PhraseName)) return;
            _suppressPhraseTracking = true;
            PhraseName = SectionName;
            _suppressPhraseTracking = false;
            _phraseIsAutoFilled = !string.IsNullOrEmpty(SectionName);
        }
    }

    public string SectionName
    {
        get => _sectionName;
        private set
        {
            if (SetProperty(ref _sectionName, value)) OnPropertyChanged(nameof(SectionNameIsUnknown));
        }
    }

    public bool SectionNameIsUnknown => !string.IsNullOrWhiteSpace(SectionName) && !RsSectionCatalog.IsValid(SectionName);

    public IReadOnlyList<string> SectionStatusOptions { get; }

    public int SectionStatusIndex
    {
        get => _sectionStatusIndex;
        set => SetProperty(ref _sectionStatusIndex, value);
    }

    public string PhraseName
    {
        get => _phraseName;
        set
        {
            if (!SetProperty(ref _phraseName, value)) return;
            if (!_suppressPhraseTracking) _phraseIsAutoFilled = false;
        }
    }

    public bool SoloPhrase
    {
        get => _soloPhrase;
        set => SetProperty(ref _soloPhrase, value);
    }

    public IReadOnlyList<string> ToneOptions => _toneOptions;

    public string ToneName
    {
        get => _toneName;
        set
        {
            if (SetProperty(ref _toneName, value)) OnPropertyChanged(nameof(ToneHasNoBody));
        }
    }

    public bool ToneHasNoBody =>
        !string.IsNullOrWhiteSpace(ToneName) && !ChartToneNames.HasBody(_edit.State.ActiveProject, ToneName);

    public IReadOnlyList<IEventCodeToggle> EventToggles => _eventToggles;

    public IReadOnlyList<IEventArrangementTarget> Arrangements => _arrangements;

    public bool HasMultipleArrangements => _arrangements.Count > 1;

    public ICommand SaveCommand { get; }

    public ICommand CancelCommand { get; }

    public void Configure(EventRequest request)
    {
        var editor = _edit.Editor;
        var beats = editor.Beats;
        // Markers are authored on the grid; tone changes from imports can sit off
        // it, so the tone is looked up at the requested time first (60 ms window).
        _eventTimeMs = editor.SnapToGrid(request.TimeMs).GetPositionMs(beats);
        // The CLICKED arrangement, not the active one: a stacked panel's marker
        // must not be seeded from the active arrangement's.
        var lookup = request.Target ?? editor.ActiveArrangement;
        var existingEvent = lookup == null ? null : EventMarkerEdit.EventAt(lookup, _eventTimeMs, beats);
        var tones = lookup?.ToneChanges ?? [];
        var existingTone = EventMarkerEdit.NearestTone(tones, request.TimeMs, beats)
                           ?? EventMarkerEdit.NearestTone(tones, _eventTimeMs, beats);

        var isNew = existingEvent == null;
        BuildSectionOptions(existingEvent?.Section ?? (isNew ? _events.LastSection : ""));
        _toneOptions = [.. ChartToneNames.Offerable(_edit.State.ActiveProject, lookup)];
        OnPropertyChanged(nameof(ToneOptions));
        BuildEventToggles(existingEvent?.Events ?? (isNew ? _events.LastEvents : null));
        PhraseName = existingEvent?.Phrase ?? (isNew ? _events.LastPhrase : "");
        // A phrase that already matches its section was following it, so it keeps following.
        _phraseIsAutoFilled = !string.IsNullOrWhiteSpace(SectionName) && PhraseFollowsSection();
        ToneName = existingTone?.ToneName ?? "";
        SoloPhrase = !string.IsNullOrWhiteSpace(PhraseName) && lookup != null &&
                     lookup.PhraseInfos.TryGetValue(PhraseName, out var info) && info.Solo;
        SectionStatusIndex = (int)(existingEvent?.Status ?? SectionStatus.None);

        // An off-grid tone keeps the marker on ITS position, so a save does not move it.
        if (existingTone != null) _eventTimeMs = existingTone.Position.GetPositionMs(beats);

        BuildArrangements(lookup, isNew);
        _edit.Logger.Info(AppLogCategory.Events,
            $"Event dialog opened at requestedMs={request.TimeMs:0.###}, snappedMs={_eventTimeMs:0.###}, " +
            $"section='{SectionName}', phrase='{PhraseName}', tone='{ToneName}'.");
    }

    public bool TryCancel()
    {
        if (!IsClosed) _edit.Logger.Info(AppLogCategory.Events, $"Event dialog canceled at snappedMs={_eventTimeMs:0.###}.");
        Close(false);
        return true;
    }

    /// <summary>The phrase says the section again — its stored key ("chorus") or its label ("Pre Chorus").</summary>
    internal bool PhraseFollowsSection() =>
        string.Equals(PhraseName, SectionName, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(PhraseName, RsSectionCatalog.DisplayName(SectionName), StringComparison.OrdinalIgnoreCase);

    private void BuildSectionOptions(string stored)
    {
        var options = new List<SectionOption> { new("", _edit.Localization["Dialog_Section_None"]) };
        options.AddRange(RsSectionCatalog.Names.Select(n => new SectionOption(n, RsSectionCatalog.DisplayName(n))));

        // An off-list name an import put here is shown, never rewritten on open.
        var trimmed = stored.Trim();
        if (trimmed.Length > 0 && !RsSectionCatalog.IsValid(trimmed))
            options.Add(new SectionOption(trimmed, _edit.Localization.Format("Dialog_Section_Unrecognized", trimmed)));

        _sectionOptions = options;
        OnPropertyChanged(nameof(SectionOptions));
        SelectedSection = options.FirstOrDefault(o => string.Equals(o.Key, trimmed, StringComparison.OrdinalIgnoreCase))
                          ?? options[0];
    }

    private void BuildEventToggles(string? stored)
    {
        var present = RsEventCatalog.Parse(stored);
        _eventToggles = [.. RsEventCatalog.Events.Select(e => new EventCodeToggle(e.Code, e.DisplayName)
        {
            IsSelected = present.Contains(e.Code, StringComparer.Ordinal)
        })];
        OnPropertyChanged(nameof(EventToggles));

        // Codes outside the catalogue belong to whoever authored the chart: kept verbatim, never shown.
        _foreignEventCodes.Clear();
        _foreignEventCodes.AddRange(present.Where(code => !RsEventCatalog.IsKnown(code)));
    }

    private void BuildArrangements(Arrangement? clicked, bool isNew)
    {
        // The set is remembered by NAME for a new marker; the clicked arrangement is always ticked.
        var remembered = isNew ? _events.LastArrangementNames.ToHashSet(StringComparer.Ordinal) : [];
        _arrangements = [.. (_edit.State.ActiveProject?.Arrangements ?? []).Select(a =>
        {
            var isActive = ReferenceEquals(a, clicked);
            return new ArrangementTarget(a, isActive, isActive || remembered.Contains(a.Name));
        })];
        OnPropertyChanged(nameof(Arrangements));
        OnPropertyChanged(nameof(HasMultipleArrangements));
    }

    private string ComposeEvents() =>
        RsEventCatalog.Join([.. _eventToggles.Where(t => t.IsSelected).Select(t => t.Code), .. _foreignEventCodes]);

    private void Save()
    {
        var editor = _edit.Editor;
        var position = TimelineMath.PositionFromMs(_eventTimeMs, editor.Beats);
        var targets = _arrangements.Where(a => a.IsSelected || a.IsActive).Select(a => a.Arrangement).ToList();
        if (targets.Count == 0 && editor.ActiveArrangement != null) targets.Add(editor.ActiveArrangement);

        var status = (SectionStatus)Math.Clamp(SectionStatusIndex, 0, (int)SectionStatus.Done);
        var fields = new EventMarkerFields(SectionName, PhraseName, status, ComposeEvents(), SoloPhrase, ToneName);
        var actions = new List<IChartAction>();
        foreach (var arrangement in targets)
            EventMarkerEdit.AppendActions(arrangement, position, _eventTimeMs, editor.Beats, fields, actions);

        // Remembered whether or not anything was written: re-saving an unchanged marker is still a choice.
        _events.RememberLastMarker(SectionName, PhraseName);
        _events.RememberLastTargets(fields.Events, targets.Select(t => t.Name));

        var saved = actions.Count > 0 && _edit.History.Do(new CompositeAction(actions) { LabelKey = "History_EditEvents" });
        if (saved)
        {
            editor.RequestRedraw();
            _edit.Logger.Info(AppLogCategory.Events,
                $"Event dialog saved at position={position} ({_eventTimeMs:0.###} ms). Section='{SectionName}', " +
                $"phrase='{PhraseName}', tone='{ToneName}', actions={actions.Count}, arrangements={targets.Count}.");
        }

        Close(true);
    }

    private sealed record SectionOption(string Key, string Display) : IEventSectionOption;

    private sealed class EventCodeToggle(string code, string display) : ObservableObject, IEventCodeToggle
    {
        private bool _isSelected;

        public string Code { get; } = code;

        public string Display { get; } = display;

        public bool IsSelected
        {
            get => _isSelected;
            set => SetProperty(ref _isSelected, value);
        }
    }

    private sealed class ArrangementTarget(Arrangement arrangement, bool isActive, bool isSelected)
        : ObservableObject, IEventArrangementTarget
    {
        private bool _isSelected = isSelected;

        public Arrangement Arrangement { get; } = arrangement;

        public string DisplayName => string.IsNullOrWhiteSpace(Arrangement.Name) ? "(unnamed)" : Arrangement.Name;

        public bool IsActive { get; } = isActive;

        public bool IsSelected
        {
            get => _isSelected;
            set => SetProperty(ref _isSelected, value);
        }
    }
}
