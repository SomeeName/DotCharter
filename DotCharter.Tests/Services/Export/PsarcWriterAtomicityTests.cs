using DotCharter.Services.Infrastructure.Psarc.Write;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Export;

/// <summary>
///     PSARC writes must replace the destination atomically.
/// </summary>
/// <remarks>
///     <see cref="FileMode.Create" /> truncates the destination the instant it opens.
///     The path handed to <see cref="PsarcWriter.WriteFile" /> is the one the user
///     picked in the export dialog — usually an existing, working package — so a
///     failure part-way through the write (disk full, IO error, cancelled build,
///     process killed) used to destroy the good package and leave a truncated stub.
///     A PCM-audio package is tens of MB, so that window is seconds, not microseconds.
/// </remarks>
public sealed class PsarcWriterAtomicityTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dotcharter_psarc_atomic_").FullName;

    public void Dispose() => BestEffortDelete.Directory(_dir);

    [Fact]
    public void A_successful_write_replaces_the_previous_package()
    {
        var path = Path.Combine(_dir, "song_p.psarc");
        File.WriteAllText(path, "the previous package");

        PsarcWriter.WriteFile(path, [Entry("appid.appid", "248750"u8.ToArray())]);

        // The file is exactly what the writer produces for those entries (#885: checked
        // by bytes, without the package reader).
        using var expected = new MemoryStream();
        PsarcWriter.Write(expected, [Entry("appid.appid", "248750"u8.ToArray())]);
        Assert.Equal(expected.ToArray(), File.ReadAllBytes(path));
    }

    [Fact]
    public void A_failed_write_leaves_the_previous_package_intact()
    {
        // THE regression. An entry that throws while its data is being read stands in
        // for any mid-write failure — the destination must still be the old, good file.
        var path = Path.Combine(_dir, "song_p.psarc");
        var original = new byte[] { 1, 2, 3, 4, 5 };
        File.WriteAllBytes(path, original);

        var exploding = new PsarcWriteEntry("songs/bin/generic/boom.sng", new ExplodingStream());

        Assert.ThrowsAny<Exception>(() => PsarcWriter.WriteFile(path, [exploding]));

        Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Fact]
    public void A_failed_write_leaves_no_staging_file_behind()
    {
        var path = Path.Combine(_dir, "song_p.psarc");
        File.WriteAllBytes(path, [1, 2, 3]);

        Assert.ThrowsAny<Exception>(() =>
            PsarcWriter.WriteFile(path, [new PsarcWriteEntry("x.sng", new ExplodingStream())]));

        Assert.Empty(Directory.GetFiles(_dir, "*.writing"));
    }

    [Fact]
    public void Writing_to_a_fresh_path_creates_the_directory()
    {
        var path = Path.Combine(_dir, "nested", "deeper", "song_p.psarc");

        PsarcWriter.WriteFile(path, [Entry("appid.appid", "248750"u8.ToArray())]);

        Assert.True(File.Exists(path));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.writing"));
    }

    private static PsarcWriteEntry Entry(string name, byte[] data) => new(name, data);

    /// <summary>Blows up part-way through being read, like a disk error would.</summary>
    private sealed class ExplodingStream : Stream
    {
        private long _position;

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => 1_000_000;

        public override long Position
        {
            get => _position;
            set => _position = value;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_position > 0) throw new IOException("simulated disk failure mid-write");
            _position += count;
            return count;
        }

        public override long Seek(long offset, SeekOrigin origin) => _position = offset;
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
