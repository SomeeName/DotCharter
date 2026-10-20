using DotCharter.Tone.Audio;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Audio;

/// <summary>
///     <see cref="NativeFileBuffer" /> carries a library song's bytes to the engine
///     outside the managed heap (#411). It must read the file exactly, and once
///     released it must refuse every access instead of handing out freed memory.
/// </summary>
public sealed class NativeFileBufferTests : IDisposable
{
    private readonly string _tempDir =
        Directory.CreateTempSubdirectory("dotcharter-nativefilebuffer-").FullName;

    public void Dispose() => BestEffortDelete.Directory(_tempDir);

    private string Write(byte[] bytes)
    {
        var path = Path.Combine(_tempDir, $"{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3 * 1024 * 1024 + 17)]
    public async Task ReadAsync_HoldsTheWholeFile_ByteForByte(int length)
    {
        var bytes = new byte[length];
        new Random(length).NextBytes(bytes);

        var buffer = await NativeFileBuffer.ReadAsync(Write(bytes), TestContext.Current.CancellationToken);

        Assert.NotNull(buffer);
        Assert.Equal(length, buffer.Length);
        Assert.Equal(length, buffer.Memory.Length);
        Assert.True(buffer.Memory.Span.SequenceEqual(bytes));
        buffer.Release();
    }

    [Fact]
    public async Task Release_FreesOnce_AndEveryLaterAccessThrows()
    {
        var buffer = await NativeFileBuffer.ReadAsync(Write([1, 2, 3]), TestContext.Current.CancellationToken);
        Assert.NotNull(buffer);
        var memory = buffer.Memory;

        Assert.False(buffer.IsReleased);
        buffer.Release();
        buffer.Release();

        Assert.True(buffer.IsReleased);
        Assert.Throws<ObjectDisposedException>(() => memory.Span.Length);
        Assert.Throws<ObjectDisposedException>(() => memory.Pin());
    }

    [Fact]
    public async Task ReadAsync_DoesNotLockTheFile()
    {
        var path = Write([9, 9, 9]);
        var buffer = await NativeFileBuffer.ReadAsync(path, TestContext.Current.CancellationToken);
        Assert.NotNull(buffer);

        // The PSARC audio cache may replace or delete the WAV while a song is open.
        File.Delete(path);
        Assert.False(File.Exists(path));
        Assert.Equal(new byte[] { 9, 9, 9 }, buffer.Memory.ToArray());
        buffer.Release();
    }
}
