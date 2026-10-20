using System.ComponentModel;
using System.Windows.Input;
using DotCharter.Services.Infrastructure.Configuration;

namespace DotCharter.Presentation.Contracts.Settings;

/// <summary>
///     Settings ▸ Keybinds: every rebindable chord of the editor, grouped the
///     way the registrar groups its verbs. Rows are data — one template draws
///     all of them — so the cross-tab search filters by flipping
///     <see cref="IKeybindRow.IsVisible" />.
///     <para>
/// The SAME rows also bind a MIDI controller: in MIDI mode each
///         row's pill shows and learns a <see cref="MidiTrigger" /> instead of a
///         chord, persisted in the same file and saved by the same Save.
///     </para>
/// </summary>
public interface IKeybindsSettingsScreen : INotifyPropertyChanged
{
    IReadOnlyList<IKeybindGroup> Groups { get; }

    /// <summary>Puts every chord back to its factory default; Save persists it.</summary>
    ICommand ResetToDefaultsCommand { get; }

    /// <summary>The rows show and record key chords. Always the opposite of <see cref="IsMidiMode" />.</summary>
    bool IsKeyboardMode { get; set; }

    /// <summary>The rows show and learn MIDI triggers. A view state, not a setting: it is never saved.</summary>
    bool IsMidiMode { get; set; }

    /// <summary>Unbinds every MIDI trigger; Save persists it.</summary>
    ICommand ClearMidiBindingsCommand { get; }

    /// <summary>The MIDI input in words: the device listening, or why none is — and where to pick one.</summary>
    string MidiInputStatus { get; }

    /// <summary>
    ///     True while a MIDI device is open and its presses arrive. When false the
    ///     bindings stay editable, but <see cref="MidiInputStatus" /> is shown as a
    ///     warning: a trigger cannot be learned and a bound one fires nothing.
    /// </summary>
    bool IsMidiInputLive { get; }
}

public interface IKeybindGroup : INotifyPropertyChanged
{
    string Title { get; }

    string Subtitle { get; }

    /// <summary>The app resource key of the group badge's glyph.</summary>
    string IconKey { get; }

    IReadOnlyList<IKeybindRow> Rows { get; }

    /// <summary>False while a search query matches nothing in this group.</summary>
    bool IsVisible { get; }
}

public interface IKeybindRow : INotifyPropertyChanged
{
    string Label { get; }

    /// <summary>The full label, and where it differs a longer explanation, for the row's tooltip.</summary>
    string Tooltip { get; }

    /// <summary>The chord itself; the capture pill writes it back.</summary>
    KeyChord Chord { get; set; }

    /// <summary>False while a search query matches neither this row nor its group.</summary>
    bool IsVisible { get; }

    /// <summary>The tab is in MIDI mode: this row shows its MIDI pill, not its chord.</summary>
    bool ShowsMidi { get; }

    /// <summary>
    ///     The MIDI pill's text: the bound trigger (<c>C4 · ch 1</c>), a dash when
    ///     none is, the learn prompt while learning, and for a row a pad cannot
    ///     stand for (a digit family) the reason.
    /// </summary>
    string MidiText { get; }

    /// <summary>False for a digit-family row (Shift+0..9 is ten verbs; one pad cannot be all of them).</summary>
    bool IsMidiBindable { get; }

    bool IsMidiBound { get; }

    /// <summary>This row takes the next press from the controller.</summary>
    bool IsMidiLearning { get; }

    /// <summary>Starts learning on this row, or stops it when it already is.</summary>
    ICommand LearnMidiCommand { get; }

    ICommand ClearMidiCommand { get; }
}
