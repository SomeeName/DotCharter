namespace DotCharter.Services.Gameplay.Setlists;

/// <summary>
/// The pure state of one setlist being played: which entry is current,
///     what comes next, which songs were finished, and which were skipped because
///     they could not be opened. It never loads anything — the caller tries to
///     open the entry <see cref="MoveNext" /> hands it and reports a failure with
///     <see cref="MarkMissing" />, then asks for the next one.
///     <para>
///         The runner works on a COPY of the setlist taken at start, so editing the
///         list on Song Select mid-run cannot shift the song under the player.
///     </para>
/// </summary>
public sealed class SetlistRunner
{
    private readonly List<PlaySetlistEntry> _entries;
    private readonly HashSet<int> _completed = [];
    private readonly List<PlaySetlistEntry> _pendingSkips = [];
    private readonly List<PlaySetlistEntry> _skipped = [];

    public SetlistRunner(PlaySetlist setlist)
    {
        ArgumentNullException.ThrowIfNull(setlist);
        Name = setlist.Name;
        _entries = setlist.Entries.Select(e => e.Clone()).ToList();
    }

    public string Name { get; }

    public int Count => _entries.Count;

    /// <summary>The entry being played; -1 before the first <see cref="MoveNext" />
    /// and after <see cref="End" />.</summary>
    public int CurrentIndex { get; private set; } = -1;

    /// <summary>False once the list ran out or <see cref="End" /> was called.</summary>
    public bool IsRunning { get; private set; } = true;

    public PlaySetlistEntry? Current =>
        IsRunning && CurrentIndex >= 0 && CurrentIndex < _entries.Count ? _entries[CurrentIndex] : null;

    /// <summary>The entry after the current one — the Results screen's "Next:".
    /// Whether it can be opened is only known when it is tried.</summary>
    public PlaySetlistEntry? PeekNext() =>
        IsRunning && CurrentIndex + 1 < _entries.Count ? _entries[CurrentIndex + 1] : null;

    public bool HasNext => PeekNext() is not null;

    /// <summary>Songs that reached their results, each counted once (a retry of
    /// the same song does not count it twice).</summary>
    public int CompletedCount => _completed.Count;

    /// <summary>Every song skipped as missing, in order, over the whole run.</summary>
    public IReadOnlyList<PlaySetlistEntry> Skipped => _skipped;

    /// <summary>Advances to the next entry and returns it, or ends the run and
    /// returns null when there is none.</summary>
    public PlaySetlistEntry? MoveNext()
    {
        if (!IsRunning) return null;
        if (CurrentIndex + 1 >= _entries.Count)
        {
            End();
            return null;
        }

        CurrentIndex++;
        return _entries[CurrentIndex];
    }

    /// <summary>The current entry could not be opened (the song left the library,
    /// or its file is gone): it is remembered for the next Get Ready's note.</summary>
    public void MarkMissing()
    {
        if (Current is not { } entry) return;
        _skipped.Add(entry);
        _pendingSkips.Add(entry);
    }

    /// <summary>The current song reached its results.</summary>
    public void MarkCompleted()
    {
        if (Current is not null) _completed.Add(CurrentIndex);
    }

    /// <summary>The songs skipped since the last call, so each is named exactly
    /// once — on the Get Ready that follows it.</summary>
    public IReadOnlyList<PlaySetlistEntry> TakePendingSkips()
    {
        var taken = _pendingSkips.ToList();
        _pendingSkips.Clear();
        return taken;
    }

    public void End()
    {
        IsRunning = false;
        CurrentIndex = -1;
    }
}
