namespace DotCharter.Presentation.Navigation;

/// <summary>
///     A bounded stack of screen contracts, top last. Pushing a contract that is
///     already on the stack unwinds to it; pushing past the capacity drops the oldest.
/// </summary>
public sealed class BackStack
{
    public const int DefaultCapacity = 16;

    private readonly List<Type> _items = [];
    private readonly int _capacity;

    public BackStack(int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _capacity = capacity;
    }

    public int Count => _items.Count;

    public Type? Top => _items.Count == 0 ? null : _items[^1];

    public IReadOnlyList<Type> Items => _items;

    public void Push(Type contract)
    {
        ArgumentNullException.ThrowIfNull(contract);
        var at = _items.IndexOf(contract);
        if (at >= 0)
        {
            _items.RemoveRange(at + 1, _items.Count - at - 1);
            return;
        }

        _items.Add(contract);
        if (_items.Count > _capacity) _items.RemoveAt(0);
    }

    public Type? Pop()
    {
        if (_items.Count == 0) return null;
        var top = _items[^1];
        _items.RemoveAt(_items.Count - 1);
        return top;
    }
}
