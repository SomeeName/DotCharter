namespace DotCharter.Services.State;

/// <summary>Sets or clears one bookmark slot, preserving its object and list position on undo.</summary>
public sealed class SetBookmarkAction : IChartAction, ILinkPolicyAction
{
    private readonly IList<Bookmark> _bookmarks;
    private readonly Bookmark _bookmark;
    private readonly double? _before;
    private readonly double? _after;
    private readonly int _index;
    private readonly Action _changed;

    public SetBookmarkAction(IList<Bookmark> bookmarks, int slot, double? time, Action changed)
    {
        _bookmarks = bookmarks;
        var existing = bookmarks.FirstOrDefault(b => b.Index == slot);
        _bookmark = existing ?? new Bookmark { Index = slot };
        _before = existing?.Time;
        _after = time;
        _index = existing == null ? bookmarks.Count : bookmarks.IndexOf(existing);
        _changed = changed;
    }

    public LinkPolicy LinkPolicy => LinkPolicy.Retain;
    public bool HasEffect => _before != _after;

    public void Execute() => Apply(_after);
    public void Undo() => Apply(_before);

    private void Apply(double? time)
    {
        if (time is { } value)
        {
            _bookmark.Time = value;
            if (!_bookmarks.Contains(_bookmark)) _bookmarks.Insert(Math.Min(_index, _bookmarks.Count), _bookmark);
        }
        else
        {
            _bookmarks.Remove(_bookmark);
        }
        _changed();
    }
}
