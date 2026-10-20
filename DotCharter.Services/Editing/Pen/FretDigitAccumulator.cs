namespace DotCharter.Services.Editing.Pen;

/// <summary>
///     The multi-digit fret typist: <c>1</c> then <c>2</c> means fret 12, but
///     only while the second digit lands inside the window.
///     <para>
///         rscharter's <c>GuitarModeHandler.handleNumber</c>
///         (<c>:478-494</c>, 2 s window) — and the reason DotCharter does NOT
///         need EOF's <c>CTRL+F1..F12</c> for frets 11–22
///         (<c>EOF_SRC/editor.c:3386-3445</c>), the one binding the
///         editing-rework audit calls out as the hardest reach in either
///         reference tool.
///     </para>
///     <para>
///         This is ONE implementation with two window lengths, not two
///         implementations. The sheet pane shipped this exact arithmetic inline
///         (<c>SheetMusicView.OnKeyDown</c>, <see cref="SheetWindowMs" />) before
///         the keyboard pen existed; it now calls in here, from
///         <c>SheetMusicViewInput.TryHandleKey</c>. The windows differ
///         deliberately: the sheet types onto an already-selected sound, where a
///         stale accumulator silently rewrites the wrong fret, while the pen's
///         digits are part of a create-then-fret gesture that wants rscharter's
///         roomier <see cref="PenWindowMs" />.
///     </para>
///     <para>
///         Immutable: it is held in editor state and pushed by a key handler, so
///         there must be no way for a consumer to mutate a shared instance.
///     </para>
/// </summary>
public readonly record struct FretDigitAccumulator(int PendingFret, long LastDigitTicks)
{
    /// <summary>rscharter's window (<c>GuitarModeHandler:474-476</c>).</summary>
    public const int PenWindowMs = 2000;

    /// <summary>What <c>SheetMusicView</c> has always used. Kept so lifting the
    /// arithmetic out of it is a strict no-op.</summary>
    public const int SheetWindowMs = 800;

    /// <summary>The largest fret a typed number may reach — the same 0..24 the
    /// note path clamps to (<c>SelectedNoteMutationCommands</c>) and the export
    /// gate enforces (<c>ChartExportValidator.KeyFretOutOfRange</c>).</summary>
    public const int MaxTypedFret = 24;

    /// <summary>Nothing typed yet. <c>-1</c> — not <c>0</c> — because fret 0 is
    /// a real, common fret (the open string / the capo).</summary>
    public static FretDigitAccumulator Empty => new(-1, 0);

    /// <summary>True while a further digit would EXTEND the value rather than
    /// start a new one (subject to the window).</summary>
    public bool IsAccumulating => PendingFret >= 0;

    /// <summary>
    ///     Feeds one digit and reports the fret it resolves to.
    ///     <paramref name="nowTicks" /> is <c>Environment.TickCount64</c> at the
    ///     call site — passed in rather than read here so the whole type is
    ///     testable without sleeping.
    ///     <para>
    ///         A candidate past <paramref name="maxFret" /> starts over on the
    ///         new digit instead of clamping: after <c>2</c>,<c>4</c> (fret 24) a
    ///         third digit <c>7</c> means "fret 7", never "fret 24 again".
    ///     </para>
    /// </summary>
    public FretDigitAccumulator Push(int digit, long nowTicks, int windowMs, int maxFret, out int fret)
    {
        if (digit is < 0 or > 9)
        {
            fret = Math.Clamp(PendingFret, 0, maxFret);
            return this;
        }

        var candidate = IsAccumulating && nowTicks - LastDigitTicks <= windowMs
            ? PendingFret * 10 + digit
            : digit;
        if (candidate > maxFret) candidate = digit;

        fret = candidate;
        return new FretDigitAccumulator(candidate, nowTicks);
    }

    /// <summary>Back to "nothing typed" — what any non-digit verb, a seek, or
    /// <c>Esc</c> does to the run.</summary>
    public FretDigitAccumulator Reset() => Empty;
}
