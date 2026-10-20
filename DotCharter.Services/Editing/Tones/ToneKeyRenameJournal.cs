using System.Runtime.CompilerServices;
using DotCharter.Services.State;

namespace DotCharter.Services.Editing.Tones;

/// <summary>
/// The tone-key renames a LIVE project has run since it was last saved or loaded, so the
///     save can move what DotCharter keeps OUTSIDE the project under a tone key — the player's tone
///     edit and the tone's chart-synth instrument (<c>SongToneEditStore</c>) — from the key the
///     saved file carries to the key it now writes.
///     <para>
///         <b>One journal per project INSTANCE</b>, held in a weak table: a discarded project (a
///         reload, another project opened, the app closed without saving) takes its unsaved renames
///         with it, and nothing is ever moved for them.
///     </para>
///     <para>
///         <b>It follows the history, not the dialog.</b> <see cref="ToneKeyEdits.PlanRename" />
///         puts a <see cref="ToneKeyRenameJournalStep" /> at the end of the rename's one undo node:
///         its Execute (do and redo) records <c>old → new</c> and its Undo records <c>new → old</c>.
///         <see cref="NetMoves" /> composes the steps, so a rename and its undo cancel out and a
///         swap through a temporary key is one simultaneous exchange.
///     </para>
///     <para>
///         A save reads <see cref="NetMoves" /> beside its snapshot and, once the file and the store
///         agree, <see cref="Settle" />s exactly the steps that snapshot contained: a rename that
///         lands while the write is on the worker stays for the next save.
///     </para>
/// </summary>
public sealed class ToneKeyRenameJournal
{
    private static readonly ConditionalWeakTable<ChartProject, ToneKeyRenameJournal> Journals = [];

    private readonly List<(string From, string To)> _steps = [];
    private readonly object _gate = new();

    /// <summary>The journal of <paramref name="project" />; created empty on first use.</summary>
    public static ToneKeyRenameJournal For(ChartProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        return Journals.GetValue(project, _ => new ToneKeyRenameJournal());
    }

    /// <summary>How many steps the journal holds; a save passes it back to <see cref="Settle" />.</summary>
    public int Count
    {
        get
        {
            lock (_gate) return _steps.Count;
        }
    }

    /// <summary>A tone went from <paramref name="from" /> to <paramref name="to" /> in the live project.</summary>
    public void Record(string from, string to)
    {
        lock (_gate) _steps.Add(((from ?? "").Trim(), (to ?? "").Trim()));
    }

    /// <summary>
    ///     What the first <paramref name="count" /> steps add up to: each tone key the saved file
    ///     had, mapped to the key the same tone has now. Keys are trim- and case-insensitive
    ///     (<see cref="ToneKeyEdits.Same" />); a tone that came back to its saved key, or changed only
    ///     its spelling, is not in the map.
    /// </summary>
    public IReadOnlyDictionary<string, string> NetMoves(int count)
    {
        // current key -> the key it had when the journal began
        var origins = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        lock (_gate)
        {
            foreach (var (from, to) in _steps.Take(count))
            {
                if (from.Length == 0 || to.Length == 0) continue;
                var origin = origins.Remove(from, out var earlier) ? earlier : from;
                origins[to] = origin;
            }
        }

        var moves = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (current, origin) in origins)
        {
            if (!ToneKeyEdits.Same(current, origin)) moves[origin] = current;
        }

        return moves;
    }

    /// <summary>The first <paramref name="count" /> steps reached the saved file: forget them.</summary>
    public void Settle(int count)
    {
        lock (_gate) _steps.RemoveRange(0, Math.Clamp(count, 0, _steps.Count));
    }
}

/// <summary>The last child of a rename's undo node: tells the project's
/// <see cref="ToneKeyRenameJournal" /> the rename ran (do, redo) or was taken back (undo).</summary>
public sealed class ToneKeyRenameJournalStep(ChartProject project, string from, string to) : IChartAction
{
    public void Execute() => ToneKeyRenameJournal.For(project).Record(from, to);

    public void Undo() => ToneKeyRenameJournal.For(project).Record(to, from);
}
