// InspectorViewModel: the CHORD side - SyncChordProperties, the chord template resolve, the per-string technique handlers behind the mask, and the chord-level flags.

using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Services.Export.Rocksmith;
using DotCharter.UI.ViewModels.Editor.Inspector;
using DotCharter.UI.ViewModels.Panels;

namespace DotCharter.UI.ViewModels.Editor;

public partial class InspectorViewModel
{
    /// <summary>The masked strings slide by different amounts (or some hold),
    /// so the slide field is blank and <see cref="ChordSlideSummary" /> spells
    /// each string out.</summary>
    [ObservableProperty] private bool _chordSlideIsMixed;

    [ObservableProperty] private string _chordSlideSummary = string.Empty;

    /// <summary>Why the last typed slide target was not applied; null once
    /// the chord is re-read.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasChordSlideRefusal))]
    private string? _chordSlideRefusal;

    public bool HasChordSlideRefusal => ChordSlideRefusal != null;

    /// <summary>Whether the ONE selected chord ships as Rocksmith's repeat box and,
    /// when it does not, why. Empty for no chord or several.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasChordRepeatVerdict))]
    private string _chordRepeatVerdict = string.Empty;

    public bool HasChordRepeatVerdict => ChordRepeatVerdict.Length > 0;

    /// <summary>
    ///     The repeat verdict of <paramref name="chord" /> in the level these lists
    ///     describe — <see cref="RepeatChords.Explain" />, the function the export's own
    ///     set is derived from — or null when the chord is not in <paramref name="chords" />.
    /// </summary>
    internal static RepeatChordVerdict? ExplainRepeat(IEnumerable<ChordTemplate> templates, IList<GuitarNote> notes,
        IList<Chord> chords, IList<HandShape> handShapes, IList<BeatMarker> beats, Chord chord) =>
        RepeatChords.Explain(templates, notes, chords, handShapes, beats).TryGetValue(chord, out var verdict)
            ? verdict
            : null;

    private void SyncChordRepeatVerdict(IReadOnlyList<Chord> chords)
    {
        var verdict = chords.Count == 1
            ? ExplainRepeat(_chartEditor.ActiveArrangement?.ChordTemplates ?? [], _chartEditor.PlacedNotes,
                _chartEditor.PlacedChords, _chartEditor.HandShapes, _chartEditor.Beats, chords[0])
            : null;
        ChordRepeatVerdict = verdict is { } v
            ? RepeatVerdictText.Format(v, NoteStringCount, NoteTuningNames, _localization)
            : string.Empty;
    }

    private void SyncChordProperties()
    {
        OnPropertyChanged(nameof(HasChordSelection));
        OnPropertyChanged(nameof(HasSingleNoteSelection));
        OnPropertyChanged(nameof(IsMultiSelect));
        OnPropertyChanged(nameof(SelectionSummary));
        // See SyncNoteProperties — a Ctrl+click that appends a second chord
        // leaves the scalar SelectedChord alone, so nothing else invalidates the
        // selection-issue memo.
        NotifySelectionIssuesChanged();
        NotifySelectionKindChanged();
        if (SelectedChord == null)
        {
            ChordRepeatVerdict = string.Empty;
            ChordBendStrings = [];
            BendTargetChordNote = null;
            ChordBendEditor = null;
            _bendTargetChord = null;
            return;
        }

        _isSyncing = true;

        var chords = SelectedChords.Count > 0 ? SelectedChords : (IReadOnlyList<Chord>)[SelectedChord];
        var agreed = ChordFlagAgreement.Over(chords);
        ChordAccent = agreed.Accent;
        ChordSplitIntoNotes = agreed.SplitIntoNotes;
        ChordForceNoNotes = agreed.ForceNoNotes;
        ChordPassOtherNotes = agreed.PassOtherNotes;
        ChordIgnore = agreed.Ignore;
        ChordStrum = (int)SelectedChord.Strum;

        // Which STRING the user is authoring, preserved across the rebuild.
        // Every committed chord edit lands here (OnHistoryChanged ->
        // SyncChordProperties) and Chord.CopyFrom REPLACES every ChordNote with
        // a clone, so the old target is an orphan and the list is fresh — but
        // resetting to FirstOrDefault() snapped the bend/technique target back
        // to the LOWEST string after every single commit. Authoring a bend on
        // any other string was therefore impossible past its first point: the
        // graph blanked and the next point was written to string 1.
        // The arrangement's strings, so the chord-bend picker names them the way
        // the Placement card and the lane gutter do.
        NoteStringCount = _chartEditor.ActiveArrangement?.Tuning.Length ?? 6;
        NoteTuningNames = _chartEditor.ActiveArrangement?.Tuning;
        SyncChordRepeatVerdict(chords);
        FollowSelectionBends(SelectedChord.ChordNotes.Any(n => n.BendValues.Count > 0));

        var previousTargetString = ReferenceEquals(_bendTargetChord, SelectedChord)
            ? BendTargetChordNote?.StringIndex
            : null;
        ChordBendStrings = new ObservableCollection<ChordNote>(
            SelectedChord.ChordNotes.OrderBy(n => n.StringIndex));
        BendTargetChordNote = PreserveBendTarget(ChordBendStrings, previousTargetString);
        _bendTargetChord = SelectedChord;

        RebuildChordBendEditor();
        SyncChordNoteTechniques();
        NotifyMaskScopeChanged();

        _isSyncing = false;
    }

    /// <summary>The chord whose strings <see cref="ChordBendStrings" /> was
    /// last built from — the reference test behind the target's survival. A
    /// mutation keeps the Chord instance and swaps its ChordNotes, so identity
    /// here means "the same chord, re-read", and a DIFFERENT chord is a fresh
    /// selection that legitimately starts at its lowest string.</summary>
    private Chord? _bendTargetChord;

    /// <summary>
    ///     The per-string target to keep after the strings were re-read: the one
    ///     on the same STRING as before, else the lowest string.
    ///     <para>
    ///         Pure so it is testable without the inspector's twelve services —
    ///         the same reason <see cref="Inspector.HandShapeEditorViewModel.CountRebindableChords" /> and
    ///         <see cref="MaskedChordNotes" /> are.
    ///     </para>
    /// </summary>
    internal static ChordNote? PreserveBendTarget(IReadOnlyList<ChordNote> strings, int? previousStringIndex)
    {
        if (strings.Count == 0) return null;
        if (previousStringIndex is { } wanted)
        {
            foreach (var note in strings)
            {
                if (note.StringIndex == wanted)
                    return note;
            }
        }

        return strings[0];
    }

    partial void OnChordNoteMuteChanged(int value)
    {
        ApplyChordNoteMutation(cn => cn.Mute = (MuteType)value);
    }

    partial void OnChordNoteHarmonicChanged(int value)
    {
        ApplyChordNoteMutation(cn => cn.Harmonic = (HarmonicType)value);
    }

    partial void OnChordNoteHopoChanged(int value)
    {
        ApplyChordNoteMutation(cn => cn.Hopo = (HopoType)value);
    }

    partial void OnChordNoteBassPickingChanged(int value)
    {
        ApplyChordNoteMutation(cn => cn.BassPicking = (BassPickingType)value);
    }

    partial void OnChordNoteVibratoChanged(bool? value)
    {
        if (value is null) return;
        var v = value.Value;
        ApplyChordNoteMutation(cn => SetVibrato(cn, v));
    }

    partial void OnChordNoteLinkNextChanged(bool? value)
    {
        if (value is null) return;
        ApplyChordNoteMutation(cn => cn.LinkNext = value.Value);
    }

    partial void OnChordNoteTremoloChanged(bool? value)
    {
        if (value is null) return;
        ApplyChordNoteMutation(cn => cn.Tremolo = value.Value);
    }

    partial void OnChordNoteUnpitchedSlideChanged(bool? value)
    {
        if (value is null) return;
        ApplyChordNoteMutation(cn => cn.UnpitchedSlide = value.Value);
    }

    /// <summary>
    ///     The typed slide target is the LOWEST-FRETTED masked string's, and every
    ///     other masked string slides by the same number of frets (EOF and
    ///     rscharter, see <see cref="ChordSlideScope" />). It is not written
    ///     to every string as one absolute fret. Every selected chord is
    ///     planned first, so a target that would push any string off the
    ///     fretboard changes nothing and says why.
    /// </summary>
    partial void OnChordNoteSlideToChanged(int? value)
    {
        if (_isSyncing) return;
        var mask = _projectState.StringEditMask;
        foreach (var chord in ChordEditTargets())
        {
            if (ChordSlideScope.Plan(MaskedChordNotes(chord.ChordNotes, mask), value).Refusal is { } offBoard)
            {
                RefuseChordSlide(offBoard);
                return;
            }
        }

        ApplyChordMutation(c => ChordSlideScope.Plan(MaskedChordNotes(c.ChordNotes, mask), value).ApplyTo(c.ChordNotes));
    }

    /// <summary>What the slide field shows: the agreed target, or "the strings
    /// differ" with each masked string's slide spelled out.</summary>
    private void SyncChordSlide()
    {
        var wasSyncing = _isSyncing;
        _isSyncing = true;
        var readout = ChordSlideScope.Read(MaskedChordNotes(ChordBendStrings, _projectState.StringEditMask));
        ChordNoteSlideTo = readout.Target;
        ChordSlideIsMixed = readout.IsMixed;
        ChordSlideSummary = readout.IsMixed ? ChordSlideSummaryText(readout.Strings) : string.Empty;
        ChordSlideRefusal = null;
        _isSyncing = wasSyncing;
    }

    private string ChordSlideSummaryText(IReadOnlyList<ChordNote> strings)
    {
        var parts = strings.Select(n => n.SlideTo is { } end
            ? _localization.Format("Inspector_ChordSlide_StringSlides", StringName(n.StringIndex), n.Fret, end)
            : _localization.Format("Inspector_ChordSlide_StringHolds", StringName(n.StringIndex), n.Fret));
        return _localization.Format("Inspector_ChordSlide_Mixed", string.Join(" · ", parts));
    }

    /// <summary>A string named the way the chord-bend picker and the lane
    /// gutter name it ("A (5)").</summary>
    private string StringName(int stringIndex) =>
        GuitarStringLabel.Format(stringIndex, NoteStringCount, NoteTuningNames, _localization["Inspector_StringLabelFmt"]);

    /// <summary>
    ///     Puts the field back on what the chord holds, then says why the typed
    ///     target did not stick.
    ///     <para>
    ///         POSTED, because the binding that wrote the refused number does not
    ///         take a value back while its own write is on the stack: putting the
    ///         property back inline (or re-raising its change afterwards) left
    ///         the spinner showing the refused number — the headless case in
    ///         <c>SoundTabFitUiPerfSmokeTests</c> caught both.
    ///     </para>
    /// </summary>
    private void RefuseChordSlide(ChordSlideOffBoard offBoard)
    {
        var reason = _localization.Format("Inspector_ChordSlide_OffBoard",
            StringName(offBoard.StringIndex), offBoard.Fret, offBoard.WouldEndOn, ChordSlideScope.MaxFret);
        Dispatcher.UIThread.Post(() =>
        {
            SyncChordSlide();
            ChordSlideRefusal = reason;
        });
    }

    [RelayCommand]
    private void ApplyTechniquesToAllStrings()
    {
        if (_isSyncing || BendTargetChordNote == null || SelectedChord == null) return;
        var src = BendTargetChordNote;
        var mute = src.Mute;
        var harmonic = src.Harmonic;
        var hopo = src.Hopo;
        var picking = src.BassPicking;
        var vibrato = src.Vibrato;
        var vibratoStrength = src.VibratoStrength;
        var linkNext = src.LinkNext;
        var tremolo = src.Tremolo;
        var unpitchedSlide = src.UnpitchedSlide;
        // The slide is copied as the source string's OFFSET, never its end fret:
        // a 5→6 copied onto an 8 is 8→9, not a slide down to 6.
        var slideOffset = ChordSlideScope.Offset(src);
        foreach (var chord in ChordEditTargets())
        {
            if (CopiedSlide(chord.ChordNotes, slideOffset).Refusal is { } offBoard)
            {
                RefuseChordSlide(offBoard);
                return;
            }
        }

        ApplyChordMutation(c =>
        {
            foreach (var cn in c.ChordNotes)
            {
                cn.Mute = mute;
                cn.Harmonic = harmonic;
                cn.Hopo = hopo;
                cn.BassPicking = picking;
                SetVibrato(cn, vibrato);
                // Only when it is ON: the source string may itself be an import
                // left at Vibrato=false with a depth, and broadcasting that pair
                // would seed the very state SetVibrato exists to prevent.
                if (vibrato) cn.VibratoStrength = vibratoStrength;
                cn.LinkNext = linkNext;
                cn.Tremolo = tremolo;
                cn.UnpitchedSlide = unpitchedSlide;
            }

            CopiedSlide(c.ChordNotes, slideOffset).ApplyTo(c.ChordNotes);
        });
    }

    private static ChordSlidePlan CopiedSlide(IReadOnlyList<ChordNote> notes, int? offset)
    {
        var strings = notes.OrderBy(n => n.StringIndex).ToArray();
        return offset is { } frets
            ? ChordSlideScope.PlanOffset(ChordSlideScope.Movers(strings), frets)
            : ChordSlideScope.Plan(strings, null);
    }

    /// <summary>
    /// The chords a technique or playback edit reaches past the selection: the
    ///     "Apply to" ladder's lit rung, resolved by the chord panel on the scope its ladder
    ///     and preview read. Null — the selection, exactly as before — when the rung IS the
    ///     selection, or when no ladder sits over the edit (a note or a multi-selection
    ///     without a chord in it: an edit is never broader than the bar above it).
    /// </summary>
    private ChordTechniqueTargets? LadderTargets()
    {
        if (!Sections.HasChordScope) return null;
        return ChordTemplateEditor.TechniqueTargets() is { IsSelection: false } targets ? targets : null;
    }

    /// <summary>What a chord edit will write: the ladder's chords, or the selection.</summary>
    private IReadOnlyList<Chord> ChordEditTargets() => LadderTargets()?.Chords ?? SelectedChords;

    /// <summary>One history node over every level the rung covers, then the status rail
    /// says how far it went ("Technique applied to 14 chords on 3 shapes").</summary>
    private void ApplyChordMutationOnLadder(ChordTechniqueTargets ladder, Action<Chord> mutation)
    {
        var action = ChordTechniqueEdit.Build(_chartEditor.ActiveArrangement?.Levels ?? [], _chartEditor.PlacedChords,
            ladder.Chords, mutation, _highwayEditingService.GetLinkNextUpdater(),
            _highwayEditingService.GetLevelSpacingUpdater);
        if (action == null) return;
        _historyService.Do(action);
        _projectState.SetMessage(ChordTemplateEditor.Ladder.DescribeTechnique(ladder.Chords));
    }

    /// <summary>The Sound tab tells the chord panel which edits its pinned ladder sits over.</summary>
    private void OnSectionsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SoundSectionsViewModel.ApplyFamily))
            ChordTemplateEditor.ApplyFamily = Sections.ApplyFamily;
    }

    /// <summary>
    ///     Apply a per-string edit to the strings the MASK targets.
    ///     <para>
    ///         <c>StringEditMask.Includes</c> answers "may I edit this string",
    ///         and an EMPTY mask says yes to everything — which is exactly the
    ///         old fall-back-to-all-strings behaviour, so nothing changes until
    ///         the user sets a mask. What changes is that setting one on the
    ///         lane headers (<c>Ctrl+1..9</c>) now reaches these controls too.
    ///     </para>
    /// </summary>
    private void ApplyChordNoteMutation(Action<ChordNote> mutation)
    {
        if (_isSyncing) return;
        var mask = _projectState.StringEditMask;
        ApplyChordMutation(c =>
        {
            foreach (var cn in MaskedChordNotes(c.ChordNotes, mask))
                mutation(cn);
        });
    }

    /// <summary>
    /// The panel resolves the shape itself: a chord naming a real
    ///     template loads it by ID — never by list position — and an untemplated
    ///     chord, a hand shape or no selection resolve as selecting them did. A
    ///     fabricated <c>new ChordTemplate { Name = "Custom" }</c> here carried no
    ///     frets, so every technique edit turned the Shape card into "Custom" and
    ///     the ladder's selection into "selected (0)".
    /// </summary>
    private void SyncChordTemplate() => ChordTemplateEditor.ReloadForSelectedChord();

    partial void OnChordStrumChanged(int value) => ApplyChordMutation(c => c.Strum = (PickDirectionType)value);

    partial void OnChordAccentChanged(bool? value)
    {
        if (value is null) return;
        ApplyChordMutation(c => c.Accent = value.Value);
    }

    partial void OnChordSplitIntoNotesChanged(bool? value)
    {
        if (value is null) return;
        ApplyChordMutation(c => c.SplitIntoNotes = value.Value);
    }

    partial void OnChordForceNoNotesChanged(bool? value)
    {
        if (value is null) return;
        ApplyChordMutation(c => c.ForceNoNotes = value.Value);
    }

    partial void OnChordPassOtherNotesChanged(bool? value)
    {
        if (value is null) return;
        ApplyChordMutation(c => c.PassOtherNotes = value.Value);
    }

    partial void OnChordIgnoreChanged(bool? value)
    {
        if (value is null) return;
        ApplyChordMutation(c => c.Ignore = value.Value);
    }

    partial void OnChordNoteIgnoreChanged(bool? value)
    {
        if (value is null) return;
        ApplyChordNoteMutation(cn => cn.Ignore = value.Value);
    }
}
