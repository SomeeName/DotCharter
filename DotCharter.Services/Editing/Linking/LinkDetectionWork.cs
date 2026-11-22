namespace DotCharter.Services.Editing.Linking;

internal sealed class LinkDetectionWork(CancellationToken cancellation, int limit = 8_000_000)
{
    private int _remaining = limit;

    public void Charge(int units = 1)
    {
        cancellation.ThrowIfCancellationRequested();
        if (units > _remaining) throw new LimitReached();
        _remaining -= units;
    }

    internal sealed class LimitReached : Exception
    {
    }
}
