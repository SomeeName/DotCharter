using System.Buffers;
using System.Runtime.InteropServices;

namespace DotCharter.Tone.Audio;

/// <summary>
///     A whole file read into NATIVE memory, handed out as a <see cref="Memory{T}" />
/// and freed the moment it is disposed.
///     <para>
///         Opening a library song hands the engine the cached WAV's bytes
///         (<c>ProjectSessionService.LoadAudioFile(preferMemoryPlayback: true)</c>) so the
///         engine never holds the file open. Those bytes are not a managed
///         <c>byte[]</c> — ~50 MB for a five-minute song, on the large object heap — and
///         the engine copies them anyway. A large-object array is not returned to the OS
///         when the load is done: it waits for a gen-2 collection, and even then the GC
///         keeps the committed memory for a while, so every song opened would leave its file
///         size (twice, while the service also copied it) in the process's private
///         bytes. Native memory goes back the moment <see cref="Release" /> runs.
///     </para>
///     <para>
///         The owner disposes it once every reader is done. After that, every access
///         through <c>Memory</c> throws <see cref="ObjectDisposedException" />
///         rather than reading freed memory.
///     </para>
/// </summary>
public sealed class NativeFileBuffer : MemoryManager<byte>
{
    private unsafe byte* _data;
    private readonly int _length;

    private unsafe NativeFileBuffer(int length)
    {
        _length = length;
        _data = (byte*)NativeMemory.Alloc((nuint)Math.Max(1, length));
    }

    /// <summary>Bytes held.</summary>
    public int Length => _length;

    /// <summary>True once the native memory has been freed.</summary>
    public unsafe bool IsReleased => _data == null;

    /// <summary>
    ///     Reads the whole file. Returns null for a file too large to hand over as one
    ///     span (over 2 GB) — the caller opens it by path instead.
    /// </summary>
    public static async Task<NativeFileBuffer?> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var size = RandomAccess.GetLength(handle);
        if (size > int.MaxValue) return null;

        var buffer = new NativeFileBuffer((int)size);
        try
        {
            var read = 0;
            while (read < buffer._length)
            {
                var n = await RandomAccess.ReadAsync(handle, buffer.Memory[read..], read, cancellationToken)
                    .ConfigureAwait(false);
                if (n == 0) throw new EndOfStreamException($"'{path}' ended after {read} of {buffer._length} bytes.");
                read += n;
            }

            return buffer;
        }
        catch
        {
            buffer.Release();
            throw;
        }
    }

    public override unsafe Span<byte> GetSpan()
    {
        ObjectDisposedException.ThrowIf(_data == null, this);
        return new Span<byte>(_data, _length);
    }

    public override unsafe MemoryHandle Pin(int elementIndex = 0)
    {
        ObjectDisposedException.ThrowIf(_data == null, this);
        if ((uint)elementIndex > (uint)_length) throw new ArgumentOutOfRangeException(nameof(elementIndex));
        return new MemoryHandle(_data + elementIndex);
    }

    // Native memory does not move, so there is nothing to unpin.
    public override void Unpin() { }

    /// <summary>Frees the native memory now. Idempotent.</summary>
    public void Release() => ((IDisposable)this).Dispose();

    // No finalizer on purpose (CA2015): a Span handed out earlier may still be
    // in use when a finalizer would run. Every owner releases explicitly.
    protected override unsafe void Dispose(bool disposing)
    {
        var data = _data;
        _data = null;
        if (data != null) NativeMemory.Free(data);
    }
}
