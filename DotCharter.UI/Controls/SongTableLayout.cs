namespace DotCharter.UI.Controls;

/// <summary>
///     One column of a song table: its fixed width, and when it gives way to the song column.
/// </summary>
/// <param name="Name">What the column holds, for messages and tests.</param>
/// <param name="Width">The fixed width. The song column's is ignored: it takes what the others leave.</param>
/// <param name="GivesWay">
///     0: the column never leaves. Otherwise its place in the order the fixed columns leave the
///     table in while the song column is under <see cref="SongTableLayout.SongFloor" />: 1 leaves first.
/// </param>
public readonly record struct SongTableColumn(string Name, double Width, int GivesWay);

/// <summary>
/// The columns of a song table, shared by its caption row and every row under it, so a
///     caption sits over its values by construction instead of by two <c>ColumnDefinitions</c>
///     strings kept equal by hand.
///     <para>
///         The SONG column gets the space first. At a width where it would be narrower than
///         <see cref="SongFloor" />, the fixed columns give way one at a time in their declared
///         order (<see cref="SongTableColumn.GivesWay" />) until it is not, and a column that left
///         at one width never comes back at a narrower one. Below the floor with nothing left to
///         give, the song column takes what remains. Pure: <see cref="SongTablePanel" /> asks it,
///         the tests read it.
///     </para>
/// </summary>
public sealed class SongTableLayout
{
    /// <summary>The song column's floor: ~30 characters of a title at the row title size.</summary>
    public const double SongFloor = 260;

    /// <summary>The gap between two shown columns.</summary>
    public const double Gap = 14;

    private readonly int[] _givingWayOrder;

    private SongTableLayout(int songColumn, params SongTableColumn[] columns)
    {
        if (songColumn < 0 || songColumn >= columns.Length) throw new ArgumentOutOfRangeException(nameof(songColumn));
        if (columns[songColumn].GivesWay != 0) throw new ArgumentException("the song column never gives way", nameof(columns));

        SongColumn = songColumn;
        Columns = columns;
        _givingWayOrder = [.. Enumerable.Range(0, columns.Length).Where(i => columns[i].GivesWay > 0).OrderBy(i => columns[i].GivesWay)];
    }

    /// <summary>
    ///     The Library (<c>Views/Library/LibraryView</c> + <c>LibraryRowView</c>). A charter's and
    ///     a player's library both pick by song, paths and tuning; the year is the least needed
    ///     and leaves first; the status (empty unless a song cannot load) goes next, and its
    ///     stand-ins in the cover column (a mark on the row, the funnel in the caption row) keep a
    ///     problem visible and its filter reachable.
    /// </summary>
    public static SongTableLayout Library { get; } = new(1,
        new("cover", 32, 0),
        new("song", 0, 0),
        new("paths", 110, 6),
        new("tuning", 160, 5),
        new("year", 72, 1),
        new("difficulty", 64, 4),
        new("time", 60, 3),
        new("status", 132, 2));

    /// <summary>
    ///     Play Mode's Song Select (<c>Views/PlayMode/Redesign/SongSelectScreenView</c>). The
    ///     player's own record is what this screen adds, so it outlasts the length and the
    ///     difficulty; paths and tuning (can I play it, must I retune) stay longest.
    /// </summary>
    public static SongTableLayout SongSelect { get; } = new(1,
        new("cover", 32, 0),
        new("song", 0, 0),
        new("paths", 110, 5),
        new("tuning", 150, 4),
        new("time", 56, 1),
        new("difficulty", 84, 2),
        new("record", 150, 3));

    /// <summary>
    ///     The LIVE Play Mode song list (<c>SongListBox</c> in <c>Views/PlayMode/PlayModeView</c>),
    ///     the one shipping today: the same order as
    ///     <see cref="SongSelect" />, over its own cells (a 52 px cover, the length and the
    ///     difficulty stacked in one cell, the record over a mastery meter).
    /// </summary>
    public static SongTableLayout PlayModeSongList { get; } = new(1,
        new("cover", 52, 0),
        new("song", 0, 0),
        new("paths", 110, 4),
        new("tuning", 150, 3),
        new("record", 140, 2),
        new("time", 64, 1));

    public IReadOnlyList<SongTableColumn> Columns { get; }

    /// <summary>The index of the song column, the one that takes the slack.</summary>
    public int SongColumn { get; }

    /// <summary>The width every column needs with the song column at its floor.</summary>
    public double FullWidth => Width(AllShown(), SongFloor);

    /// <summary>Which columns a table this wide shows. An unbounded width shows all of them.</summary>
    public bool[] ShownAt(double width)
    {
        var shown = AllShown();
        if (!double.IsFinite(width)) return shown;

        foreach (var column in _givingWayOrder)
        {
            if (SongWidth(width, shown) >= SongFloor) break;
            shown[column] = false;
        }

        return shown;
    }

    /// <summary>What the song column gets in a table this wide with these columns shown.</summary>
    public double SongWidth(double width, IReadOnlyList<bool> shown) => Math.Max(0, width - Width(shown, 0));

    /// <summary>
    ///     Each column's left edge and width in a table this wide. A hidden column is zero wide, at
    ///     the left edge of the next shown one.
    /// </summary>
    public (double Left, double Width)[] Place(double width, IReadOnlyList<bool> shown)
    {
        var places = new (double Left, double Width)[Columns.Count];
        var song = SongWidth(width, shown);
        var x = 0.0;
        var first = true;
        for (var i = 0; i < Columns.Count; i++)
        {
            if (!shown[i])
            {
                places[i] = (x + (first ? 0 : Gap), 0);
                continue;
            }

            if (!first) x += Gap;
            var w = i == SongColumn ? song : Columns[i].Width;
            places[i] = (x, w);
            x += w;
            first = false;
        }

        return places;
    }

    private bool[] AllShown() => [.. Columns.Select(_ => true)];

    /// <summary>The fixed columns' widths and the gaps between the shown columns, plus <paramref name="song" />.</summary>
    private double Width(IReadOnlyList<bool> shown, double song)
    {
        var count = 0;
        var total = 0.0;
        for (var i = 0; i < Columns.Count; i++)
        {
            if (!shown[i]) continue;
            count++;
            total += i == SongColumn ? song : Columns[i].Width;
        }

        return total + Math.Max(0, count - 1) * Gap;
    }
}
