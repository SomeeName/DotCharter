namespace DotCharter.Services.Package.DcSong;

/// <summary>
///     A read-only, SEEKABLE window onto one STORED member of a package: bytes
///     <c>[start, start + length)</c> of the file, and nothing outside them. It owns
///     its own file handle, so it outlives the <see cref="DcSongPackage" /> that
///     handed it out and seeks independently of every other stream.
/// </summary>
internal sealed class DcSongEntryStream : Stream
{
    private readonly FileStream _file;
    private readonly long _start;
    private readonly long _length;
    private long _position;

    public DcSongEntryStream(string path, long start, long length)
    {
        _file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.RandomAccess);
        if (start < 0 || length < 0 || start + length > _file.Length)
        {
            _file.Dispose();
            throw new DcSongReadException(DcSongReadFailure.Malformed, "a member's bytes lie outside the file.");
        }

        _start = start;
        _length = length;
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _length;

    public override long Position
    {
        get => _position;
        set => Seek(value, SeekOrigin.Begin);
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var remaining = _length - _position;
        if (remaining <= 0 || buffer.IsEmpty) return 0;
        if (buffer.Length > remaining) buffer = buffer[..(int)remaining];

        _file.Position = _start + _position;
        var read = _file.Read(buffer);
        _position += read;
        return read;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        var target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };
        ArgumentOutOfRangeException.ThrowIfNegative(target, nameof(offset));
        _position = target;
        return _position;
    }

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) _file.Dispose();
        base.Dispose(disposing);
    }
}
