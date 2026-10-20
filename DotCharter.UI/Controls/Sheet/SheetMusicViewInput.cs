using Avalonia.Controls;
using Avalonia.Input;

// SheetMusicView's input: the pointer gestures on the score (press seeks + selects, drag
// scrubs, double-click inserts, move hovers) and the GP-style keys (digits type a fret,
// +/- step the note value). It holds the gesture state; the view keeps the hit-testing.
namespace DotCharter.UI.Controls.Sheet;

/// <summary>
/// The sheet's pointer and keyboard gestures, turned into calls on the view. Built once by
/// <see cref="SheetMusicView" />, which hands it the hit-testable scroll content (the
/// surface every pointer handler hangs on), the canvas whose coordinates the gestures
/// are read in, and the hover overlay a pointer exit clears.
/// </summary>
internal sealed class SheetMusicViewInput
{
    private readonly SheetMusicView _view;
    private bool _isScrubbing;
    private long _lastScrubSeekTicks;

    // GP-style fret typing: digits accumulate while they arrive within the window.
    // The multi-digit fret typist is the shared, unit-tested
    // DotCharter.Services.Editing.Pen.FretDigitAccumulator — the SAME one the
    // keyboard pen uses, so "1 then 2 = fret 12" cannot drift between the two
    // surfaces. The sheet keeps its own (shorter) window: see the type's doc.
    private FretDigitAccumulator _fretDigits = FretDigitAccumulator.Empty;

    public SheetMusicViewInput(SheetMusicView view, Control surface, Control canvas, HoverOverlay hoverOverlay)
    {
        _view = view;

        // Click / drag on the sheet scrubs the transport, mirroring the
        // minimap contract: immediate seek on press, ~60 Hz coalesced seeks
        // while dragging, no extra seek on release. A DOUBLE-click is the
        // note-insert gesture (GP-style); the first click already sought and
        // selected, so the second only raises AddNoteRequested.
        surface.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(surface).Properties.IsLeftButtonPressed) return;
            // The strip owns keyboard gestures (digit fret entry) once clicked;
            // global keybinds (Entf, arrows) still bubble to the window routes.
            _view.Focus();
            var p = e.GetPosition(canvas);
            if (e.ClickCount == 2)
            {
                _isScrubbing = false;
                if (_view.TryAddAtPointer(p.X, p.Y)) { e.Handled = true; return; }
            }
            _isScrubbing = true;
            _lastScrubSeekTicks = Environment.TickCount64;
            e.Pointer.Capture(surface);
            _view.SeekToPointer(p.X, p.Y);
            // A press both seeks (scrub start) AND selects — exact glyph when
            // the click lands on one, nearest-in-time otherwise. Ctrl adds to /
            // toggles the selection (GP-style multi-select) instead of replacing.
            // Drag (PointerMoved) only seeks.
            var additive = e.KeyModifiers.HasFlag(KeyModifiers.Control);
            _view.SelectAtPointer(p.X, p.Y, additive);
            e.Handled = true;
        };
        surface.PointerMoved += (_, e) =>
        {
            var p = e.GetPosition(canvas);
            if (_isScrubbing)
            {
                var now = Environment.TickCount64;
                if (now - _lastScrubSeekTicks < 16) { e.Handled = true; return; }
                _lastScrubSeekTicks = now;
                _view.SeekToPointer(p.X, p.Y);
                e.Handled = true;
                return;
            }
            // Not pressed: hover-highlight the exact note head / tab digit under
            // the cursor. Cheap — only the thin hover overlay repaints, and only
            // when the hovered cell actually changed.
            _view.UpdateHover(p.X, p.Y);
        };
        surface.PointerReleased += (_, e) =>
        {
            _isScrubbing = false;
            e.Pointer.Capture(null);
        };
        surface.PointerCaptureLost += (_, _) => _isScrubbing = false;
        surface.PointerExited += (_, _) => hoverOverlay.SetHover(null);
    }

    /// <summary>
    /// GP-style fret typing: plain digits set the selected notes' fret; a
    /// second digit arriving within the timeout extends the value (1 → 12)
    /// when the result is still a real fret, otherwise it starts over.
    /// Returns true when the key was handled (and marks it so); false lets the
    /// view pass it on to its base handler.
    /// </summary>
    public bool TryHandleKey(KeyEventArgs e)
    {
        // GP-style +/- halve/double the selected note value. Handled ONLY when a
        // sound is selected, so with an empty selection the keys keep bubbling to
        // the window's global playback-speed binding (OemPlus/OemMinus/Num±).
        // The sheet contains no TextBox, so this control-level handler never
        // fires over a text input. Deliberately shadows the global +/- while the
        // sheet has focus + a selection — documented in AppKeybindRegistrar.
        if (e.KeyModifiers == KeyModifiers.None &&
            TryGetDurationStep(e.Key, out var step) &&
            _view.Selection is { } dsel && (dsel.SelectedNotes.Count > 0 || dsel.SelectedChords.Count > 0))
        {
            _view.RaiseDurationStep(step);
            e.Handled = true;
            return true;
        }

        if (e.KeyModifiers == KeyModifiers.None &&
            TryGetDigit(e.Key, out var digit) &&
            _view.Selection is { } sel && (sel.SelectedNotes.Count > 0 || sel.SelectedChords.Count > 0))
        {
            _fretDigits = _fretDigits.Push(digit, Environment.TickCount64,
                FretDigitAccumulator.SheetWindowMs, FretDigitAccumulator.MaxTypedFret, out var candidate);
            _view.RaiseSetFret(candidate);
            e.Handled = true;
            return true;
        }

        return false;
    }

    /// <summary>+1 = longer (OemPlus / numpad Add), −1 = shorter (OemMinus /
    /// numpad Subtract). 0 (no match) reported as false.</summary>
    internal static bool TryGetDurationStep(Key key, out int step)
    {
        step = key switch
        {
            Key.OemPlus or Key.Add => 1,
            Key.OemMinus or Key.Subtract => -1,
            _ => 0
        };
        return step != 0;
    }

    internal static bool TryGetDigit(Key key, out int digit)
    {
        digit = key switch
        {
            >= Key.D0 and <= Key.D9 => key - Key.D0,
            >= Key.NumPad0 and <= Key.NumPad9 => key - Key.NumPad0,
            _ => -1
        };
        return digit >= 0;
    }
}
