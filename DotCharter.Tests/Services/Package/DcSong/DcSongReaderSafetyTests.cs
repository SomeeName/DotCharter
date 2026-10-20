using System.IO.Compression;
using System.Text;
using DotCharter.Services.Package.DcSong;
using DotCharter.Tone.Infrastructure.Storage;
using static DotCharter.Tests.Services.Package.DcSong.DcSongTestKit;

namespace DotCharter.Tests.Services.Package.DcSong;

/// <summary>
///     A <c>.dcsong</c> is UNTRUSTED input. Every package here is built by hand, so
///     each one breaks exactly one rule — and needs no ffmpeg: the reader checks
///     structure and checksums, not whether the audio plays.
/// </summary>
public sealed class DcSongReaderSafetyTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dotcharter_dcsong_safety_").FullName;

    public void Dispose() => BestEffortDelete.Directory(_dir);

    private string PackagePath(string name = "song") => Path.Combine(_dir, name + ".dcsong");

    private static DcSongReadFailure Refusal(Action read) => Assert.Throws<DcSongReadException>(read).Reason;

    [Fact]
    public void AHandBuiltPackage_Opens_AndHandsBackExactlyItsBytes()
    {
        // The positive control every refusal below is measured against.
        var audio = RandomBytes(10_000, 3);
        var path = PackagePath();
        BuildPackage(path, MinimalMembers(_dir, audio));

        using var package = DcSongPackage.Open(path);
        package.VerifyAll();
        Assert.Equal(DcSongAudioCodec.Opus, package.Manifest.Audio.Codec);
        Assert.False(package.IsLossless);
        Assert.Equal("audio/song.opus", package.LoadChart().AudioFileName);

        using var stream = package.OpenAudio();
        Assert.True(stream.CanSeek);
        Assert.Equal(audio.Length, stream.Length);
        var all = new byte[audio.Length];
        stream.ReadExactly(all);
        Assert.Equal(audio, all);

        // Seekable: the middle of the member, and nothing past its end.
        stream.Position = 6_000;
        var tail = new byte[8_000];
        Assert.Equal(4_000, stream.ReadAtLeast(tail, 8_000, throwOnEndOfStream: false));
        Assert.Equal(audio.AsSpan(6_000).ToArray(), tail.AsSpan(0, 4_000).ToArray());
    }

    [Fact]
    public void ANewerMajorVersion_IsRefused_WhateverTheRestOfItsManifestSays()
    {
        var path = PackagePath();
        // A 2.x manifest may look like anything; the version is read before the rest.
        var manifest = Encoding.UTF8.GetBytes("""{ "format": "dcsong", "formatVersion": "2.0", "files": "a new shape" }""");
        BuildPackage(path, MinimalMembers(_dir), manifestJson: manifest);

        var ex = Assert.Throws<DcSongReadException>(() => DcSongPackage.Open(path));
        Assert.Equal(DcSongReadFailure.NewerVersion, ex.Reason);
        Assert.Contains("2.0", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ANewerMinorVersion_Opens_AndAMemberItAddedIsVerifiedButNotRead()
    {
        var path = PackagePath();
        var members = MinimalMembers(_dir);
        members.Add(new Member("extras/lyrics-karaoke.bin", RandomBytes(300, 9)));
        BuildPackage(path, members, formatVersion: "1.7");

        using var package = DcSongPackage.Open(path);
        Assert.Equal((1, 7), (package.Manifest.FormatMajor, package.Manifest.FormatMinor));
        Assert.Contains(package.Manifest.Files, f => f.Role == DcSongFileRole.Other);
        package.VerifyAll();
    }

    [Fact]
    public void ATamperedAudioMember_FailsItsChecksum()
    {
        var audio = RandomBytes(10_000, 4);
        var path = PackagePath();
        BuildPackage(path, MinimalMembers(_dir, audio));
        FlipByteInside(path, audio);

        // The structure still reads; the member's CONTENT is what is caught.
        using var package = DcSongPackage.Open(path);
        Assert.Equal(DcSongReadFailure.ChecksumMismatch, Refusal(() => package.OpenAudio().Dispose()));
        Assert.Equal(DcSongReadFailure.ChecksumMismatch, Refusal(package.VerifyAll));
    }

    [Fact]
    public void ATamperedChart_FailsItsChecksum()
    {
        var path = PackagePath();
        var members = MinimalMembers(_dir);
        members[0] = members[0] with { Deflate = false }; // stored, so the byte flip lands in the XML itself
        BuildPackage(path, members);
        FlipByteInside(path, members[0].Bytes);

        using var package = DcSongPackage.Open(path);
        Assert.Equal(DcSongReadFailure.ChecksumMismatch, Refusal(() => package.LoadChart()));
    }

    [Fact]
    public void AMemberWhoseSizeDisagreesWithTheManifest_IsRefusedAtOpen()
    {
        var path = PackagePath();
        var members = MinimalMembers(_dir);
        var files = members.Select(m => new DcSongFileEntry(m.Path,
            m.Path == "chart.rscp" ? DcSongFileRole.Chart : DcSongFileRole.Audio,
            m.Bytes.Length + (m.Path == "audio/song.opus" ? 1 : 0), Sha(m.Bytes))).ToList();
        BuildPackage(path, members, files);

        Assert.Equal(DcSongReadFailure.ChecksumMismatch, Refusal(() => DcSongPackage.Open(path)));
    }

    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("audio/../../evil.txt")]
    [InlineData("/rooted.txt")]
    [InlineData("C:/Windows/evil.txt")]
    [InlineData("\\\\server\\share\\evil.txt")]
    public void AMemberThatClimbsOutOfThePackage_IsRefused(string name)
    {
        var path = PackagePath();
        var members = MinimalMembers(_dir);
        members.Add(new Member(name, RandomBytes(100, 5)));
        BuildPackage(path, members);

        Assert.Equal(DcSongReadFailure.UnsafePath, Refusal(() => DcSongPackage.Open(path)));
    }

    [Fact]
    public void AManifestThatNamesAClimbingPath_IsRefused()
    {
        var path = PackagePath();
        var members = MinimalMembers(_dir);
        var files = members.Select(m => new DcSongFileEntry(m.Path,
            m.Path == "chart.rscp" ? DcSongFileRole.Chart : DcSongFileRole.Audio, m.Bytes.Length, Sha(m.Bytes))).ToList();
        files.Add(new DcSongFileEntry("../outside.bin", DcSongFileRole.Other, 1, new string('0', 64)));
        BuildPackage(path, members, files);

        Assert.Equal(DcSongReadFailure.UnsafePath, Refusal(() => DcSongPackage.Open(path)));
    }

    [Fact]
    public void AChartThatInflatesPastTheCap_IsRefusedOnItsDeclaredSize()
    {
        // 80 MB of zeros deflates to ~80 KB: a classic bomb.
        var bomb = new byte[80 * 1024 * 1024];
        var path = PackagePath();
        BuildPackage(path, [new Member("chart.rscp", bomb, Deflate: true), new Member("audio/song.opus", RandomBytes(4096, 1))]);
        Assert.True(new FileInfo(path).Length < 1024 * 1024, "the bomb should be small on disk");

        using var package = DcSongPackage.Open(path);
        Assert.Equal(DcSongReadFailure.EntryTooLarge, Refusal(() => package.LoadChart()));
    }

    [Fact]
    public void AChartThatProducesMoreThanItDeclares_IsRefused()
    {
        // The SENDER writes the declared size. Here it lies: the central record and
        // the manifest both say 1,000 bytes, while the member really inflates to 2 MB.
        // Only counting what the inflater produces catches that.
        var real = new byte[2 * 1024 * 1024];
        Array.Fill(real, (byte)'a');
        const int declared = 1000;
        var path = PackagePath();
        var audio = RandomBytes(4096, 1);
        var files = new List<DcSongFileEntry>
        {
            new("chart.rscp", DcSongFileRole.Chart, declared, Sha(real[..declared])),
            new("audio/song.opus", DcSongFileRole.Audio, audio.Length, Sha(audio))
        };
        BuildPackage(path, [new Member("chart.rscp", real, Deflate: true), new Member("audio/song.opus", audio)], files);
        PatchCentralUncompressedSize(path, "chart.rscp", declared);

        using var package = DcSongPackage.Open(path);
        Assert.Equal(DcSongReadFailure.EntryTooLarge, Refusal(() => package.LoadChart()));
    }

    [Fact]
    public void TooManyMembers_IsRefusedBeforeAnyIsRead()
    {
        var path = PackagePath();
        var members = MinimalMembers(_dir);
        for (var i = 0; i < DcSongFormat.MaxEntries; i++) members.Add(new Member($"extras/{i}.bin", [1]));
        BuildPackage(path, members);

        Assert.Equal(DcSongReadFailure.TooManyEntries, Refusal(() => DcSongPackage.Open(path)));
    }

    [Fact]
    public void AMemberTheManifestDoesNotList_IsRefused()
    {
        var path = PackagePath();
        var members = MinimalMembers(_dir);
        var files = members.Select(m => new DcSongFileEntry(m.Path,
            m.Path == "chart.rscp" ? DcSongFileRole.Chart : DcSongFileRole.Audio, m.Bytes.Length, Sha(m.Bytes))).ToList();
        members.Add(new Member("smuggled.exe", RandomBytes(64, 2)));
        BuildPackage(path, members, files);

        Assert.Equal(DcSongReadFailure.EntryUnlisted, Refusal(() => DcSongPackage.Open(path)));
    }

    [Fact]
    public void AListedMemberTheZipLacks_IsRefused()
    {
        var path = PackagePath();
        var members = MinimalMembers(_dir);
        var files = members.Select(m => new DcSongFileEntry(m.Path,
            m.Path == "chart.rscp" ? DcSongFileRole.Chart : DcSongFileRole.Audio, m.Bytes.Length, Sha(m.Bytes))).ToList();
        BuildPackage(path, [members[0]], files);

        Assert.Equal(DcSongReadFailure.EntryMissing, Refusal(() => DcSongPackage.Open(path)));
    }

    [Fact]
    public void ATruncatedDownload_IsRefusedAsUnreadable()
    {
        var path = PackagePath();
        BuildPackage(path, MinimalMembers(_dir));
        var bytes = File.ReadAllBytes(path);
        File.WriteAllBytes(path, bytes[..(bytes.Length - 40)]);

        Assert.Equal(DcSongReadFailure.Unreadable, Refusal(() => DcSongPackage.Open(path)));
    }

    [Fact]
    public void SomethingThatIsNotAZip_IsRefusedAsUnreadable()
    {
        var path = PackagePath();
        File.WriteAllBytes(path, RandomBytes(5000, 8));
        Assert.Equal(DcSongReadFailure.Unreadable, Refusal(() => DcSongPackage.Open(path)));

        Assert.Equal(DcSongReadFailure.Unreadable, Refusal(() => DcSongPackage.Open(PackagePath("missing"))));
    }

    [Fact]
    public void AZipWithNoManifest_IsRefused()
    {
        var path = PackagePath();
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        using (var entry = zip.CreateEntry("chart.rscp").Open())
            entry.Write(ChartBytes(_dir));

        Assert.Equal(DcSongReadFailure.ManifestMissing, Refusal(() => DcSongPackage.Open(path)));
    }

    [Fact]
    public void CompressedAudio_IsRefused_BecauseItCouldNotBeSeekedOrBounded()
    {
        var path = PackagePath();
        var members = MinimalMembers(_dir);
        members[1] = members[1] with { Deflate = true };
        BuildPackage(path, members);

        Assert.Equal(DcSongReadFailure.Malformed, Refusal(() => DcSongPackage.Open(path)));
    }

    [Fact]
    public void AChartThatAsksToRecoverFromSiblingFiles_IsRefused()
    {
        // A pre-v3 project names sibling XML files and NO embedded arrangements; the
        // .rscp reader would follow those names, rooted ones included, out of the
        // package. The writer never produces one, so the reader refuses it.
        var xml = """
                  <?xml version='1.0' encoding='UTF-8'?>
                  <rscProject>
                    <chartFormatVersion>2</chartFormatVersion>
                    <musicFileName>audio/song.opus</musicFileName>
                    <arrangementFiles><string>C:\Windows\win.ini</string></arrangementFiles>
                    <arrangements />
                  </rscProject>
                  """;
        var path = PackagePath();
        BuildPackage(path, [new Member("chart.rscp", Encoding.UTF8.GetBytes(xml), Deflate: true), new Member("audio/song.opus", RandomBytes(4096, 1))]);

        using var package = DcSongPackage.Open(path);
        Assert.Equal(DcSongReadFailure.ChartInvalid, Refusal(() => package.LoadChart()));
    }

    [Fact]
    public void AnImageThatIsNotWhatItsManifestSays_IsRefused()
    {
        var path = PackagePath();
        var members = MinimalMembers(_dir);
        var fakePng = RandomBytes(200, 6);
        members.Add(new Member("images/cover.png", fakePng));
        var files = members.Select(m => new DcSongFileEntry(m.Path, m.Path switch
        {
            "chart.rscp" => DcSongFileRole.Chart,
            "audio/song.opus" => DcSongFileRole.Audio,
            _ => DcSongFileRole.Cover
        }, m.Bytes.Length, Sha(m.Bytes), MediaType: m.Path.EndsWith(".png", StringComparison.Ordinal) ? DcSongImages.Png : null)).ToList();
        BuildPackage(path, members, files);

        using var package = DcSongPackage.Open(path);
        Assert.Equal(DcSongReadFailure.ManifestInvalid, Refusal(() => package.ReadCover()));
    }

    [Fact]
    public void EveryFailure_NamesAResxKeyOfItsOwn()
    {
        var keys = Enum.GetValues<DcSongReadFailure>().Select(DcSongReadException.KeyFor).ToList();
        Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());
        Assert.All(keys, k => Assert.StartsWith("DcSong_ReadFailure_", k, StringComparison.Ordinal));
    }
}
