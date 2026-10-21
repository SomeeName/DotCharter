namespace DotCharter.Services.Infrastructure;

/// <summary>
///     A forward-only read wrapper that refuses to hand out more than
///     <c>maxBytes</c>: the read that would pass the budget throws
///     <see cref="ImportFileTooLargeException" /> instead of returning data. Counts what the inner stream really PRODUCES, so a zip
///     member whose header understates its inflated size is still stopped.
///     Disposing it disposes the inner stream.
/// </summary>
public sealed class LimitedReadStream(Stream inner, long maxBytes, string what) : Stream
{
    private long _total;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => _total;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));

    public override int Read(Span<byte> buffer) => Count(inner.Read(buffer));

    private int Count(int read)
    {
        _total += read;
        ImportSizeLimits.RequireWithin(_total, maxBytes, what);
        return read;
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) inner.Dispose();
        base.Dispose(disposing);
    }
}
