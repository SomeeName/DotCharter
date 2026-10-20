using Avalonia.Input;
using CommunityToolkit.Mvvm.ComponentModel;

namespace DotCharter.Services.Infrastructure.Configuration;

/// <summary>
///     User-rebindable key chords for every editor action. Each property is a
///     <see cref="KeyChord" /> (Key + optional Ctrl/Shift/Alt modifiers) so the
///     user can fully control whether an action requires a modifier — the
///     previous design hard-coded the modifier in <c>AppKeybindRegistrar</c>
///     and only let the user pick the bare <see cref="Key" />.
/// </summary>
public partial class KeybindConfig : ObservableObject
{
    /// <summary>
    ///     The highest keybind-migration wave that has been applied to this
    ///     config. Bump it in the same change that adds a wave to
    ///     <c>JsonConfigurationService.NormalizeKeybinds</c>.
    /// </summary>
    public const int CurrentMigrationVersion = 4;

    /// <summary>
    ///     Which migration waves this config has already been through.
    ///     <para>
    ///         Without it every wave re-ran on EVERY load, and a wave is an
    ///         equality test against the chord a verb defaulted to in older builds — so a
    ///         user who deliberately rebound a verb ONTO one of those old
    ///         defaults (Ctrl+I for the Inspector, Ctrl+F for 3D fingering,
    ///         Ctrl+B to add a beat — all of them the obvious choice for their
    ///         verb, which is why they were the defaults) had that choice reset
    ///         at every single launch, with nothing said. The migrations
    ///         themselves are careful not to overwrite a user's chord; they
    ///         simply could not tell "still the old default" from "chosen
    ///         again", and a stamp is the only thing that can.
    ///     </para>
    ///     <para>
    ///         0 is a config written before the stamp existed: every wave
    ///         applies once, and <c>LoadKeybinds</c> persists the stamp so it
    ///         never happens twice. A plain property, not
    ///         <c>[ObservableProperty]</c>: it is bookkeeping, not a setting —
    ///         which is also what keeps it out of the palette/parity reflection
    ///         (it reads <see cref="KeyChord" /> properties only).
    ///     </para>
    /// </summary>
    public int MigrationVersion { get; set; }

    // --- Transport ---
    [ObservableProperty] private KeyChord _playPause = new(Key.Space);
    [ObservableProperty] private KeyChord _toggleMetronome = new(Key.M);
    [ObservableProperty] private KeyChord _toggleClaps = new(Key.C);

    // Play the chart: the one MIDI Out / chart-synth switch, on EOF's own
    // "MIDI Tones" chord (EOF_SRC/menu/edit.c:227, "&MIDI Tones	Shift+T").
    [ObservableProperty] private KeyChord _toggleChartSynth = new(Key.T, KeyModifiers.Shift);
    [ObservableProperty] private KeyChord _toggleWaveform = new(Key.F5);
    [ObservableProperty] private KeyChord _speedUp = new(Key.OemPlus);
    [ObservableProperty] private KeyChord _speedUpFast = new(Key.OemPlus, KeyModifiers.Control);
    [ObservableProperty] private KeyChord _speedUpFine = new(Key.OemPlus, KeyModifiers.Shift);
    [ObservableProperty] private KeyChord _speedDown = new(Key.OemMinus);
    [ObservableProperty] private KeyChord _speedDownFast = new(Key.OemMinus, KeyModifiers.Control);
    [ObservableProperty] private KeyChord _speedDownFine = new(Key.OemMinus, KeyModifiers.Shift);
    [ObservableProperty] private KeyChord _doubleGrid = new(Key.OemPeriod);
    [ObservableProperty] private KeyChord _halveGrid = new(Key.OemComma);

    // --- The editor loop region (v0.35, editing rework stage 3) ---
    // rscharter's repeater keys verbatim where they are free here: Alt+[ punches
    // the loop start, Alt+] the end, F6 toggles (DefaultShortcuts.java:169-171).
    // The bare and Ctrl rows of those brackets are already sustain ±1 step and
    // zoom, and Alt+<letter> is reserved for the "group" technique variants —
    // Alt+<bracket> is neither, so it stays free of that convention. NEW rows,
    // so no NormalizeKeybinds migration.
    [ObservableProperty] private KeyChord _setLoopStart = new(Key.Home, KeyModifiers.Alt);
    [ObservableProperty] private KeyChord _setLoopEnd = new(Key.End, KeyModifiers.Alt);
    [ObservableProperty] private KeyChord _toggleLoop = new(Key.F6);

    /// <summary>Play the resolved edit target once. Sits in the same
    /// function-key band as the other playback-feedback keys (F5 waveform,
    /// F6 loop), which is where rscharter puts its audition family too
    /// (F2 MIDI chart playback, F3 claps, F4 metronome, F6 repeat).</summary>
    [ObservableProperty] private KeyChord _auditionSelection = new(Key.F7);

    // --- The transport bar's grouped popovers (v0.54) -----------------------
    // `Play Menus.dc.html` prints ALT+M, ALT+T and ALT+A in the Mix, Tools and
    // Tracks footers. Until now nothing handled them, so the caps were left
    // BLANK rather than printed — a key legend naming a chord no route claims
    // is the same class of lie as a capability string nothing implements. These
    // three rows are what make the mockup's caps true; the footers print them
    // LIVE through `KeyChordToGestureConverter`, exactly as Undo and Redo
    // already do, so rebinding one updates the panel that opens with it.
    //
    // COLLISIONS: none. Every Alt+<letter> default in this file is Alt+H, Alt+P
    // and Alt+G (the group technique variants) plus Alt+D0; M, T and A are free
    // with Alt, and the bare M (metronome), bare A and Shift+A (accent) are
    // different chords. They are NEW rows, so — like the loop-region trio above
    // — they need no `NormalizeKeybinds` migration; a migration moves a binding
    // whose DEFAULT changed, and these had none.
    //
    // They do sit beside the "Alt+<letter> is the group technique family"
    // convention noted above. That is a deliberate exception, made because the
    // owner's mockup names these exact three chords, and it is written down
    // here so the next person reads it as a decision rather than a mistake.
    [ObservableProperty] private KeyChord _openMixPopover = new(Key.M, KeyModifiers.Alt);
    [ObservableProperty] private KeyChord _openToolsPopover = new(Key.T, KeyModifiers.Alt);
    [ObservableProperty] private KeyChord _openTracksPopover = new(Key.A, KeyModifiers.Alt);

    // --- Navigation ---
    [ObservableProperty] private KeyChord _clearSelection = new(Key.Escape);
    [ObservableProperty] private KeyChord _seekStart = new(Key.Home);
    [ObservableProperty] private KeyChord _seekEnd = new(Key.End);
    [ObservableProperty] private KeyChord _moveToFirstItem = new(Key.Home, KeyModifiers.Control);
    [ObservableProperty] private KeyChord _moveToLastItem = new(Key.End, KeyModifiers.Control);
    [ObservableProperty] private KeyChord _seekLeft = new(Key.Left);
    [ObservableProperty] private KeyChord _seekLeftFast = new(Key.Left, KeyModifiers.Control);
    [ObservableProperty] private KeyChord _seekLeftFine = new(Key.Left, KeyModifiers.Shift);
    [ObservableProperty] private KeyChord _seekRight = new(Key.Right);
    [ObservableProperty] private KeyChord _seekRightFast = new(Key.Right, KeyModifiers.Control);
    [ObservableProperty] private KeyChord _seekRightFine = new(Key.Right, KeyModifiers.Shift);

    // --- EOF's PgUp/PgDn navigation family (v0.39) --------------------------
    // Ported VERBATIM from Editor on Fire's Song → Seek menu
    // (EOF_SRC/menu/song.c:72-95), because that family is the whole reason EOF
    // charts fast: one key pair walks the chart at four different granularities
    // and the modifier picks which. The owner asked for EOF's table as the
    // default, and this is the part they described as "jump between measure and
    // notes".
    //
    //   PgUp/PgDn              previous / next BEAT        (song.c:91-92)
    //   Shift+PgUp/PgDn        previous / next NOTE        (song.c:64-65)
    //   Ctrl+PgUp/PgDn         previous / next SCREEN      (song.c:85-86)
    //   Ctrl+Shift+PgUp/PgDn   previous / next GRID SNAP   (song.c:87-88)
    //   Alt+PgUp/PgDn          previous / next ANCHOR      (song.c:93-94)
    //
    // Everything this displaced is re-homed below, and every move is migrated
    // by JsonConfigurationService.NormalizeKeybinds so an existing config is not
    // silently rewritten.
    [ObservableProperty] private KeyChord _seekPrevBeat = new(Key.PageUp);
    [ObservableProperty] private KeyChord _seekNextBeat = new(Key.PageDown);

    /// <summary>Sound-by-sound seek. Not the BARE PgUp/PgDn row:
    /// EOF puts note navigation on the Shift row and the beat grid on the bare
    /// one, which is the pairing that makes the family learnable.</summary>
    [ObservableProperty] private KeyChord _seekNext = new(Key.PageDown, KeyModifiers.Shift);

    [ObservableProperty] private KeyChord _seekPrev = new(Key.PageUp, KeyModifiers.Shift);

    /// <summary>Extend-the-selection variants of the note seek. EOF has no
    /// equivalent, so they take the one free row of the same key pair rather
    /// than leaving the family split across unrelated keys.</summary>
    [ObservableProperty] private KeyChord _seekNextExtend = new(Key.PageDown, KeyModifiers.Shift | KeyModifiers.Alt);

    [ObservableProperty] private KeyChord _seekPrevExtend = new(Key.PageUp, KeyModifiers.Shift | KeyModifiers.Alt);

    /// <summary>A screen at a time — EOF's "Previous/Next Screen".</summary>
    [ObservableProperty] private KeyChord _seekPrevScreen = new(Key.PageUp, KeyModifiers.Control);

    [ObservableProperty] private KeyChord _seekNextScreen = new(Key.PageDown, KeyModifiers.Control);

    /// <summary>Step to the previous / next GRID LINE. EOF calls it "grid snap"
    /// and puts it on Ctrl+Shift+PgUp/PgDn; it does not live on Alt+←/→ here,
    /// which would leave the arrow keys carrying three unrelated jobs.</summary>
    [ObservableProperty] private KeyChord _seekPrevGridLine = new(Key.PageUp, KeyModifiers.Control | KeyModifiers.Shift);

    [ObservableProperty]
    private KeyChord _seekNextGridLine =
        new(Key.PageDown, KeyModifiers.Control | KeyModifiers.Shift);

    /// <summary>Walk the FRET-HAND POSITIONS — EOF's "Previous/Next Anchor".
    /// One owner session was ~100 anchor interactions in 20
    /// minutes, every one of them reached by hunting with the mouse.</summary>
    [ObservableProperty] private KeyChord _seekPrevAnchor = new(Key.PageUp, KeyModifiers.Alt);

    [ObservableProperty] private KeyChord _seekNextAnchor = new(Key.PageDown, KeyModifiers.Alt);

    /// <summary>Step between the chords and hand shapes a pending bulk chord edit
    /// would change — the chord panel's preview marks. Alt+Left/Right are
    /// free because the grid-line seek sits on Ctrl+Shift+PgUp/PgDn.</summary>
    [ObservableProperty] private KeyChord _seekPrevEditTarget = new(Key.Left, KeyModifiers.Alt);

    [ObservableProperty] private KeyChord _seekNextEditTarget = new(Key.Right, KeyModifiers.Alt);

    /// <summary>EOF's "First Beat" (Ctrl+Shift+Home) and "End of chart"
    /// (Ctrl+Shift+End) — distinct from Home/End, which are the AUDIO bounds,
    /// and from Ctrl+Home/End, which are the first/last SOUND.</summary>
    [ObservableProperty] private KeyChord _seekFirstBeat = new(Key.Home, KeyModifiers.Control | KeyModifiers.Shift);

    [ObservableProperty] private KeyChord _seekChartEnd = new(Key.End, KeyModifiers.Control | KeyModifiers.Shift);

    /// <summary>EOF's bare <c>R</c> rewind (song.c:80).</summary>
    [ObservableProperty] private KeyChord _rewind = new(Key.R);

    /// <summary>The inspector work queue's «« / »» — seek AND select the
    /// previous/next sound matching the queue's filter (missing fingering,
    /// unnamed chord, blocks export, …). EOF's <c>F</c> dialog, generalised.
    /// <para>
    /// Not on <c>Ctrl+PgUp/PgDn</c>, which EOF spends on
    /// previous/next SCREEN. They land on EOF's <c>Shift+Y</c> /
    /// <c>Ctrl+Shift+Y</c> — "next / previous HIGHLIGHTED note"
    /// (<c>EOF_SRC/menu/song.c:67-68</c>) — which is the same idea: seek only
    /// over the sounds that are flagged as needing attention.
    /// </para></summary>
    [ObservableProperty] private KeyChord _queuePrevMatch = new(Key.Y, KeyModifiers.Control | KeyModifiers.Shift);

    [ObservableProperty] private KeyChord _queueNextMatch = new(Key.Y, KeyModifiers.Shift);
    [ObservableProperty] private KeyChord _arrangementNext = new(Key.Up, KeyModifiers.Control | KeyModifiers.Shift);
    [ObservableProperty] private KeyChord _arrangementPrevious = new(Key.Down, KeyModifiers.Control | KeyModifiers.Shift);

    // --- Global Editing ---
    [ObservableProperty] private KeyChord _undo = new(Key.Z, KeyModifiers.Control);
    [ObservableProperty] private KeyChord _redo = new(Key.Y, KeyModifiers.Control);
    [ObservableProperty] private KeyChord _copy = new(Key.C, KeyModifiers.Control);
    [ObservableProperty] private KeyChord _cut = new(Key.X, KeyModifiers.Control);
    [ObservableProperty] private KeyChord _paste = new(Key.V, KeyModifiers.Control);
    /// <summary>EOF's <c>Shift+Ins</c> (menu/edit.c:22).</summary>
    [ObservableProperty] private KeyChord _pasteAtMouse = new(Key.Insert, KeyModifiers.Shift);
    [ObservableProperty] private KeyChord _pasteSpecial = new(Key.V, KeyModifiers.Control | KeyModifiers.Shift);
    [ObservableProperty] private KeyChord _selectAll = new(Key.A, KeyModifiers.Control);
    [ObservableProperty] private KeyChord _selectLike = new(Key.L, KeyModifiers.Shift);

    /// <summary>
    ///     Opens the Pattern Builder — the surface where "alike" stops being three
    ///     hard-coded matchers and becomes editable data. Next to
    ///     <see cref="SelectLike" /> (Shift+L) because it is the same family;
    ///     <c>L</c> is EOF's own letter for select-like (<c>Shift+L</c>), and the
    ///     bare Ctrl row of that key was free.
    /// </summary>
    [ObservableProperty] private KeyChord _patternBuilder = new(Key.L, KeyModifiers.Control);

    /// <summary>
    ///     Drop the LAST section marker again at the playhead, without opening the
    ///     dialog. Sections repeat hard — an owner session placed 18
    ///     choruses among 44 markers, and the hand-finished corpus is 31.8% chorus
    ///     and 19.1% verse — so the second one onward is pure repetition.
    ///     <c>Shift+M</c> because the bare M row is the metronome and the marker
    ///     rows are what this writes to.
    /// </summary>
    [ObservableProperty] private KeyChord _stampSection = new(Key.S, KeyModifiers.Shift);

    [ObservableProperty] private KeyChord _delete = new(Key.Delete);
    [ObservableProperty] private KeyChord _deleteRelated = new(Key.Delete, KeyModifiers.Shift);
    [ObservableProperty] private KeyChord _duplicateSelection = new(Key.D, KeyModifiers.Control);
    /// <summary>EOF's <c>Shift+R</c> "Resnap to this grid".</summary>
    [ObservableProperty] private KeyChord _snapSelected = new(Key.R, KeyModifiers.Shift);
    [ObservableProperty] private KeyChord _snapAll = new(Key.R, KeyModifiers.Alt);

    // --- Note Modifiers ---
    [ObservableProperty] private KeyChord _stringUp = new(Key.Up);
    [ObservableProperty] private KeyChord _stringDown = new(Key.Down);
    [ObservableProperty] private KeyChord _fretUp = new(Key.Up, KeyModifiers.Shift);
    [ObservableProperty] private KeyChord _fretDown = new(Key.Down, KeyModifiers.Shift);
    /// <summary>
    ///     Move to the next string KEEPING THE PITCH — fret 5 on the low E becomes
    ///     fret 0 on the A. Guitar Pro spells it Alt+Up / Alt+Down and so do we.
    /// </summary>
    [ObservableProperty] private KeyChord _stringUpSamePitch = new(Key.Up, KeyModifiers.Alt);

    [ObservableProperty] private KeyChord _stringDownSamePitch = new(Key.Down, KeyModifiers.Alt);

    [ObservableProperty] private KeyChord _fretUpOctave = new(Key.Up, KeyModifiers.Shift | KeyModifiers.Alt);
    [ObservableProperty] private KeyChord _fretDownOctave = new(Key.Down, KeyModifiers.Shift | KeyModifiers.Alt);
    [ObservableProperty] private KeyChord _toggleHopo = new(Key.H);
    [ObservableProperty] private KeyChord _toggleHarmonic = new(Key.H, KeyModifiers.Shift);
    /// <summary>EOF's <c>Shift+X</c> "Toggle string mute".</summary>
    [ObservableProperty] private KeyChord _toggleMute = new(Key.X, KeyModifiers.Shift);
    [ObservableProperty] private KeyChord _toggleLinkNext = new(Key.N, KeyModifiers.Shift);
    [ObservableProperty] private KeyChord _toggleVibrato = new(Key.V, KeyModifiers.Shift);
    [ObservableProperty] private KeyChord _toggleTremolo = new(Key.O, KeyModifiers.Control | KeyModifiers.Shift);
    [ObservableProperty] private KeyChord _togglePicking = new(Key.P);
    [ObservableProperty] private KeyChord _toggleAccent = new(Key.A, KeyModifiers.Shift);
    /// <summary>
    ///     The Rocksmith "ignore" flag. EOF spells it <c>Ctrl+Shift+I</c>
    ///     (<c>EOF_SRC/menu/note.c:722</c>, <c>{"&amp;Ignore	" CTRL_NAME
    ///     "+Shift+I", eof_menu_note_toggle_rs_ignore}</c>) — the same RS-ignore
    ///     status this toggles, not an approximation. A bare <c>G</c>
    ///     would be a chord of this project's own invention: EOF leaves
    ///     bare <c>G</c> unbound entirely and spends <c>Ctrl+G</c> on ghost
    ///     status (here <see cref="ToggleGhostGems" />) and
    ///     <c>Shift+G</c> on the grid lines.
    ///     <para>
    ///         It therefore sits on the <c>Ctrl+Shift</c>+letter row while being
    ///         a NOTE-level flag rather than a chord-level one. That is a
    ///         deliberate exception to the convention, made because EOF is the
    ///         authority the owner asked for; the same exception the Alt+M/T/A
    ///         popover row above records.
    ///     </para>
    /// </summary>
    [ObservableProperty] private KeyChord _toggleIgnore = new(Key.I, KeyModifiers.Control | KeyModifiers.Shift);
    [ObservableProperty] private KeyChord _togglePassOtherNotes = new(Key.O, KeyModifiers.Shift);

    // Group ("set all to first.next") variants. v0.35: these ship BOUND — ~25
    // shipped actions were unreachable out of the box, so a large part of the
    // vocabulary existed only in the Keybinds settings tab (editing-rework
    // audit 03 §0(b), §8 stage 0). The scheme is "the single-key chord plus
    // Alt", mirroring rscharter's Alt-prefixed "independently" variants.
    [ObservableProperty] private KeyChord _toggleHopoGroup = new(Key.H, KeyModifiers.Alt);
    [ObservableProperty] private KeyChord _toggleHarmonicGroup = new(Key.H, KeyModifiers.Shift | KeyModifiers.Alt);
    [ObservableProperty] private KeyChord _toggleMuteGroup = new(Key.X, KeyModifiers.Shift | KeyModifiers.Alt);
    [ObservableProperty] private KeyChord _toggleLinkNextGroup = new(Key.N, KeyModifiers.Shift | KeyModifiers.Alt);
    [ObservableProperty] private KeyChord _toggleVibratoGroup = new(Key.V, KeyModifiers.Shift | KeyModifiers.Alt);

    [ObservableProperty]
    private KeyChord _toggleTremoloGroup =
        new(Key.O, KeyModifiers.Control | KeyModifiers.Shift | KeyModifiers.Alt);

    [ObservableProperty] private KeyChord _togglePickingGroup = new(Key.P, KeyModifiers.Alt);
    [ObservableProperty] private KeyChord _toggleAccentGroup = new(Key.A, KeyModifiers.Shift | KeyModifiers.Alt);
    /// <summary>
    ///     Follows <see cref="ToggleIgnore" /> onto <c>I</c>. NOT
    ///     <c>Ctrl+Shift+Alt+I</c>, which the "chord plus Alt" rule for this
    ///     family would otherwise give it: <c>Ctrl+Alt</c> IS AltGr on the
    ///     owner's German layout, so that chord types a character instead of
    ///     firing. <c>Shift+Alt+I</c> keeps the letter and the Alt marker
    ///     without the AltGr collision.
    /// </summary>
    [ObservableProperty] private KeyChord _toggleIgnoreGroup = new(Key.I, KeyModifiers.Shift | KeyModifiers.Alt);

    [ObservableProperty]
    private KeyChord _togglePassOtherNotesGroup =
        new(Key.O, KeyModifiers.Shift | KeyModifiers.Alt);

    [ObservableProperty] private KeyChord _increaseLength = new(Key.OemCloseBrackets);
    [ObservableProperty] private KeyChord _decreaseLength = new(Key.OemOpenBrackets);
    /// <summary>
    ///     EOF puts zoom on the ALT row of the same two keys whose BARE row is
    ///     the grid snap: <c>ALT+.</c> zooms in and <c>ALT+,</c> zooms out
    ///     (<c>EOF_SRC/editor.c:2064</c>, <c>KEY_STOP</c> + <c>KEY_EITHER_ALT</c>
    ///     -> <c>eof_menu_edit_zoom_helper_in</c>; <c>editor.c:2035</c>,
    ///     <c>KEY_COMMA</c> + <c>KEY_EITHER_ALT</c> ->
    ///     <c>eof_menu_edit_zoom_helper_out</c>). That pairing is the point: one
    ///     key pair carries "how fine is the grid" bare and "how close am I"
    ///     on Alt, so the charter never leaves the two keys.
    ///     <para>
    ///         These are not on <c>Ctrl+[</c> / <c>Ctrl+]</c>, which EOF
    ///         spends on "move notes backward/forward one grid snap"
    ///         (<c>EOF_SRC/menu/note.c:822-823</c>). DotCharter has no
    ///         move-by-grid-snap verb, so those two chords are left UNBOUND
    ///         rather than filled — a keybind onto nothing is worse than none.
    ///     </para>
    /// </summary>
    [ObservableProperty] private KeyChord _zoomIn = new(Key.OemPeriod, KeyModifiers.Alt);

    [ObservableProperty] private KeyChord _zoomOut = new(Key.OemComma, KeyModifiers.Alt);

    // --- Tools ---
    [ObservableProperty] private KeyChord _editFhp = new(Key.F, KeyModifiers.Shift);

    /// <summary>
    ///     Nudge the selected FHP anchor without the dialog. Alt+plus/minus for
    ///     the fret, Shift+Alt for the hand span — deliberately next to
    ///     <c>Shift+F</c>, which opens the dialog for everything else.
    ///     <para>
    ///         Exists because the dialog was the ONLY route to an existing
    ///         anchor: 16 modal round trips in one session, 12 of them a +/-1 fret
    ///         change, two of them consecutive on the same anchor.
    ///     </para>
    /// </summary>
    [ObservableProperty] private KeyChord _anchorFretUp = new(Key.OemPlus, KeyModifiers.Alt);

    [ObservableProperty] private KeyChord _anchorFretDown = new(Key.OemMinus, KeyModifiers.Alt);

    [ObservableProperty]
    private KeyChord _anchorWidthUp = new(Key.OemPlus, KeyModifiers.Shift | KeyModifiers.Alt);

    [ObservableProperty]
    private KeyChord _anchorWidthDown = new(Key.OemMinus, KeyModifiers.Shift | KeyModifiers.Alt);
    [ObservableProperty] private KeyChord _generateHandShape = new(Key.G, KeyModifiers.Control | KeyModifiers.Shift);

    /// <summary>Generate hand shape from selection, as an ARPEGGIO. The
    /// Alt variant of <see cref="GenerateHandShape" />, with the Ctrl dropped because
    /// <c>Ctrl+Shift+Alt</c> is AltGr+Shift on a German layout (the
    /// <see cref="ToggleIgnoreGroup" /> precedent).</summary>
    [ObservableProperty] private KeyChord _generateArpeggio = new(Key.G, KeyModifiers.Shift | KeyModifiers.Alt);

    /// <summary>Split the hand shape covering the selected sound AT that sound
    /// (owner request). Next to Ctrl+Shift+G, which is the other
    /// hand-shape verb; Ctrl+Shift+K was free.</summary>
    [ObservableProperty] private KeyChord _splitHandShape = new(Key.K, KeyModifiers.Control | KeyModifiers.Shift);

    /// <summary>Accept the suggested hand shapes holding the selection: the dashed
    /// bars the suggester draws over single-note runs become real bars, one undo step. U for
    /// "use"; <c>Ctrl+Shift+U</c> was free, and EOF binds no verb DotCharter has on it.</summary>
    [ObservableProperty]
    private KeyChord _acceptSuggestedHandShapes = new(Key.U, KeyModifiers.Control | KeyModifiers.Shift);

    /// <summary>The same accept as ARPEGGIOS: the Alt variant of
    /// <see cref="AcceptSuggestedHandShapes" />, Ctrl dropped as <see cref="GenerateArpeggio" />
    /// drops it (<c>Ctrl+Shift+Alt</c> is AltGr+Shift on a German layout).</summary>
    [ObservableProperty]
    private KeyChord _acceptSuggestedArpeggios = new(Key.U, KeyModifiers.Shift | KeyModifiers.Alt);

    /// <summary>Repeat previous chord: the shape of the nearest chord before the
    /// playhead, placed at the playhead, pointing at that chord's template. A chord-LEVEL
    /// structural verb, so the <c>Ctrl+Shift</c>+letter row, on <c>C</c> for chord;
    /// <c>Ctrl+Shift+C</c> was free on every row and in EOF's parity list.</summary>
    [ObservableProperty] private KeyChord _repeatPreviousChord = new(Key.C, KeyModifiers.Control | KeyModifiers.Shift);

    /// <summary>Muted strums → muted chord: the muted single notes after a chord become
    /// muted copies of it inside its hand shape. A chord-LEVEL structural verb beside
    /// <see cref="RepeatPreviousChord" />, so the <c>Ctrl+Shift</c>+letter row, on <c>M</c> for
    /// muted; <c>Ctrl+Shift+M</c> was free.</summary>
    [ObservableProperty] private KeyChord _mutedStrumsToChord = new(Key.M, KeyModifiers.Control | KeyModifiers.Shift);

    /// <summary>Ghost gems: EOF's own chord for "toggle ghost" (<c>Ctrl+G</c>, the
    /// <see cref="ToggleIgnore" /> note above records it), free here until now. The selected
    /// gems become held-not-played frets of the hand shape over them.</summary>
    [ObservableProperty] private KeyChord _toggleGhostGems = new(Key.G, KeyModifiers.Control);

    /// <summary>The tapped-harmonic shortcut: harmonic + tap on the note and a ghost
    /// fretting finger in its hand shape. EOF has no such verb; a structural verb, so the
    /// <c>Ctrl+Shift</c>+letter row, on <c>R</c> (har<c>R</c>monic — <c>H</c> is the chord
    /// pass-other-notes flag); <c>Ctrl+Shift+R</c> was free.</summary>
    [ObservableProperty] private KeyChord _tappedHarmonic = new(Key.R, KeyModifiers.Control | KeyModifiers.Shift);

    /// <summary>
    ///     EOF's Beat ▸ Double/Halve tempo pair
    ///     (<c>EOF_SRC/menu/beat.c:77</c>, <c>{"&amp;Selected	" CTRL_NAME
    ///     "+Shift+D"}</c>; <c>beat.c:84</c>, <c>{"&amp;Selected	" CTRL_NAME
    ///     "+Shift+X"}</c>). <see cref="DoubleTime" /> already sat on EOF's
    ///     chord; <see cref="HalfTime" /> is not <c>Ctrl+Shift+H</c>,
    ///     which would leave the pair split across two unrelated letters and only half
    ///     of it learnable from EOF. The letters are not mnemonic in EOF either
    ///     — D and X are simply where the Beat menu puts them — which is exactly
    ///     why guessing one is worse than reading it.
    /// </summary>
    [ObservableProperty] private KeyChord _doubleTime = new(Key.D, KeyModifiers.Control | KeyModifiers.Shift);

    [ObservableProperty] private KeyChord _halfTime = new(Key.X, KeyModifiers.Control | KeyModifiers.Shift);
    [ObservableProperty] private KeyChord _toggle3DFingering = new(Key.F8);
    [ObservableProperty] private KeyChord _toggleInspector = new(Key.F9);

    /// <summary>v0.35: moved off <c>Ctrl+3</c> so the whole <c>Ctrl+0..9</c> row
    /// can carry the string-edit mask (<see cref="StringMaskBase" />), which is
    /// rscharter's binding for it. <c>JsonConfigurationService.NormalizeKeybinds</c>
    /// migrates a config still holding the old default.</summary>
    [ObservableProperty] private KeyChord _togglePreview3D = new(Key.P, KeyModifiers.Control);

    /// <summary>The AI chat pane: <c>Ctrl+Shift+A</c>, A for AI; it was free.</summary>
    [ObservableProperty] private KeyChord _toggleAiChat = new(Key.A, KeyModifiers.Control | KeyModifiers.Shift);

    [ObservableProperty] private KeyChord _tapLyric = new(Key.Space, KeyModifiers.Control);

    [ObservableProperty]
    private KeyChord _toggleLyricWordPart =
        new(Key.W, KeyModifiers.Control | KeyModifiers.Shift);

    [ObservableProperty]
    private KeyChord _toggleLyricPhraseEnd =
        new(Key.E, KeyModifiers.Control | KeyModifiers.Shift);

    [ObservableProperty]
    private KeyChord _applyHandShapeTemplateToChords =
        new(Key.T, KeyModifiers.Control | KeyModifiers.Shift);

    /// <summary>
    ///     EOF's Beat menu spells these <c>Ctrl+Ins</c> and <c>Ctrl+Del</c>
    ///     (<c>EOF_SRC/menu/beat.c:144-145</c>, <c>{"&amp;Add	" CTRL_NAME "+Ins"}</c>
    ///     and <c>{"Delete	" CTRL_NAME "+Del"}</c>) — the insert/delete keys
    ///     doing what they are named for, one level up from the bare
    ///     <c>Del</c> that removes a NOTE. They are not <c>Ctrl+B</c> /
    ///     <c>Ctrl+Shift+B</c>, a letter EOF spends on the tom
    ///     cymbal toggles instead.
    /// </summary>
    [ObservableProperty] private KeyChord _insertBeatAtPlayhead = new(Key.Insert, KeyModifiers.Control);

    [ObservableProperty] private KeyChord _removeBeatAtPlayhead = new(Key.Delete, KeyModifiers.Control);

    /// <summary>
    ///     Not on <c>Ctrl+Shift+I</c>, which EOF spells for the
    ///     RS ignore flag (<see cref="ToggleIgnore" />). <c>J</c> is the one
    ///     letter EOF binds on NO row at all, so taking it cannot shadow a verb
    ///     a charter already has in their fingers; it also keeps this next to
    ///     <see cref="SplitHandShape" /> (<c>Ctrl+Shift+K</c>) on the same row.
    /// </summary>
    [ObservableProperty]
    private KeyChord _insertVocalAtPlayhead =
        new(Key.J, KeyModifiers.Control | KeyModifiers.Shift);

    // Chord-level flags live on Ctrl+Shift+<letter>; the per-STRING technique
    // toggles above stay on the bare/Shift rows so the two families never read
    // as variants of each other.
    [ObservableProperty]
    private KeyChord _toggleChordSplit =
        new(Key.N, KeyModifiers.Control | KeyModifiers.Shift);

    [ObservableProperty]
    private KeyChord _toggleChordOnlyBox =
        new(Key.F, KeyModifiers.Control | KeyModifiers.Shift);

    /// <summary>
    ///     Not on <c>Ctrl+Shift+X</c>, which EOF spells for
    ///     "halve tempo, selected" (<see cref="HalfTime" />). A straight SWAP
    ///     with that verb rather than a new chord: it stays on the
    ///     <c>Ctrl+Shift</c>+letter row where every other chord-LEVEL flag
    ///     lives, and EOF spends <c>Ctrl+Shift+H</c> on nothing — its
    ///     high-density toggle is <c>Ctrl+ALT+H</c>
    ///     (<c>EOF_SRC/menu/note.c:725</c>), which this project may not use
    ///     because <c>Ctrl+Alt</c> IS AltGr on the owner's German layout.
    /// </summary>
    [ObservableProperty]
    private KeyChord _toggleChordPassOtherNotes =
        new(Key.H, KeyModifiers.Control | KeyModifiers.Shift);

    // 5 and 8 are the intervals: root+5th, root+5th+octave.
    [ObservableProperty]
    private KeyChord _convertToPowerChordSmall =
        new(Key.D5, KeyModifiers.Control | KeyModifiers.Shift);

    [ObservableProperty]
    private KeyChord _convertToPowerChordBig =
        new(Key.D8, KeyModifiers.Control | KeyModifiers.Shift);

    // --- Chord-template fingerings (-1 = unspecified, 0 = thumb,
    // 1..4 = index/middle/ring/pinky). rscharter puts these on Alt+1..4 /
    // Alt+5; here Alt+digit is already "set fret", so they take Shift+Alt+digit
    // and Shift+Alt+Backspace for "clear". ---
    [ObservableProperty]
    private KeyChord _setFingerNone =
        new(Key.Back, KeyModifiers.Shift | KeyModifiers.Alt);

    [ObservableProperty]
    private KeyChord _setFingerThumb =
        new(Key.D0, KeyModifiers.Shift | KeyModifiers.Alt);

    [ObservableProperty] private KeyChord _setFinger1 = new(Key.D1, KeyModifiers.Shift | KeyModifiers.Alt);
    [ObservableProperty] private KeyChord _setFinger2 = new(Key.D2, KeyModifiers.Shift | KeyModifiers.Alt);
    [ObservableProperty] private KeyChord _setFinger3 = new(Key.D3, KeyModifiers.Shift | KeyModifiers.Alt);
    [ObservableProperty] private KeyChord _setFinger4 = new(Key.D4, KeyModifiers.Shift | KeyModifiers.Alt);

    // --- File Ops ---
    [ObservableProperty] private KeyChord _newProject = new(Key.N, KeyModifiers.Control);
    [ObservableProperty] private KeyChord _openProject = new(Key.O, KeyModifiers.Control);
    [ObservableProperty] private KeyChord _saveProject = new(Key.S, KeyModifiers.Control);
    [ObservableProperty] private KeyChord _saveProjectAs = new(Key.S, KeyModifiers.Control | KeyModifiers.Shift);

    // --- Bookmarks (Shift+0..9 — base + modifier are user-configurable) ---
    [ObservableProperty] private KeyChord _bookmarkBase = new(Key.D0, KeyModifiers.Shift);

    // --- Set Fret (modifier + 0..9 — applies fret value to selected notes) ---
    [ObservableProperty] private KeyChord _setFretBase = new(Key.D0, KeyModifiers.Alt);

    // --- String-edit mask (modifier + 1..9 toggles a string; + 0 clears back to
    // "all strings"). rscharter's Ctrl+1..9 for the same selector; EOF spells it
    // SHIFT+Numpad 1..6 with SHIFT+Num0 to clear. Only the MODIFIERS of
    // StringMaskBase are honoured — the digit is derived, exactly like
    // BookmarkBase and SetFretBase. ---
    [ObservableProperty] private KeyChord _stringMaskBase = new(Key.D1, KeyModifiers.Control);
    [ObservableProperty] private KeyChord _stringMaskClear = new(Key.D0, KeyModifiers.Control);

    // --- The keyboard pen (v0.35, editing rework stage 2): modifier + 1..9
    // toggles a string into the sound at the PLAYHEAD — create a note, add it to
    // the chord already there, remove it, delete the sound when the last string
    // goes. EOF's Feedback-mode lane keys (editor.c:3884-3932) and rscharter's
    // Insert+1..9. Only the MODIFIERS of PenToggleStringBase are honoured — the
    // digit is derived, exactly like BookmarkBase / SetFretBase / StringMaskBase.
    //
    // BARE digits, because the whole digit row is otherwise spoken for
    // (Shift = bookmarks, Alt = set fret, Ctrl = the string mask, Shift+Alt =
    // fingering, and Ctrl+Alt IS AltGr on a German layout) — and because bare
    // home-row keys were already reserved for note entry when the phrase catalog
    // deliberately left EOF's W / E unbound. The typed-fret half of the pen
    // therefore rides SetFretBase (Alt+digit), which is EOF's split too:
    // bare digits pick the lane, modifier+digit types the fret.
    //
    // INERT while AppConfig.UseFastChartingInput is off, which is the default —
    // registered so the row is rebindable and collision-checked, not so it
    // fires. ---
    [ObservableProperty] private KeyChord _penToggleStringBase = new(Key.D1);

    // --- Named phrase catalog ---
    // Defaults follow Editor-on-Fire's fret-catalog keys (menu/song.c:122-136,
    // menu/edit.c:35-36) where they do not collide with an existing binding:
    // Q shows the catalog, SHIFT+W adds the selection, SHIFT+C pastes, SHIFT+~
    // pastes by typed name. EOF's W / E (previous / next entry) are deliberately
    // NOT bound — bare letter keys on the home row are reserved for note entry,
    // and the pane's list already answers arrow keys.
    [ObservableProperty] private KeyChord _togglePhraseCatalog = new(Key.Q);
    [ObservableProperty] private KeyChord _phraseCatalogAdd = new(Key.W, KeyModifiers.Shift);
    [ObservableProperty] private KeyChord _phraseCatalogPaste = new(Key.C, KeyModifiers.Shift);
    [ObservableProperty] private KeyChord _phraseCatalogPasteByName = new(Key.OemTilde, KeyModifiers.Shift);

    public void ResetToDefault()
    {
        // Re-create every property at its default value so a Reset wipes any
        // user customization, even if the user remapped fields to weird chords.
        var fresh = new KeybindConfig();
        foreach (var prop in typeof(KeybindConfig).GetProperties())
        {
            if (prop.CanRead && prop.CanWrite && prop.PropertyType == typeof(KeyChord))
                prop.SetValue(this, prop.GetValue(fresh));
        }
    }
}
