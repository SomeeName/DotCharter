// InspectorViewModel: the NOTE side - SyncNoteProperties and its tri-state fold, every per-note field handler, the placement fretboard target, the editable length, and the read-only provenance badges.

using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.UI.Controls;
using DotCharter.UI.ViewModels.Editor.Inspector;

namespace DotCharter.UI.ViewModels.Editor;

public partial class InspectorViewModel
{
    /// <summary>
    ///     What the selected note SOUNDS — <c>A2</c>, <c>C#4</c> — beside the
    ///     string and fret that produce it.
    ///     <para>
    ///         The card printed a string number and a fret number and stopped
    ///         there, and that is the one piece of arithmetic a charter cannot do
    ///         in their head on this app's turf: under a capo, or in any tuning
    ///         that is not E standard, fret 5 on the low E is not A. The octave
    ///         is part of the answer for the same reason.
    ///     </para>
    ///     <para>
    ///         Empty when there is nothing honest to name — no arrangement, a
    ///         string it does not have, or a fret behind the capo (rule
    ///         <c>L01</c>); the row hides itself rather than showing a wrong
    ///         note. See <see cref="SoundingPitchReadout" />.
    ///     </para>
    /// </summary>
    public string NotePitchName =>
        SoundingPitchReadout.For(_chartEditor.ActiveArrangement, NoteStringIndex, NoteFret);

    /// <summary>Whether <see cref="NotePitchName" /> has anything to show.</summary>
    public bool HasNotePitch => NotePitchName.Length > 0;

    private void SyncNoteProperties()
    {
        OnPropertyChanged(nameof(HasNoteSelection));
        OnPropertyChanged(nameof(HasSingleNoteSelection));
        OnPropertyChanged(nameof(IsMultiSelect));
        OnPropertyChanged(nameof(SelectionSummary));
        OnPropertyChanged(nameof(NoteStringIsOutsideMask));
        // The pitch also moves when the ARRANGEMENT does — a different tuning, a
        // capo edit — with the string and fret sitting still, so the two
        // NotifyPropertyChangedFor hops off NoteFret / NoteStringIndex are not
        // enough on their own.
        OnPropertyChanged(nameof(NotePitchName));
        OnPropertyChanged(nameof(HasNotePitch));
        // Same reason OnHandShapeEditorSynced does it: the selection-issue memo
        // reads SelectedNotes, and ISelectionService raises PropertyChanged only
        // when the SCALAR SelectedNote changes — which it does not when a
        // Ctrl+click appends a second note behind the first. The badge, the
        // field lines and the chord-box pips then still described the previous
        // selection. This handler IS the CollectionChanged path, so it is where
        // the memo has to be dropped.
        NotifySelectionIssuesChanged();
        NotifySelectionKindChanged();
        if (SelectedNote == null)
        {
            NoteBendEditor = null;
            NotePreviewChord = null;
            NoteSustainLabel = string.Empty;
            NoteEnums = default;
            return;
        }

        _isSyncing = true;

        var notes = SelectedNotes.Count > 0 ? SelectedNotes : (IReadOnlyList<GuitarNote>)[SelectedNote];
        // The strings BEFORE the string index: the String picker's items are
        // built from them and its SelectedIndex is the index.
        NoteStringCount = _chartEditor.ActiveArrangement?.Tuning.Length ?? 6;
        NoteTuningNames = _chartEditor.ActiveArrangement?.Tuning;
        NoteStringIndex = SelectedNote.StringIndex;
        NoteFret = SelectedNote.Fret;
        NoteSustainLabel = FormatSustain(SelectedNote.Sustain.DoubleValue);
        NoteSustainText = SustainBoxText(SelectedNote.Sustain.DoubleValue);
        NotePreviewChord = BuildNotePreviewChord(SelectedNote);
        var agreed = NoteFlagAgreement.Over(notes);
        NoteAccent = agreed.Accent;
        NoteVibrato = agreed.Vibrato;
        NoteLinkNext = agreed.LinkNext;
        NotePassOtherNotes = agreed.PassOtherNotes;
        NoteIgnore = agreed.Ignore;
        NoteTremolo = agreed.Tremolo;
        NoteUnpitchedSlide = agreed.UnpitchedSlide;
        NoteMute = (int)SelectedNote.Mute;
        NoteHopo = (int)SelectedNote.Hopo;
        NoteHarmonic = (int)SelectedNote.Harmonic;
        NoteBassPicking = (int)SelectedNote.BassPicking;
        NoteLeftHandFinger = SelectedNote.LeftHandFinger;
        NotePickDirection = (int)SelectedNote.PickDirection;
        NoteSlideTo = SelectedNote.SlideTo;
        // The four segmented pickers have no indeterminate state, so they SHOW
        // the first sound's value. Remember whether the selection actually agreed
        // on each, or "apply to all" broadcasts that first value over the rest.
        NoteEnums = NoteEnumAgreement.Over(notes);

        NoteBendEditor = BuildNoteBendEditor(SelectedNote);
        FollowSelectionBends(SelectedNote.BendValues.Count > 0);

        _isSyncing = false;
        SustainWasShortened = false;
        NotifyProvenanceChanged();
    }

    /// <summary>
    ///     The seven note flags the chips show, resolved in ONE pass.
    ///     <para>
    ///         They were seven <see cref="AllSameNullable{T}" /> calls over a
    ///         <c>SelectedNotes.ToList()</c> snapshot: eight walks of the selection
    ///         plus a full copy of it — and this whole sync runs TWICE per
    ///         selection edit, because the selection service re-derives its scalar
    ///         <c>SelectedNote</c> from inside its own <c>CollectionChanged</c>
    ///         handler and this view model is signalled on both. On a 5,000-note
    ///         selection that is ~80,000 element reads and 10,000 copied references
    ///         per keypress, for seven tri-state chips.
    ///     </para>
    ///     <para>
    ///         Answer-for-answer the same: each field latches to <c>null</c> on the
    ///         first note that disagrees, exactly as its own pass short-circuited,
    ///         and an empty list leaves every field <c>null</c>
    ///         (<c>default</c>) — <see cref="AllSameNullable{T}" />'s answer for
    ///         nothing.
    ///     </para>
    /// </summary>
    internal readonly record struct NoteFlagAgreement(
        bool? Accent, bool? Vibrato, bool? LinkNext, bool? PassOtherNotes,
        bool? Ignore, bool? Tremolo, bool? UnpitchedSlide)
    {
        public static NoteFlagAgreement Over(IReadOnlyList<GuitarNote> notes)
        {
            if (notes.Count == 0) return default;

            var first = notes[0];
            bool? accent = first.Accent;
            bool? vibrato = first.Vibrato;
            bool? linkNext = first.LinkNext;
            bool? passOther = first.PassOtherNotes;
            bool? ignore = first.Ignore;
            bool? tremolo = first.Tremolo;
            bool? unpitched = first.UnpitchedSlide;

            for (var i = 1; i < notes.Count; i++)
            {
                var n = notes[i];
                if (accent.HasValue && accent.Value != n.Accent) accent = null;
                if (vibrato.HasValue && vibrato.Value != n.Vibrato) vibrato = null;
                if (linkNext.HasValue && linkNext.Value != n.LinkNext) linkNext = null;
                if (passOther.HasValue && passOther.Value != n.PassOtherNotes) passOther = null;
                if (ignore.HasValue && ignore.Value != n.Ignore) ignore = null;
                if (tremolo.HasValue && tremolo.Value != n.Tremolo) tremolo = null;
                if (unpitched.HasValue && unpitched.Value != n.UnpitchedSlide) unpitched = null;
            }

            return new NoteFlagAgreement(accent, vibrato, linkNext, passOther, ignore, tremolo, unpitched);
        }
    }

    /// <summary>
    ///     Whether the selection AGREED on each of the four enum pickers, recorded
    ///     at sync time. <c>null</c> = mixed.
    ///     <para>
    ///         The seven binary techniques render as tri-state chips, so
    ///         <see cref="NoteFlagAgreement" /> can hand "mixed" straight to the
    ///         UI. <c>Seg</c> has no indeterminate state, so Mute / HOPO /
    ///         Harmonic / Picking show the FIRST selected sound's value — which is
    ///         a display compromise, not a claim about the rest. "Apply current
    ///         values to all" read those four straight off the pickers and wrote
    ///         them unconditionally, so on a mixed selection one click stamped the
    ///         first sound's palm mute (or HOPO, or pinch harmonic, or slap/pop) on
    ///         every other one — while the same method carefully skipped every
    ///         indeterminate CHIP, which is the contract it documents.
    ///     </para>
    ///     <para>
    ///         Nothing is lost by skipping a mixed picker: the per-field handlers
    ///         (<c>OnNoteMuteChanged</c> and its three siblings) already
    ///         write through <see cref="ApplyNoteMutation" /> to the WHOLE
    ///         selection, so a value the user actually chose has been applied to
    ///         everything before this verb is reachable — which is why
    ///         <see cref="RecordEnumAsAgreed" /> marks it agreed on the way past.
    ///     </para>
    /// </summary>
    internal readonly record struct NoteEnumAgreement(int? Mute, int? Hopo, int? Harmonic, int? BassPicking)
    {
        public static NoteEnumAgreement Over(IReadOnlyList<GuitarNote> notes)
        {
            if (notes.Count == 0) return default;

            var first = notes[0];
            int? mute = (int)first.Mute;
            int? hopo = (int)first.Hopo;
            int? harmonic = (int)first.Harmonic;
            int? picking = (int)first.BassPicking;

            for (var i = 1; i < notes.Count; i++)
            {
                var n = notes[i];
                if (mute.HasValue && mute.Value != (int)n.Mute) mute = null;
                if (hopo.HasValue && hopo.Value != (int)n.Hopo) hopo = null;
                if (harmonic.HasValue && harmonic.Value != (int)n.Harmonic) harmonic = null;
                if (picking.HasValue && picking.Value != (int)n.BassPicking) picking = null;
            }

            return new NoteEnumAgreement(mute, hopo, harmonic, picking);
        }
    }

    /// <summary>Agreement of the four enum pickers over the current selection.
    /// Internal for <c>InspectorMixedSelectionTests</c>.</summary>
    internal NoteEnumAgreement NoteEnums { get; private set; }

    /// <summary>A picker the user has just written is unanimous by construction —
    /// <see cref="ApplyNoteMutation" /> wrote it to every selected sound.</summary>
    private void RecordEnumAsAgreed(Func<NoteEnumAgreement, NoteEnumAgreement> update)
    {
        if (_isSyncing) return;
        NoteEnums = update(NoteEnums);
    }

    /// <summary>
    ///     The chord side of <see cref="NoteFlagAgreement" /> — five flags, one
    ///     pass, same latch-to-null semantics.
    /// </summary>
    internal readonly record struct ChordFlagAgreement(
        bool? Accent, bool? SplitIntoNotes, bool? ForceNoNotes, bool? PassOtherNotes, bool? Ignore)
    {
        public static ChordFlagAgreement Over(IReadOnlyList<Chord> chords)
        {
            if (chords.Count == 0) return default;

            var first = chords[0];
            bool? accent = first.Accent;
            bool? split = first.SplitIntoNotes;
            bool? forceNoNotes = first.ForceNoNotes;
            bool? passOther = first.PassOtherNotes;
            bool? ignore = first.Ignore;

            for (var i = 1; i < chords.Count; i++)
            {
                var c = chords[i];
                if (accent.HasValue && accent.Value != c.Accent) accent = null;
                if (split.HasValue && split.Value != c.SplitIntoNotes) split = null;
                if (forceNoNotes.HasValue && forceNoNotes.Value != c.ForceNoNotes) forceNoNotes = null;
                if (passOther.HasValue && passOther.Value != c.PassOtherNotes) passOther = null;
                if (ignore.HasValue && ignore.Value != c.Ignore) ignore = null;
            }

            return new ChordFlagAgreement(accent, split, forceNoNotes, passOther, ignore);
        }
    }

    // Returns the shared value if every item agrees, null otherwise.
    // Lets the inspector chips show indeterminate when a multi-
    // selection has heterogeneous values for a flag (all-true → true,
    // all-false → false, mixed → null → chip renders :indeterminate).
    // `internal` so the tri-state projection is unit-testable without the
    // full inspector view-model graph (see NoteTechniqueChipBehaviorTests).
    internal static bool? AllSameNullable<T>(IEnumerable<T> items, Func<T, bool> getter)
    {
        bool? acc = null;
        foreach (var it in items)
        {
            var v = getter(it);
            if (acc == null) acc = v;
            else if (acc.Value != v) return null;
        }
        return acc;
    }

    // --- Note / chord field handlers ---

    partial void OnNoteStringIndexChanged(int value)
    {
        // The String picker writes -1 while its items are being replaced; that
        // is the picker clearing itself, never a string.
        if (value < 0) return;
        ApplyNoteMutation(n => n.StringIndex = value);
    }

    /// <summary>The String picker's items: every string of the arrangement,
    /// named the way the lane gutter names it, in model order so the picker's
    /// SelectedIndex IS <see cref="NoteStringIndex" />.</summary>
    public IReadOnlyList<string> NoteStringLabels =>
        GuitarStringLabel.All(NoteStringCount, NoteTuningNames,
            _localization["Inspector_StringLabelFmt"]);

    partial void OnNoteTuningNamesChanged(IReadOnlyList<string>? value)
    {
        _ = value;
        OnPropertyChanged(nameof(NoteStringLabels));
    }

    partial void OnNoteStringCountChanged(int value)
    {
        _ = value;
        OnPropertyChanged(nameof(NoteStringLabels));
    }

    /// <summary>What the Length box shows: the grid-relative length, or "0" for
    /// a note with no sustain. An empty box read as "not loaded".</summary>
    internal static string SustainBoxText(double beats) => FractionalLengthFormat.Format(beats) ?? "0";

    partial void OnNoteFretChanged(int value)
    {
        ApplyNoteMutation(n => n.Fret = value);
    }

    partial void OnNoteAccentChanged(bool? value)
    {
        if (value is null) return;
        ApplyNoteMutation(n => n.Accent = value.Value);
    }

    partial void OnNoteVibratoChanged(bool? value)
    {
        if (value is null) return;
        var v = value.Value;
        ApplyNoteMutation(n => SetVibrato(n, v));
    }

    /// <summary>
    ///     THE way this panel writes vibrato: depth goes with the flag.
    ///     <para>
    ///         <c>SngFromDomainMapper</c> ORs the RS vibrato bit from
    ///         <c>note.Vibrato || note.VibratoStrength &gt; 0</c>, so a sound left
    ///         at <c>Vibrato=false, VibratoStrength=80</c> — what an import
    ///         produces and what clearing only the flag leaves behind — still
    ///         exports as vibrato. The two single-chip handlers folded the depth
    ///         in; the bulk verbs (<c>ResetNoteFlags</c>,
    ///         <c>ApplyNoteValuesToAll</c>) wrote the flag alone, so "Reset"
    ///         appeared to clear vibrato and the psarc still played it.
    ///     </para>
    ///     <para>
    ///         Setting it TRUE never invents a depth: RS writes
    ///         <c>max(strength, 80)</c> on export, and an imported depth the user
    ///         is re-enabling is theirs to keep.
    ///     </para>
    /// </summary>
    internal static void SetVibrato(IGuitarSound sound, bool on)
    {
        sound.Vibrato = on;
        if (!on) sound.VibratoStrength = 0;
    }

    partial void OnNoteLinkNextChanged(bool? value)
    {
        if (value is null) return;
        ApplyNoteMutation(n => n.LinkNext = value.Value);
    }

    partial void OnNotePassOtherNotesChanged(bool? value)
    {
        if (value is null) return;
        ApplyNoteMutation(n => n.PassOtherNotes = value.Value);
    }

    partial void OnNoteIgnoreChanged(bool? value)
    {
        if (value is null) return;
        var v = value.Value;
        // AUTHORED WINS (§6.5). Authoring Ignore must clear IgnoreDerived in the
        // SAME mutation: the enforcer's "clear only if IgnoreDerived was already
        // set" branch would otherwise discard the user's intent on the next
        // edit, silently. One line, inside the existing write funnel.
        ApplyNoteMutation(n =>
        {
            n.Ignore = v;
            n.IgnoreDerived = false;
        });
    }

    partial void OnNoteTremoloChanged(bool? value)
    {
        if (value is null) return;
        ApplyNoteMutation(n => n.Tremolo = value.Value);
    }

    partial void OnNoteUnpitchedSlideChanged(bool? value)
    {
        if (value is null) return;
        ApplyNoteMutation(n => n.UnpitchedSlide = value.Value);
    }

    partial void OnNoteMuteChanged(int value)
    {
        ApplyNoteMutation(n => n.Mute = (MuteType)value);
        RecordEnumAsAgreed(a => a with { Mute = value });
    }

    partial void OnNoteHopoChanged(int value)
    {
        ApplyNoteMutation(n => n.Hopo = (HopoType)value);
        RecordEnumAsAgreed(a => a with { Hopo = value });
    }

    partial void OnNoteHarmonicChanged(int value)
    {
        ApplyNoteMutation(n => n.Harmonic = (HarmonicType)value);
        RecordEnumAsAgreed(a => a with { Harmonic = value });
    }

    partial void OnNoteBassPickingChanged(int value)
    {
        ApplyNoteMutation(n => n.BassPicking = (BassPickingType)value);
        RecordEnumAsAgreed(a => a with { BassPicking = value });
    }

    partial void OnNoteLeftHandFingerChanged(int value) => ApplyNoteMutation(n => n.LeftHandFinger = value);

    partial void OnNotePickDirectionChanged(int value) => ApplyNoteMutation(n => n.PickDirection = (PickDirectionType)value);

    partial void OnNoteSlideToChanged(int? value)
    {
        ApplyNoteMutation(n => n.SlideTo = value);
    }

    /// <summary>
    /// Move the selected note(s) to the clicked string/fret. Wired to the
    /// placement fretboard's <c>ToggleNoteCommand</c> (payload = the hit cell)
    /// so a click sets string + fret in ONE undoable gesture, the same
    /// <see cref="ApplyNoteMutation" /> path every other note edit uses.
    /// </summary>
    [RelayCommand]
    private void SetNoteTarget((int stringIdx, int fret) pos)
    {
        var maxString = Math.Max(0, NoteStringCount - 1);
        var stringIdx = Math.Clamp(pos.stringIdx, 0, maxString);
        var fret = Math.Clamp(pos.fret, 0, 24);
        ApplyNoteMutation(n =>
        {
            n.StringIndex = stringIdx;
            n.Fret = fret;
        });
    }

    // A one-note synthetic chord the placement fretboard renders as a single
    // dot at the note's string/fret. Rebuilt (fresh reference) on every sync so
    // the control — which only repaints on a Chord reference change — follows
    // string/fret edits. Never enters the domain; view-only projection.
    private static Chord BuildNotePreviewChord(GuitarNote note)
    {
        var chord = new Chord { Position = note.Position };
        chord.ChordNotes.Add(new ChordNote
        {
            StringIndex = note.StringIndex,
            Fret = note.Fret,
            Mute = note.Mute
        });
        return chord;
    }

    private string FormatSustain(double beats)
    {
        var text = FractionalLengthFormat.Format(beats);
        if (text == null) return _localization["Inspector_SustainNone"];

        // PluralWith, not Plural: {0} is the FORMATTED length ("1½"), which is
        // not the integer that decides the form. A whole beat reads "1 beat" /
        // "1 Schlag"; everything else, 1½ included, takes the plural.
        return _localization.PluralWith(
            "Inspector_SustainBeatsOne", "Inspector_SustainBeats",
            beats == 1.0 ? 1 : 2, text);
    }

    // ---- the length editor (0.36, 04-inspector-redesign.md §6.2) -----------
    //
    // Sustain is editable, grid-relative (owner-confirmed: never milliseconds), and it
    // tells the truth about what the spacing enforcer did to the value.

    /// <summary>Editable, grid-relative length of the selected sound
    /// ("1/8", "3/16", "2"). Commits on Enter / LostFocus, never per keystroke.</summary>
    [ObservableProperty] private string _noteSustainText = string.Empty;

    /// <summary>
    ///     True when the committed length came back SHORTER than what was typed.
    ///     <c>ChartSpacingEnforcer</c> runs on every committed edit and only ever
    ///     shortens; the field re-reads the model afterwards and says so, because
    ///     the user must never be told their value stuck when it did not.
    /// </summary>
    [ObservableProperty] private bool _sustainWasShortened;

    /// <summary>
    ///     The commit point. The box binds with
    ///     <c>UpdateSourceTrigger=LostFocus</c>, so this property changes ONLY on
    ///     Enter / focus loss — which makes the change handler the commit, the
    ///     same shape every other field on this view model uses. Per-keystroke it
    ///     would rewrite the sustain on every digit.
    /// </summary>
    partial void OnNoteSustainTextChanged(string value)
    {
        _ = value;
        CommitSustainText();
    }

    /// <summary>Commit the typed length. Refuses garbage silently (the box keeps
    /// showing what the model really holds after the next sync) rather than
    /// writing a zero the user did not ask for.</summary>
    private void CommitSustainText()
    {
        if (_isSyncing || SelectedNote == null) return;
        if (!FractionalLengthFormat.TryParseBeats(NoteSustainText, out var numerator, out var denominator))
        {
            // Put the real value back so the box never lies about the model.
            SyncSustainReadout();
            return;
        }

        // Exact, not via a double: timing here is grid-relative by design.
        var target = new FractionalPosition(new Fraction(numerator, denominator));
        var requested = target.DoubleValue;
        ApplyNoteMutation(n => n.Sustain = target);

        // RE-READ. ChartSpacingEnforcer has already run inside the action's
        // post-pass and only ever SHORTENS, so the user is told when it did.
        SustainWasShortened = SelectedNote.Sustain.DoubleValue < requested - 0.0001;
        SyncSustainReadout();
    }

    /// <summary>▴ / ▾ — exactly one grid-divisor step, which is exactly what
    /// <c>]</c>/<c>[</c> and wheel rule 1 do, so the three agree.</summary>
    /// <remarks>
    ///     The parameter is a STRING because it comes from a XAML
    ///     <c>CommandParameter="1"</c> literal, which is a string. Declaring it
    ///     <c>int</c> makes CommunityToolkit generate a <c>RelayCommand&lt;int&gt;</c>
    ///     whose <c>CanExecute</c> THROWS <see cref="ArgumentException" /> the moment
    ///     the Button attaches to the logical tree — an unhandled crash during
    ///     layout, not a binding warning. `SheetMusicPaneViewModel.ApplyDurationValue`
    ///     already takes a string for exactly this reason; keep them consistent.
    /// </remarks>
    [RelayCommand]
    private void StepSustain(string? deltaText)
    {
        if (!int.TryParse(deltaText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var deltaGridSteps)) return;
        if (SelectedNote == null && SelectedChord == null) return;
        var before = SelectedNote?.Sustain.DoubleValue ?? 0;
        _highwayEditingService.AdjustSelectedNotesSustain(deltaGridSteps,
            _projectState.TotalAudioLengthMs, Transport.GridDivisor, _projectState.Beats);
        SustainWasShortened = deltaGridSteps > 0 && SelectedNote != null &&
                              SelectedNote.Sustain.DoubleValue <= before + 0.0001;
        SyncSustainReadout();
    }

    // ---- tier 3: read-only provenance (§6.4) ------------------------------
    //
    // These round-trip in the model but NOTHING in the app can author them.
    // Today they are invisible, which is worse than read-only: a charter
    // destroys them without knowing they were there. Shown as badges — shown,
    // never edited.

    /// <summary>RS writes max(v, 80) and the UI only ever ZEROES it, so an
    /// imported depth is real data the Vibrato chip can silently discard.</summary>
    public bool HasImportedVibratoDepth =>
        SelectedNote is { VibratoStrength: > 0 } n && n.VibratoStrength != 80;

    public string VibratoDepthBadge => SelectedNote == null
        ? string.Empty
        : _localization.Format("Inspector_More_VibratoDepth", SelectedNote.VibratoStrength);

    /// <summary>A notation-only ornament hint that reaches <c>.gp</c> and
    /// <c>.eof</c>. Fret 0 is a real (open-string) hint, not "absent".</summary>
    public bool HasGraceFret => SelectedNote?.GraceFret != null;

    public string GraceFretBadge => SelectedNote?.GraceFret is { } g
        ? _localization.Format("Inspector_More_Grace", g)
        : string.Empty;

    /// <summary>The authored rhythm hint. READ-ONLY here by owner decision — it
    /// is notation-only, never gameplay, never exported to RS, and the sheet
    /// palette already owns editing it.</summary>
    public bool HasNotationDuration => SelectedNote?.NotationDuration != null;

    public string NotationDurationBadge => SelectedNote?.NotationDuration is { } d
        ? _localization.Format("Inspector_More_NotationDuration",
            $"{d.Numerator}/{d.Denominator}")
        : string.Empty;

    /// <summary>Provenance, not a field: the <c>·auto</c> pip on the Ignore
    /// chip. Authored wins — authoring Ignore clears this flag in the same
    /// mutation, so the enforcer cannot take the choice back.</summary>
    public bool NoteIgnoreIsDerived => SelectedNote?.IgnoreDerived == true;

    /// <summary>Whether <c>▸ MORE</c> has anything to show. The disclosure is
    /// hidden for the ordinary note that carries none of the four badges: a
    /// toggle that opens onto nothing cost a row of the Placement card.</summary>
    public bool HasProvenanceBadges =>
        HasImportedVibratoDepth || HasGraceFret || HasNotationDuration || NoteIgnoreIsDerived;

    private void NotifyProvenanceChanged()
    {
        OnPropertyChanged(nameof(HasProvenanceBadges));
        OnPropertyChanged(nameof(HasImportedVibratoDepth));
        OnPropertyChanged(nameof(VibratoDepthBadge));
        OnPropertyChanged(nameof(HasGraceFret));
        OnPropertyChanged(nameof(GraceFretBadge));
        OnPropertyChanged(nameof(HasNotationDuration));
        OnPropertyChanged(nameof(NotationDurationBadge));
        OnPropertyChanged(nameof(NoteIgnoreIsDerived));
    }

    private void SyncSustainReadout()
    {
        if (SelectedNote == null)
        {
            NoteSustainLabel = string.Empty;
            NoteSustainText = string.Empty;
            return;
        }

        var wasSyncing = _isSyncing;
        _isSyncing = true;
        NoteSustainLabel = FormatSustain(SelectedNote.Sustain.DoubleValue);
        NoteSustainText = SustainBoxText(SelectedNote.Sustain.DoubleValue);
        _isSyncing = wasSyncing;
    }
}
