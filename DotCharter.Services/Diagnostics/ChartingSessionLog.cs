using System.Globalization;

namespace DotCharter.Services.Diagnostics;

/// <summary>
///     A running tally of what the charter DID this session, emitted as one line
///     at exit.
///     <para>
///         <b>The log records every event and answers no question.</b> A real
///         32-minute session produced thousands of lines, and to learn something
///         as basic as "how much of that was fighting the undo stack" you had to
///         count node ids by hand. The per-event lines are the evidence; this is
///         the summary that says whether the evidence is worth reading.
///     </para>
///     <para>
///         Deliberately COUNTS ONLY — no positions, no file names, no lyric text.
///         It goes in a log the owner pastes into a chat, and a summary line is
///         the one part guaranteed to be read, so it must carry nothing that
///         needs redacting.
///     </para>
///     <para>
///         Thread-safe by <see cref="Interlocked" />: edits arrive on the UI
///         thread but saves and exports report from worker threads, and a tally
///         that occasionally loses a count would be worse than no tally — it
///         would look precise.
///     </para>
/// </summary>
public sealed class ChartingSessionLog
{
    private int _edits;
    private int _gestureTicks;
    private int _undos;
    private int _redos;
    private int _saves;
    private int _dialogsOpened;

    /// <summary>One undo node was recorded.</summary>
    public void CountEdit() => Interlocked.Increment(ref _edits);

    /// <summary>
    ///     One tick was FOLDED INTO an existing node by gesture merging. The ratio
    ///     of these to edits is the direct measure of how much a session is drags
    ///     and wheels rather than discrete decisions — the number that would have
    ///     shown the 111-node hand-shape resize immediately.
    /// </summary>
    public void CountGestureTick() => Interlocked.Increment(ref _gestureTicks);

    public void CountUndo() => Interlocked.Increment(ref _undos);
    public void CountRedo() => Interlocked.Increment(ref _redos);
    public void CountSave() => Interlocked.Increment(ref _saves);
    public void CountDialogOpened() => Interlocked.Increment(ref _dialogsOpened);

    /// <summary>
    ///     The summary line. Undo is reported as a SHARE of edits because the
    ///     absolute count means nothing without it: 40 undos in a 400-edit session
    ///     is ordinary exploration, 40 in a 60-edit session is the app fighting
    ///     back.
    /// </summary>
    public string Summarize()
    {
        var edits = Volatile.Read(ref _edits);
        var ticks = Volatile.Read(ref _gestureTicks);
        var undos = Volatile.Read(ref _undos);
        var redos = Volatile.Read(ref _redos);
        var saves = Volatile.Read(ref _saves);
        var dialogs = Volatile.Read(ref _dialogsOpened);

        // INVARIANT culture: this line is grepped and pasted into chats, and on
        // a German machine the default formatter writes "111,0", which reads as
        // a thousands separator to everyone downstream.
        var undoShare = edits > 0
            ? " (" + (undos * 100.0 / edits).ToString("F0", CultureInfo.InvariantCulture) + "% of edits)"
            : "";
        var perGesture = edits > 0
            ? ", " + ((ticks + edits) / (double)edits).ToString("F1", CultureInfo.InvariantCulture) +
              " ticks per undo step"
            : "";

        return $"ChartingSession: edits={edits}, gestureTicksMerged={ticks}{perGesture}, " +
               $"undo={undos}{undoShare}, redo={redos}, saves={saves}, dialogs={dialogs}.";
    }
}
