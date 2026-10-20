namespace DotCharter.Services.Editing.Pen;

/// <summary>
///     The keyboard note-entry pen: seek → toggle strings → type frets, with the
///     commit implicit and the target implied. Stage 2 of the editing rework;
///     read <c>docs/agents/ui/editor-input.md</c> before touching it.
///     <para>
///         Every method is a strict no-op (or the legacy behaviour) while
///         <see cref="IsEnabled" /> is false, so the pen and the existing pointer
///         path ship side by side behind <c>AppConfig.UseFastChartingInput</c>.
///     </para>
/// </summary>
public interface IChartPenService
{
    /// <summary><c>AppConfig.UseFastChartingInput</c>. Read per keystroke so the
    /// setting takes effect without a restart.</summary>
    bool IsEnabled { get; }

    /// <summary>
    ///     <c>1..9</c> — toggle a string into or out of the sound at the pen:
    ///     create a note, add the string to the chord already there, remove it,
    ///     and collapse/delete the sound when the last string goes.
    /// </summary>
    void ToggleString(int stringIndex, int gridDivisor);

    /// <summary>
    ///     One typed fret digit. Multi-digit within the accumulator window, so
    ///     <c>1</c> then <c>2</c> is fret 12 — the whole run is ONE undo step.
    ///     With NO string mask the value transposes the shape instead of setting
    ///     every string.
    ///     <para>
    ///         While the pen is disabled this is the pre-v0.35 single-digit
    ///         "set the selection's fret", so the shipped <c>Alt+0..9</c> row is
    ///         unchanged for anyone who has not opted in.
    ///     </para>
    /// </summary>
    void TypeFretDigit(int digit, int gridDivisor);

    /// <summary><c>Esc</c>. Undoes the in-progress entry; returns false (and
    /// claims nothing) when there was none.</summary>
    bool RevertPendingEntry();

    /// <summary>
    ///     Where the pen currently sits: the playhead, snapped the way the
    ///     POINTER snaps (grid, with a nearer existing sound winning). Unlike
    ///     the verbs above this is live regardless of <see cref="IsEnabled" /> —
    ///     it is pure geometry, and the composition root installs it as
    ///     <c>IEditTargetService</c>'s pen-position provider so every keyboard
    ///     verb resolves its implied target at the same place.
    /// </summary>
    FractionalPosition PenPosition(int gridDivisor);
}
