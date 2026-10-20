using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using DotCharter.UI.Controls.Themes;

namespace DotCharter.UI.Controls;

/// <summary>What a primary click on the chord box MEANS.</summary>
public enum ChordBoxRole
{
    /// <summary>Move the selected note to the clicked string/fret.</summary>
    MoveNote,

    /// <summary>Set or clear that string of the shape.</summary>
    Shape,

    /// <summary>Display only — the primary click does nothing and the control
    /// says why. Used by the Library block, where nothing is selected to
    /// reshape.</summary>
    ReadOnly
}

/// <summary>A hover-typed fret. <see cref="ContinuesRun" /> is true when the
/// digit EXTENDED an open accumulator run, so the consumer undoes the run's own
/// history node before committing — the rule the keyboard pen already uses.</summary>
public readonly record struct ChordBoxFretEdit(int StringIndex, int Fret, bool ContinuesRun);

/// <summary>A hover-typed finger. <c>0</c> = thumb, <c>1..4</c> = fingers,
/// <c>-1</c> = unspecified.</summary>
public readonly record struct ChordBoxFingerEdit(int StringIndex, int Finger);

/// <summary>
///     The inspector's ONE fretboard (was <c>MiniFretboardControl</c>, which the
///     Note tab, the Chord tab's string picker and the template editor all
///     instantiated with three different click meanings, two of them visually
///     identical and ~200 px apart).
///     <para>
///         Now: one instrument, one meaning per state. <see cref="Role" /> decides
///         what the primary click does; the per-string
///         <see cref="StringEditMask" /> is drawn on its own rail column and is
///         the SAME value the lane headers draw, so the inspector cannot
///         disagree with the keyboard about which strings an edit targets.
///     </para>
///     <para>
///         <b>Perf contract (kept from the original):</b> only invalidate when
///         the rendered state actually changed — pointer-move compares the hover
///         cell first. The <c>StyledProperty</c>s do NOT observe collection
///         mutation, so a view model must reassign <see cref="Chord" />,
///         <see cref="Template" />, <see cref="MutedStrings" /> and
///         <see cref="IssueStrings" /> as FRESH references.
///         <see cref="Mask" /> is an immutable value type for the same reason.
///     </para>
/// </summary>
public class ChordBoxControl : Control
{
    public static readonly StyledProperty<Chord?> ChordProperty =
        AvaloniaProperty.Register<ChordBoxControl, Chord?>(nameof(Chord));

    public static readonly StyledProperty<ChordTemplate?> TemplateProperty =
        AvaloniaProperty.Register<ChordBoxControl, ChordTemplate?>(nameof(Template));

    public static readonly StyledProperty<int> StringCountProperty =
        AvaloniaProperty.Register<ChordBoxControl, int>(nameof(StringCount), 6);

    public static readonly StyledProperty<int> CapoProperty =
        AvaloniaProperty.Register<ChordBoxControl, int>(nameof(Capo));

    /// <summary>
    ///     The viewport's own <c>LeftyFlip ^ InvertStrings</c>. It has to reach
    ///     this control or the flip toggle inverts the highway and leaves the
    ///     chord box pointing the other way — the two must always agree.
    /// </summary>
    public static readonly StyledProperty<bool> FlipStringsProperty =
        AvaloniaProperty.Register<ChordBoxControl, bool>(nameof(FlipStrings));

    public static readonly StyledProperty<IReadOnlyList<string>?> TuningNamesProperty =
        AvaloniaProperty.Register<ChordBoxControl, IReadOnlyList<string>?>(nameof(TuningNames));

    /// <summary>The primary click, meaning set by <see cref="Role" />. Payload is
    /// <c>(int stringIdx, int fret)</c>.</summary>
    public static readonly StyledProperty<ICommand?> ToggleNoteCommandProperty =
        AvaloniaProperty.Register<ChordBoxControl, ICommand?>(nameof(ToggleNoteCommand));

    public static readonly StyledProperty<ChordBoxRole> RoleProperty =
        AvaloniaProperty.Register<ChordBoxControl, ChordBoxRole>(nameof(Role));

    /// <summary>The persistent string-edit mask — the same value the lane
    /// headers draw. EMPTY means every string, which is why the rail renders
    /// "no mask set" rather than "all lit".</summary>
    public static readonly StyledProperty<StringEditMask> MaskProperty =
        AvaloniaProperty.Register<ChordBoxControl, StringEditMask>(nameof(Mask));

    /// <summary>Toggle one string in/out of the mask. Payload: the string index.</summary>
    public static readonly StyledProperty<ICommand?> ToggleMaskCommandProperty =
        AvaloniaProperty.Register<ChordBoxControl, ICommand?>(nameof(ToggleMaskCommand));

    /// <summary>Hover + <c>Shift</c>+<c>1</c>..<c>4</c> / <c>T</c>. Payload:
    /// <see cref="ChordBoxFingerEdit" />.</summary>
    public static readonly StyledProperty<ICommand?> SetFingerCommandProperty =
        AvaloniaProperty.Register<ChordBoxControl, ICommand?>(nameof(SetFingerCommand));

    /// <summary>Hover + digits (two-digit accumulator). Payload:
    /// <see cref="ChordBoxFretEdit" />.</summary>
    public static readonly StyledProperty<ICommand?> SetFretCommandProperty =
        AvaloniaProperty.Register<ChordBoxControl, ICommand?>(nameof(SetFretCommand));

    /// <summary>Hover + <c>Del</c>/<c>Backspace</c>, or right-click. Payload:
    /// the string index.</summary>
    public static readonly StyledProperty<ICommand?> ClearStringCommandProperty =
        AvaloniaProperty.Register<ChordBoxControl, ICommand?>(nameof(ClearStringCommand));

    /// <summary>Strings whose chord note is <c>MuteType.Full</c> — drawn as an
    /// ✕ on the rail. Reassign a fresh list to repaint.</summary>
    public static readonly StyledProperty<IReadOnlyList<int>?> MutedStringsProperty =
        AvaloniaProperty.Register<ChordBoxControl, IReadOnlyList<int>?>(nameof(MutedStrings));

    /// <summary>Strings carrying a validation finding, with whether it blocks the
    /// export. Drawn as a severity pip at the end of the string line.</summary>
    public static readonly StyledProperty<IReadOnlyList<ChordBoxIssuePip>?> IssueStringsProperty =
        AvaloniaProperty.Register<ChordBoxControl, IReadOnlyList<ChordBoxIssuePip>?>(nameof(IssueStrings));

    private int _displayMaxFret = 5;
    private int _displayMinFret = 1;
    private int _hoveredFret = -1;
    private int _hoveredString = -1;
    private bool _hoveredOverPip;

    /// <summary>The two-digit fret typist. The SHEET window (800 ms), not the
    /// pen's 2 s: a hover-scoped edit is a deliberate, short gesture and the
    /// pointer can leave the string at any moment.</summary>
    private FretDigitAccumulator _fretDigits = FretDigitAccumulator.Empty;
    private int _fretDigitsString = -1;

    // Hoist allocations out of the render path. ChartTheme.*Brush instances are
    // frozen singletons, so building these pens once is safe; Render runs on
    // every layout/invalidate pass and per-call Pen/Brush allocations were one
    // of the dominant alloc sources during inspector selection churn.
    private static readonly Pen NutPenSlim = new(ChartTheme.WhiteBrush, 4);
    private static readonly Pen NutPenCapo = new(ChartTheme.WhiteBrush, 6);
    private static readonly Pen FretPen = new(ChartTheme.MutedNoteBrush, 2);
    private static readonly Pen MutePen = new(ChartTheme.TopMarginBgBrush, 2);
    private static readonly IBrush MaskedStringBrush = new SolidColorBrush(Color.FromArgb(70, 90, 200, 255));
    private static readonly IBrush MaskPipOnBrush = new SolidColorBrush(Color.FromArgb(255, 90, 200, 255));
    private static readonly IBrush MaskPipOffBrush = new SolidColorBrush(Color.FromArgb(70, 190, 190, 200));
    private static readonly IBrush HoverDotBrush = new SolidColorBrush(Color.FromArgb(100, 255, 255, 255));
    /// <summary>The finger number on a pip: white, which reads on the red, blue,
    /// orange and purple strings. Plain: an outline built from offset
    /// copies smeared the digit at this size. Immutable, so a test on any thread can
    /// read its colour.</summary>
    internal static readonly ISolidColorBrush FingerTextBrush = new ImmutableSolidColorBrush(Colors.White);

    private static readonly IBrush MuteMarkBrush = new SolidColorBrush(Color.FromArgb(220, 235, 120, 120));
    private static readonly Pen MuteMarkPen = new(MuteMarkBrush, 2);
    private static readonly IBrush IssueBlockingBrush = new SolidColorBrush(Color.FromArgb(255, 232, 82, 82));
    private static readonly IBrush IssueWarnBrush = new SolidColorBrush(Color.FromArgb(255, 232, 176, 74));
    private static readonly Pen[] StringPens = ChartTheme.StringBrushes
        .Select(b => new Pen(b, 1.5)).ToArray();
    private static readonly Pen[] StringNotePens = ChartTheme.StringBrushes
        .Select(b => new Pen(b, 2)).ToArray();

    public ChordBoxControl()
    {
        ClipToBounds = true;
        // Hover-typing needs key events, and key events need focus. Focus is
        // taken on pointer ENTER (see OnPointerEntered) and only when no text
        // input owns it, so typing in a name/numeric field is never stolen.
        Focusable = true;
    }

    public Chord? Chord
    {
        get => GetValue(ChordProperty);
        set => SetValue(ChordProperty, value);
    }

    public ChordTemplate? Template
    {
        get => GetValue(TemplateProperty);
        set => SetValue(TemplateProperty, value);
    }

    public int StringCount
    {
        get => GetValue(StringCountProperty);
        set => SetValue(StringCountProperty, value);
    }

    public bool FlipStrings
    {
        get => GetValue(FlipStringsProperty);
        set => SetValue(FlipStringsProperty, value);
    }

    public int Capo
    {
        get => GetValue(CapoProperty);
        set => SetValue(CapoProperty, value);
    }

    public IReadOnlyList<string>? TuningNames
    {
        get => GetValue(TuningNamesProperty);
        set => SetValue(TuningNamesProperty, value);
    }

    public ICommand? ToggleNoteCommand
    {
        get => GetValue(ToggleNoteCommandProperty);
        set => SetValue(ToggleNoteCommandProperty, value);
    }

    public ChordBoxRole Role
    {
        get => GetValue(RoleProperty);
        set => SetValue(RoleProperty, value);
    }

    public StringEditMask Mask
    {
        get => GetValue(MaskProperty);
        set => SetValue(MaskProperty, value);
    }

    public ICommand? ToggleMaskCommand
    {
        get => GetValue(ToggleMaskCommandProperty);
        set => SetValue(ToggleMaskCommandProperty, value);
    }

    public ICommand? SetFingerCommand
    {
        get => GetValue(SetFingerCommandProperty);
        set => SetValue(SetFingerCommandProperty, value);
    }

    public ICommand? SetFretCommand
    {
        get => GetValue(SetFretCommandProperty);
        set => SetValue(SetFretCommandProperty, value);
    }

    public ICommand? ClearStringCommand
    {
        get => GetValue(ClearStringCommandProperty);
        set => SetValue(ClearStringCommandProperty, value);
    }

    public IReadOnlyList<int>? MutedStrings
    {
        get => GetValue(MutedStringsProperty);
        set => SetValue(MutedStringsProperty, value);
    }

    public IReadOnlyList<ChordBoxIssuePip>? IssueStrings
    {
        get => GetValue(IssueStringsProperty);
        set => SetValue(IssueStringsProperty, value);
    }

    private ChordBoxGeometry Geometry => new(Bounds.Width, Bounds.Height, StringCount,
        _displayMinFret, _displayMaxFret, FlipStrings);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == FlipStringsProperty) InvalidateVisual();

        if (change.Property == ChordProperty || change.Property == StringCountProperty ||
            change.Property == CapoProperty)
        {
            UpdateDisplayFrets();
            InvalidateVisual();
            return;
        }

        if (change.Property == TemplateProperty || change.Property == TuningNamesProperty ||
            change.Property == MaskProperty || change.Property == MutedStringsProperty ||
            change.Property == IssueStringsProperty || change.Property == RoleProperty)
        {
            InvalidateVisual();
        }
    }

    private void UpdateDisplayFrets()
    {
        var frets = Chord?.ChordNotes.Select(n => n.Fret);
        (_displayMinFret, _displayMaxFret) = ChordBoxGeometry.DisplayFrets(frets, Capo);
    }

    // ---- pointer -----------------------------------------------------------

    protected override void OnPointerEntered(PointerEventArgs e)
    {
        base.OnPointerEntered(e);
        // Never steal focus from a text input — the hover gestures are typed,
        // and the app-wide rule is that no keyboard route fires while a TextBox
        // or a KeybindCaptureBox owns focus.
        if (IsFocused) return;
        var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
        if (focused is TextBox or AutoCompleteBox) return;
        Focus(NavigationMethod.Pointer);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = e.GetPosition(this);
        var prevString = _hoveredString;
        var prevFret = _hoveredFret;
        var prevPip = _hoveredOverPip;

        Geometry.TryHit(p.X, p.Y, Capo, out _hoveredString, out _hoveredFret);
        _hoveredOverPip = _hoveredString >= 0 && ChordBoxGeometry.IsOverMaskPip(p.X);

        // Only repaint when the hovered cell actually changed — pointer-move
        // fires per input event and unconditional invalidation floods the
        // render loop while the cursor glides inside one cell.
        if (_hoveredString != prevString || _hoveredFret != prevFret || _hoveredOverPip != prevPip)
        {
            if (_hoveredString != prevString) EndFretDigitRun();
            InvalidateVisual();
        }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        EndFretDigitRun();
        if (_hoveredString == -1 && _hoveredFret == -1 && !_hoveredOverPip) return;
        _hoveredString = -1;
        _hoveredFret = -1;
        _hoveredOverPip = false;
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var props = e.GetCurrentPoint(this).Properties;
        var p = e.GetPosition(this);
        if (!Geometry.TryHit(p.X, p.Y, Capo, out var stringIdx, out var fret)) return;

        // Right-click clears the string from the shape.
        if (props.IsRightButtonPressed)
        {
            if (Role == ChordBoxRole.Shape) Execute(ClearStringCommand, stringIdx);
            return;
        }

        if (!props.IsLeftButtonPressed) return;

        // The mask is toggled from the pip column or with Ctrl — in EVERY role,
        // including ReadOnly. It is editor state, not chart state.
        if (ChordBoxGeometry.IsOverMaskPip(p.X) || e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            Execute(ToggleMaskCommand, stringIdx);
            return;
        }

        if (Role == ChordBoxRole.ReadOnly) return;

        EndFretDigitRun();
        Execute(ToggleNoteCommand, (stringIdx, fret));
    }

    // ---- keyboard (hover-scoped) -------------------------------------------

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || _hoveredString < 0 || Role == ChordBoxRole.ReadOnly) return;

        // Thumb + fingers. Shift is the finger modifier because bare 1..4 are
        // frets and a chord box has to spell both: it mirrors the app's global
        // Shift+Alt fingering family, and Key.D1 + Shift is layout-independent
        // (Shift+1 is "!" on a German keyboard, but the Key is still D1).
        if (Role == ChordBoxRole.Shape)
        {
            var finger = FingerFor(e.Key, e.KeyModifiers);
            if (finger.HasValue)
            {
                EndFretDigitRun();
                if (Execute(SetFingerCommand, new ChordBoxFingerEdit(_hoveredString, finger.Value)))
                    e.Handled = true;
                return;
            }

            if (e.Key is Key.Delete or Key.Back)
            {
                EndFretDigitRun();
                if (Execute(ClearStringCommand, _hoveredString)) e.Handled = true;
                return;
            }
        }

        // Bare digits only: Shift belongs to the finger family above and Ctrl to
        // the mask, so neither may fall through into a fret.
        var digit = DigitFor(e.Key);
        if (digit < 0 || e.KeyModifiers != KeyModifiers.None) return;

        var continues = _fretDigits.IsAccumulating && _fretDigitsString == _hoveredString;
        _fretDigits = _fretDigits.Push(digit, Environment.TickCount64,
            FretDigitAccumulator.SheetWindowMs, FretDigitAccumulator.MaxTypedFret, out var typedFret);
        _fretDigitsString = _hoveredString;

        if (Execute(SetFretCommand, new ChordBoxFretEdit(_hoveredString, typedFret, continues)))
            e.Handled = true;
    }

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        EndFretDigitRun();
    }

    private void EndFretDigitRun()
    {
        _fretDigits = FretDigitAccumulator.Empty;
        _fretDigitsString = -1;
    }

    /// <summary>Digit key → <c>0..9</c>, or <c>-1</c>. Both the number row and
    /// the numpad.</summary>
    internal static int DigitFor(Key key) => key switch
    {
        >= Key.D0 and <= Key.D9 => key - Key.D0,
        >= Key.NumPad0 and <= Key.NumPad9 => key - Key.NumPad0,
        _ => -1
    };

    /// <summary>Finger key → <c>0</c> (thumb) or <c>1..4</c>, else null.</summary>
    internal static int? FingerFor(Key key, KeyModifiers modifiers)
    {
        if (key == Key.T && modifiers == KeyModifiers.None) return 0;
        if (modifiers != KeyModifiers.Shift) return null;
        return key switch
        {
            >= Key.D1 and <= Key.D4 => key - Key.D0,
            >= Key.NumPad1 and <= Key.NumPad4 => key - Key.NumPad0,
            _ => null
        };
    }

    private static bool Execute(ICommand? command, object? parameter)
    {
        if (command?.CanExecute(parameter) != true) return false;
        command.Execute(parameter);
        return true;
    }

    // ---- render ------------------------------------------------------------

    public override void Render(DrawingContext context)
    {
        var bounds = Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0) return;

        context.FillRectangle(ChartTheme.BottomMarginBgBrush, bounds);

        var g = Geometry;
        var stringSpacing = g.StringSpacing;
        var maskEmpty = Mask.IsEmpty;

        // Nut / capo.
        context.DrawLine(Capo > 0 ? NutPenCapo : NutPenSlim,
            new Point(ChordBoxGeometry.FretboardStartX, ChordBoxGeometry.TopPadding),
            new Point(ChordBoxGeometry.FretboardStartX, bounds.Height - ChordBoxGeometry.BottomPadding));

        if (Capo > 0)
        {
            var capoText = ChartTheme.GetCachedText($"CAPO {Capo}", 10, ChartTheme.WhiteBrush, true);
            context.DrawText(capoText,
                new Point(ChordBoxGeometry.FretboardStartX - capoText.Width / 2,
                    ChordBoxGeometry.TopPadding - 16));
        }

        // Fret wires + numbers.
        for (var i = 1; i <= g.FretCount; i++)
        {
            var fx = ChordBoxGeometry.FretboardStartX + i * g.FretSpacing;
            context.DrawLine(FretPen, new Point(fx, ChordBoxGeometry.TopPadding),
                new Point(fx, bounds.Height - ChordBoxGeometry.BottomPadding));

            var fretText = ChartTheme.GetCachedText((_displayMinFret + i - 1).ToString(), 12,
                ChartTheme.WhiteBrush, true);
            context.DrawText(fretText,
                new Point(fx - g.FretSpacing / 2 - fretText.Width / 2,
                    bounds.Height - ChordBoxGeometry.BottomPadding + 6));
        }

        var stringBrushes = ChartTheme.StringBrushes;
        for (var i = 0; i < StringCount; i++)
        {
            var sy = g.StringY(i);
            var sPen = StringPens[ChartTheme.StringSlot(i, StringCount)];
            context.DrawLine(sPen, new Point(ChordBoxGeometry.FretboardStartX, sy),
                new Point(ChordBoxGeometry.FretboardStartX + g.FretboardWidth, sy));

            // Mask pip. An empty mask draws every pip DIM, never every pip lit —
            // "no mask set" and "all nine explicitly masked" must not look the
            // same, which is the same split StringEditMask.IsExplicit encodes.
            var explicitlyMasked = Mask.IsExplicit(i);
            context.DrawEllipse(explicitlyMasked ? MaskPipOnBrush : MaskPipOffBrush, null,
                new Point(ChordBoxGeometry.PipZoneWidth / 2, sy), 3.5, 3.5);

            if (TuningNames != null && i < TuningNames.Count && !string.IsNullOrEmpty(TuningNames[i]))
            {
                var noteText = ChartTheme.GetCachedText(TuningNames[i], 10,
                    stringBrushes[ChartTheme.StringSlot(i, StringCount)], true);
                context.DrawText(noteText,
                    new Point(ChordBoxGeometry.OpenDotX + 9 - noteText.Width, sy - noteText.Height / 2));
            }

            if (_hoveredString == i)
            {
                context.FillRectangle(ChartTheme.HoverBrush,
                    new Rect(0, sy - stringSpacing / 3, bounds.Width, stringSpacing / 1.5));
            }

            // Explicit mask band. Skipped while the mask is empty: highlighting
            // every string would read as a selection the user never made.
            if (!maskEmpty && explicitlyMasked)
            {
                context.FillRectangle(MaskedStringBrush,
                    new Rect(0, sy - stringSpacing / 3, bounds.Width, stringSpacing / 1.5));
            }
        }

        // Muted strings: an ✕ on the rail. A muted string is MuteType.Full on
        // the chord note — NOT a missing/"x" template fret, which is simply an
        // unused string.
        if (MutedStrings != null)
        {
            foreach (var idx in MutedStrings)
            {
                if (idx < 0 || idx >= StringCount) continue;
                var sy = g.StringY(idx);
                var cx = ChordBoxGeometry.OpenDotX;
                context.DrawLine(MuteMarkPen, new Point(cx - 5, sy - 5), new Point(cx + 5, sy + 5));
                context.DrawLine(MuteMarkPen, new Point(cx + 5, sy - 5), new Point(cx - 5, sy + 5));
            }
        }

        // Hover dot.
        if (_hoveredString >= 0 && _hoveredFret >= 0 && !_hoveredOverPip && Role != ChordBoxRole.ReadOnly)
        {
            var sy = g.StringY(_hoveredString);
            var sx = _hoveredFret <= Capo
                ? ChordBoxGeometry.OpenDotX
                : g.FretCenterX(_hoveredFret);
            context.DrawEllipse(HoverDotBrush, null, new Point(sx, sy), 8.5, 8.5);
        }

        // The shape itself.
        if (Chord != null)
        {
            foreach (var cn in Chord.ChordNotes)
            {
                if (cn.StringIndex < 0 || cn.StringIndex >= StringCount) continue;

                var sy = g.StringY(cn.StringIndex);
                var noteBrush = stringBrushes[ChartTheme.StringSlot(cn.StringIndex, StringCount)];

                if (cn.Fret <= Capo)
                {
                    context.DrawEllipse(null, StringNotePens[ChartTheme.StringSlot(cn.StringIndex, StringCount)],
                        new Point(ChordBoxGeometry.OpenDotX, sy), 6, 6);
                }
                else if (cn.Fret >= _displayMinFret && cn.Fret <= _displayMaxFret)
                {
                    var sx = g.FretCenterX(cn.Fret);
                    context.DrawEllipse(noteBrush, null, new Point(sx, sy), 8.5, 8.5);

                    if (Template?.Fingers != null &&
                        Template.Fingers.TryGetValue(cn.StringIndex, out var fingerId))
                    {
                        var fingerStr = fingerId == 0 ? "T" : fingerId.ToString();
                        var fText = ChartTheme.GetCachedText(fingerStr, 11, FingerTextBrush, true);
                        context.DrawText(fText, new Point(sx - fText.Width / 2, sy - fText.Height / 2));
                    }

                    if (cn.Mute is MuteType.Full or MuteType.Palm)
                    {
                        context.DrawLine(MutePen, new Point(sx - 4, sy - 4), new Point(sx + 4, sy + 4));
                        context.DrawLine(MutePen, new Point(sx + 4, sy - 4), new Point(sx - 4, sy + 4));
                    }
                }
            }
        }

        // Issue pips, at the far right of the offending string.
        if (IssueStrings != null)
        {
            foreach (var pip in IssueStrings)
            {
                if (pip.StringIndex < 0 || pip.StringIndex >= StringCount) continue;
                var sy = g.StringY(pip.StringIndex);
                context.DrawEllipse(pip.BlocksExport ? IssueBlockingBrush : IssueWarnBrush, null,
                    new Point(bounds.Width - 5, sy), 3.5, 3.5);
            }
        }
    }
}

/// <summary>One validation pip on a string. A value type so the property's
/// change detection stays reference-based on the LIST, which is what the
/// "reassign a fresh array" contract needs.</summary>
public readonly record struct ChordBoxIssuePip(int StringIndex, bool BlocksExport);
