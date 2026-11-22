using System.IO.Compression;
using System.Buffers.Binary;
using System.Text;
using DotCharter.Services.Audio.StemSeparation.Cache;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Audio;

/// <summary>A hostile .dcstems must be refused without writing outside the destination.</summary>
public sealed class StemArchiveHostileTests : IDisposable
{
    [Fact]
    public void ForgedDirectoryCount_CannotHideTheActualEntries()
    {
        var archive = Build(Enumerable.Range(0, StemArchive.MaxEntries + 1)
            .Select(i => ($"s{i}.opus", new byte[] { 1 })).ToArray());
        var bytes = File.ReadAllBytes(archive);
        Assert.InRange(bytes.Length, 22, 16_384);
        var end = bytes.AsSpan(bytes.Length - 22);
        Assert.Equal(0x06054b50u, BinaryPrimitives.ReadUInt32LittleEndian(end));
        BinaryPrimitives.WriteUInt16LittleEndian(end[8..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(end[10..], 1);
        File.WriteAllBytes(archive, bytes);

        Assert.Throws<StemArchiveRefusedException>(() => StemArchive.ReadManifest(archive));
        Assert.Throws<StemArchiveRefusedException>(() => StemArchive.ReadEntryBytes(archive, "s0.opus"));
        Assert.True(File.Exists(archive));
        // Preserve refusal (rather than deletion as corrupt) for unsupported Zip64 counts.
        BinaryPrimitives.WriteUInt16LittleEndian(end[8..], ushort.MaxValue);
        BinaryPrimitives.WriteUInt16LittleEndian(end[10..], ushort.MaxValue);
        File.WriteAllBytes(archive, bytes);
        Assert.Throws<StemArchiveRefusedException>(() => StemArchive.ReadManifest(archive));
    }

    private readonly string _tmp;
    private readonly string _dest;

    public StemArchiveHostileTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), "DotCharterStemHostile", Guid.NewGuid().ToString("N"));
        _dest = Path.Combine(_tmp, "a", "dest");
        Directory.CreateDirectory(_tmp);
    }

    public void Dispose() => BestEffortDelete.Directory(_tmp);

    private string Build(params (string Name, byte[] Data)[] entries)
    {
        var path = Path.Combine(_tmp, Guid.NewGuid().ToString("N") + ".dcstems");
        using var fs = File.Create(path);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        foreach (var (name, data) in entries)
        {
            using var s = zip.CreateEntry(name).Open();
            s.Write(data);
        }
        return path;
    }

    private void AssertNothingEscaped()
    {
        var files = Directory.GetFiles(_tmp, "*", SearchOption.AllDirectories)
            .Where(f => !f.EndsWith(".dcstems", StringComparison.Ordinal));
        Assert.DoesNotContain(files, f => !f.StartsWith(_dest, StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("..\\escape.wav")]
    [InlineData("..\\..\\escape.wav")]
    [InlineData("C:\\escape.wav")]
    [InlineData("/escape.wav")]
    [InlineData("../escape.wav")]
    [InlineData("sub/../../escape.wav")]
    public void A_path_escaping_entry_is_refused(string name)
    {
        var archive = Build(("manifest.json", [1]), (name, [1, 2, 3]));
        Assert.Throws<StemArchiveRefusedException>(() => StemArchive.ExtractStems(archive, _dest));
        AssertNothingEscaped();
    }

    [Fact]
    public void Too_many_entries_are_refused_before_anything_is_written()
    {
        var entries = Enumerable.Range(0, StemArchive.MaxEntries + 1)
            .Select(i => ($"s{i}.opus", new byte[] { 1 })).ToArray();
        var archive = Build(entries);
        Assert.Throws<StemArchiveRefusedException>(() => StemArchive.ExtractStems(archive, _dest));
        Assert.Empty(Directory.GetFiles(_dest));
    }

    [Fact]
    public void An_entry_inflating_past_the_budget_is_refused()
    {
        var archive = Build(("drums.opus", new byte[1 << 20]));
        Assert.Throws<StemArchiveRefusedException>(() => StemArchive.ExtractStems(archive, _dest, 64, 1024));
        AssertNothingEscaped();
    }

    [Fact]
    public void An_oversized_manifest_is_refused_and_the_archive_is_kept()
    {
        var json = new StemCacheManifest { Key = "example" }.ToJson() + new string(' ', 2 * 1024 * 1024);
        var archive = Build(("manifest.json", Encoding.UTF8.GetBytes(json)));

        Assert.Throws<StemArchiveRefusedException>(() => StemArchive.ReadManifest(archive));
        Assert.True(File.Exists(archive));
        using var unlocked = new FileStream(archive, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.True(unlocked.CanWrite);
    }

    [Fact]
    public void Every_read_path_refuses_an_archive_with_too_many_entries()
    {
        var entries = Enumerable.Range(0, StemArchive.MaxEntries)
            .Select(i => ($"s{i}.opus", new byte[] { 1 }))
            .Prepend(("manifest.json", Encoding.UTF8.GetBytes(new StemCacheManifest().ToJson())))
            .ToArray();
        var archive = Build(entries);

        Assert.Throws<StemArchiveRefusedException>(() => StemArchive.ReadManifest(archive));
        Assert.Throws<StemArchiveRefusedException>(() => StemArchive.ReadEntryBytes(archive, "s0.opus"));
        Assert.Throws<StemArchiveRefusedException>(() => StemArchive.HasSamePayload(archive, archive));
        Assert.Throws<StemArchiveRefusedException>(() =>
            StemArchive.Rewrite(archive, Path.Combine(_tmp, "copy.dcstems"), new StemCacheManifest()));
        Assert.True(File.Exists(archive));
    }

    [Theory]
    [InlineData("Lead..solo.opus")]
    [InlineData("a..b.opus")]
    public void A_name_with_a_double_dot_inside_it_extracts_normally(string name)
    {
        var archive = Build(("manifest.json", [1]), (name, Encoding.ASCII.GetBytes("abcd")));
        var files = StemArchive.ExtractStems(archive, _dest);
        Assert.Equal(Path.Combine(_dest, name), Assert.Single(files));
        Assert.Equal("abcd", File.ReadAllText(files[0]));
    }

    [Fact]
    public void A_normal_archive_still_extracts()
    {
        var archive = Build(("manifest.json", [1]), ("drums.opus", Encoding.ASCII.GetBytes("abcd")));
        var files = StemArchive.ExtractStems(archive, _dest);
        Assert.Single(files);
        Assert.Equal("abcd", File.ReadAllText(files[0]));
    }
}
