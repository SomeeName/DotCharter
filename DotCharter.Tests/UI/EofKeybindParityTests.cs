using Avalonia.Input;

namespace DotCharter.Tests.UI;

/// <summary>
///     The chords DotCharter shares with EOF, pinned to the verb EOF spells them
///     for.
///     <para>
///         Owner, 2026-08-22: "I want the keybinds to generally be just like EOF
///         has so it's familiar." The whole value of that is muscle memory, so a
///         later change that quietly re-homes one of these takes the familiarity
///         away without anything failing. Each row here names the EOF menu entry
///         it came from — the accelerators are in <c>EOF_SRC/menu/*.c</c>, which
///         is a local-only reference checkout, so they are transcribed rather
///         than read at test time.
///     </para>
///     <para>
///         This is deliberately a list of the ones that MATCH, not of every EOF
///         key. EOF binds verbs DotCharter does not have (Toggle Anchor on A,
///         Place RS Phrase on Shift+P, Select Rest on Shift+End) and DotCharter
///         binds verbs EOF does not (the pen, the string-edit mask, Play Mode);
///         inventing a verb to fill a row would be the tail wagging the dog.
///     </para>
/// </summary>
public sealed class EofKeybindParityTests
{
    public static TheoryData<string, Key, KeyModifiers> EofChords() => new()
    {
        // Transport / navigation — EOF_SRC/menu/song.c
        { nameof(KeybindConfig.SeekStart), Key.Home, KeyModifiers.None },           // Start
        { nameof(KeybindConfig.SeekEnd), Key.End, KeyModifiers.None },              // End of audio
        { nameof(KeybindConfig.SeekPrevBeat), Key.PageUp, KeyModifiers.None },      // Previous Beat
        { nameof(KeybindConfig.SeekNextBeat), Key.PageDown, KeyModifiers.None },    // Next Beat
        { nameof(KeybindConfig.SeekPrevAnchor), Key.PageUp, KeyModifiers.Alt },     // Previous Anchor
        { nameof(KeybindConfig.SeekNextAnchor), Key.PageDown, KeyModifiers.Alt },   // Next Anchor
        { nameof(KeybindConfig.SeekPrev), Key.PageUp, KeyModifiers.Shift },         // Previous (note)
        { nameof(KeybindConfig.SeekNext), Key.PageDown, KeyModifiers.Shift },       // Next (note)
        { nameof(KeybindConfig.QueueNextMatch), Key.Y, KeyModifiers.Shift },        // Next h.l.
        { nameof(KeybindConfig.Rewind), Key.R, KeyModifiers.None },                 // Rewind
        { nameof(KeybindConfig.SetLoopStart), Key.Home, KeyModifiers.Alt },         // Set start point
        { nameof(KeybindConfig.SetLoopEnd), Key.End, KeyModifiers.Alt },            // Set end point

        // Playback aids — EOF_SRC/menu/edit.c
        { nameof(KeybindConfig.ToggleMetronome), Key.M, KeyModifiers.None },        // Metronome
        { nameof(KeybindConfig.ToggleClaps), Key.C, KeyModifiers.None },            // Claps
        { nameof(KeybindConfig.ToggleChartSynth), Key.T, KeyModifiers.Shift },      // MIDI Tones (edit.c:227, #574)
        { nameof(KeybindConfig.ToggleWaveform), Key.F5, KeyModifiers.None },        // Show (waveform)

        // Note techniques — EOF_SRC/menu/note.c
        { nameof(KeybindConfig.ToggleHopo), Key.H, KeyModifiers.None },             // Cycle On/Off/Auto
        { nameof(KeybindConfig.ToggleHarmonic), Key.H, KeyModifiers.Shift },        // Pinch Harmonic
        { nameof(KeybindConfig.ToggleMute), Key.X, KeyModifiers.Shift },            // Toggle string mute
        { nameof(KeybindConfig.ToggleVibrato), Key.V, KeyModifiers.Shift },         // Toggle vibrato
        { nameof(KeybindConfig.ToggleLinkNext), Key.N, KeyModifiers.Shift },        // Linknext
        { nameof(KeybindConfig.StringUp), Key.Up, KeyModifiers.None },              // Transpose Up
        { nameof(KeybindConfig.StringDown), Key.Down, KeyModifiers.None },          // Transpose Down
        { nameof(KeybindConfig.FretUp), Key.Up, KeyModifiers.Shift },               // Pitched transpose Up
        { nameof(KeybindConfig.FretDown), Key.Down, KeyModifiers.Shift },           // Pitched transpose Down
        { nameof(KeybindConfig.Delete), Key.Delete, KeyModifiers.None },            // Delete
        { nameof(KeybindConfig.DeleteRelated), Key.Delete, KeyModifiers.Shift },    // Delete w/ FHP
        { nameof(KeybindConfig.EditFhp), Key.F, KeyModifiers.Shift },               // Set (fret hand position)

        // Selection & snapping — EOF_SRC/menu/edit.c
        { nameof(KeybindConfig.SelectLike), Key.L, KeyModifiers.Shift },            // Precise select like
        { nameof(KeybindConfig.SnapSelected), Key.R, KeyModifiers.Shift },          // Resnap to this grid
        { nameof(KeybindConfig.SnapAll), Key.R, KeyModifiers.Alt },                 // Resnap Auto
        { nameof(KeybindConfig.PasteAtMouse), Key.Insert, KeyModifiers.Shift },     // Paste at mouse

        // Beat / structure — EOF_SRC/menu/beat.c
        { nameof(KeybindConfig.StampSection), Key.S, KeyModifiers.Shift },          // Place RS Section

        // Catalog — EOF_SRC/menu/edit.c
        { nameof(KeybindConfig.TogglePhraseCatalog), Key.Q, KeyModifiers.None },    // Show
        { nameof(KeybindConfig.PhraseCatalogAdd), Key.W, KeyModifiers.Shift },      // Add
        { nameof(KeybindConfig.PhraseCatalogPaste), Key.C, KeyModifiers.Shift },    // Catalog
        { nameof(KeybindConfig.PhraseCatalogPasteByName), Key.OemTilde, KeyModifiers.Shift }, // Catalog entry Name

        // Views — EOF_SRC/menu/*.c
        { nameof(KeybindConfig.Toggle3DFingering), Key.F8, KeyModifiers.None },     // Fingering view
        { nameof(KeybindConfig.ToggleInspector), Key.F9, KeyModifiers.None },       // Properties
    };

    [Theory]
    [MemberData(nameof(EofChords))]
    public void ADefaultDotCharterSharesWithEof_StaysWhereEofPutsIt(string property, Key key, KeyModifiers modifiers)
    {
        var config = new KeybindConfig();
        var value = typeof(KeybindConfig).GetProperty(property)?.GetValue(config);

        Assert.True(value is KeyChord, $"{property} is not a KeyChord property — repoint this row.");
        Assert.Equal(new KeyChord(key, modifiers), (KeyChord)value!);
    }

    /// <summary>
    ///     The 2026-09-04 alignment pass (the third), kept in its OWN theory so
    ///     each row can carry the exact EOF source literal it was read from. The
    ///     rows above were transcribed by earlier passes and cite the menu entry;
    ///     these cite file and line, because every one of them MOVED a chord a
    ///     charter may already have in their fingers and the next agent must be
    ///     able to check the claim rather than take it.
    /// </summary>
    public static TheoryData<string, Key, KeyModifiers, string> Pass3EofChords() => new()
    {
        // EOF_SRC/editor.c:2064 —
        //   if((eof_key_code == KEY_STOP) && (KEY_EITHER_ALT))
        //       (void) eof_menu_edit_zoom_helper_in();
        // KEY_STOP is the period key. The BARE row of the same key is EOF's
        // "increase grid snap", which DotCharter already spells DoubleGrid.
        { nameof(KeybindConfig.ZoomIn), Key.OemPeriod, KeyModifiers.Alt, "editor.c:2064 ALT+. zoom in" },

        // EOF_SRC/editor.c:2035 —
        //   if((eof_key_code == KEY_COMMA) && KEY_EITHER_ALT)
        //       (void) eof_menu_edit_zoom_helper_out();
        { nameof(KeybindConfig.ZoomOut), Key.OemComma, KeyModifiers.Alt, "editor.c:2035 ALT+, zoom out" },

        // EOF_SRC/menu/beat.c:144 — {"&Add	" CTRL_NAME "+Ins", eof_menu_beat_add
        { nameof(KeybindConfig.InsertBeatAtPlayhead), Key.Insert, KeyModifiers.Control, "beat.c:144 Ctrl+Ins Add" },

        // EOF_SRC/menu/beat.c:145 — {"Delete	" CTRL_NAME "+Del", eof_menu_beat_delete
        { nameof(KeybindConfig.RemoveBeatAtPlayhead), Key.Delete, KeyModifiers.Control, "beat.c:145 Ctrl+Del Delete" },

        // EOF_SRC/menu/beat.c:77 — {"&Selected	" CTRL_NAME "+Shift+D", eof_menu_beat_double_tempo
        { nameof(KeybindConfig.DoubleTime), Key.D, KeyModifiers.Control | KeyModifiers.Shift,
            "beat.c:77 Ctrl+Shift+D Double tempo" },

        // EOF_SRC/menu/beat.c:84 — {"&Selected	" CTRL_NAME "+Shift+X", eof_menu_beat_halve_tempo
        { nameof(KeybindConfig.HalfTime), Key.X, KeyModifiers.Control | KeyModifiers.Shift,
            "beat.c:84 Ctrl+Shift+X Halve tempo" },

        // EOF_SRC/menu/note.c:722 —
        //   {"&Ignore	" CTRL_NAME "+Shift+I", eof_menu_note_toggle_rs_ignore
        // The same Rocksmith "ignore" status DotCharter's verb sets, not an
        // approximation of it.
        { nameof(KeybindConfig.ToggleIgnore), Key.I, KeyModifiers.Control | KeyModifiers.Shift,
            "note.c:722 Ctrl+Shift+I Ignore" }
    };

    [Theory]
    [MemberData(nameof(Pass3EofChords))]
    public void AnEofAlignedDefault_StaysOnTheChordEofSpellsForIt(
        string property, Key key, KeyModifiers modifiers, string eofCitation)
    {
        var actual = (KeyChord)typeof(KeybindConfig).GetProperty(property)!.GetValue(new KeybindConfig())!;

        Assert.Equal(new KeyChord(key, modifiers), actual);
        Assert.False(string.IsNullOrWhiteSpace(eofCitation), $"{property} lost its EOF citation");
    }

    /// <summary>
    ///     A chord an alignment pass VACATED must stay vacant unless a verb EOF
    ///     spells for it arrives. Ctrl+[ / Ctrl+] are EOF's "move notes
    ///     backward/forward one grid snap" (<c>EOF_SRC/menu/note.c:822-823</c>)
    ///     and DotCharter has no such verb — refilling them with something else
    ///     is exactly the drift these tests exist to catch, and it would look
    ///     like tidiness rather than a mistake.
    /// </summary>
    [Theory]
    [InlineData(Key.OemOpenBrackets, KeyModifiers.Control)]
    [InlineData(Key.OemCloseBrackets, KeyModifiers.Control)]
    // Ctrl+B is EOF's "Toggle Blue cymbal" (menu/note.c:520), a drum verb
    // DotCharter has no counterpart for; Ctrl+Shift+B is its "Seek
    // Beat/Measure" (menu/song.c:97), which DotCharter reaches through the
    // PgUp/PgDn beat family instead.
    [InlineData(Key.B, KeyModifiers.Control)]
    [InlineData(Key.B, KeyModifiers.Control | KeyModifiers.Shift)]
    // Bare G is unbound in EOF entirely; it kept DotCharter's ignore toggle
    // until the 2026-09-04 pass moved that onto EOF's Ctrl+Shift+I.
    [InlineData(Key.G, KeyModifiers.None)]
    public void AChordVacatedForEof_IsNotRefilled(Key key, KeyModifiers modifiers)
    {
        var config = new KeybindConfig();
        var chord = new KeyChord(key, modifiers);
        var claimant = typeof(KeybindConfig).GetProperties()
            .Where(p => p.PropertyType == typeof(KeyChord))
            .FirstOrDefault(p => Equals(p.GetValue(config), chord));

        Assert.True(claimant == null,
            $"{claimant?.Name} took {chord}, which an EOF alignment pass deliberately left free.");
    }

    /// <summary>
    ///     The list is a real cross-section, not a handful. If it collapses,
    ///     something deleted rows rather than fixing a mismatch.
    /// </summary>
    [Fact]
    public void TheParityListCoversTheWholeChartingSurface() =>
        Assert.True(EofChords().Count >= 35, $"only {EofChords().Count} EOF chords pinned");

    /// <summary>
    ///     Every pinned property really exists on the config. A typo'd
    ///     <c>nameof</c> cannot happen, but a property REMOVED in a refactor
    ///     would leave the row silently untested if the lookup were tolerant —
    ///     the row above asserts on it, so this only has to prove the reflection
    ///     path is real rather than always-null.
    /// </summary>
    [Fact]
    public void EveryPinnedPropertyResolves()
    {
        var config = new KeybindConfig();
        foreach (var name in EofChords().Select(row => row.Data.Item1))
        {
            Assert.NotNull(typeof(KeybindConfig).GetProperty(name));
            Assert.IsType<KeyChord>(typeof(KeybindConfig).GetProperty(name)!.GetValue(config));
        }
    }
}
