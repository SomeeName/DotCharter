using DotCharter.Services.Audio.Dsp.Tones;
using DotCharter.Services.Editing.Tones;

namespace DotCharter.Services.Session;

/// <summary>
/// What one save does for the store OUTSIDE the project: the player's tone edits and
///     the tones' chart-synth instruments (<see cref="SongToneEditStore" />), keyed by song identity
///     (an own chart's is its project path) and tone key, follow the tone keys the save writes.
///     <list type="number">
///         <item><see cref="Capture" /> on the UI thread, beside the snapshot: the renames the live
///         project's <see cref="ToneKeyRenameJournal" /> holds, net (an undone rename is gone), and
///         only those whose new key the snapshot really has.</item>
///         <item><see cref="Apply" /> on the worker, AFTER the <c>.rscp</c> is in place.</item>
///         <item><see cref="Settle" /> once both agree: the captured steps are the new baseline.
///         A store that could not be written keeps them, so the next save moves the entries.</item>
///     </list>
///     <para>
///         <b>Save (same path)</b> moves the entries in place. <b>Save As</b> copies the old
///         path's entries, renamed, to the new path and leaves the old path's alone (that file is
///         still on disk under its old keys). <b>A first save</b> has no earlier file, and a session
///         loaded from the library has no project path, so neither moves anything: a library
///         (<c>lib:</c>) identity is never written.
///     </para>
/// </summary>
internal sealed class SongToneEditKeyFollow
{
    private static readonly IReadOnlyDictionary<string, string> NoMoves = new Dictionary<string, string>();

    private readonly ToneKeyRenameJournal? _journal;
    private readonly int _mark;

    private SongToneEditKeyFollow(ToneKeyRenameJournal? journal, int mark, string fromIdentity, string toIdentity,
        IReadOnlyDictionary<string, string> moves)
    {
        _journal = journal;
        _mark = mark;
        FromIdentity = fromIdentity;
        ToIdentity = toIdentity;
        Moves = moves;
    }

    /// <summary>The song the store's entries are under now: the path the project was last
    /// saved to or opened from. Empty for a project that has never been saved.</summary>
    public string FromIdentity { get; }

    /// <summary>The path this save writes.</summary>
    public string ToIdentity { get; }

    /// <summary>Saved tone key → the key it is written under now.</summary>
    public IReadOnlyDictionary<string, string> Moves { get; }

    /// <summary>Reads the live project's renames against what <paramref name="snapshot" /> writes.
    /// UI thread, with the snapshot: the journal is the history's, and so is its order.</summary>
    public static SongToneEditKeyFollow Capture(ChartProject? live, string? previousPath, string savedPath,
        ChartProject snapshot)
    {
        if (live is null) return new SongToneEditKeyFollow(null, 0, previousPath ?? "", savedPath, NoMoves);

        var journal = ToneKeyRenameJournal.For(live);
        var mark = journal.Count;
        var written = new HashSet<string>(ToneKeyEdits.Keys(snapshot).Select(k => k.Key), StringComparer.OrdinalIgnoreCase);
        var moves = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (saved, now) in journal.NetMoves(mark))
        {
            // A key the file does not carry (renamed, then deleted) is no home for an edit.
            if (written.Contains(now)) moves[saved] = now;
        }

        return new SongToneEditKeyFollow(journal, mark, previousPath ?? "", savedPath, moves);
    }

    /// <summary>Moves (or, for a Save As, copies) the entries. True when the store now agrees
    /// with the saved file; false when it could not be written. Never throws past the logger.</summary>
    public bool Apply(SongToneEditStore store, IAppLogger logger)
    {
        if (string.IsNullOrWhiteSpace(FromIdentity)) return true; // a first save: nothing was keyed by a file yet
        var sameSong = string.Equals(FromIdentity, ToIdentity, StringComparison.Ordinal);
        if (sameSong && Moves.Count == 0) return true;

        var result = store.MoveKeys(FromIdentity, ToIdentity, Moves);
        var renames = string.Join(", ", Moves.Select(m => $"'{m.Key}' -> '{m.Value}'"));
        if (!result.Ok)
        {
            logger.Warning(AppLogCategory.Project,
                $"Song tone edits did not follow the saved tone keys ({renames}) for '{ToIdentity}': {result.Detail}. " +
                "The next save tries again.");
            return false;
        }

        if (result.Outcome == SongToneEditWriteOutcome.Written)
        {
            logger.Info(AppLogCategory.Project, sameSong
                ? $"Song tone edits followed the renamed tone keys ({renames})."
                : $"Song tone edits copied to the Save As path '{ToIdentity}' (renamed: {(renames.Length > 0 ? renames : "none")}).");
        }

        return true;
    }

    /// <summary>The captured renames reached the file and the store: forget them.</summary>
    public void Settle() => _journal?.Settle(_mark);
}
