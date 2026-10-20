using DotCharter.Services.Export;
using DotCharter.Tests.Services.Export.Rs7;

namespace DotCharter.Tests.Services.Export;

/// <summary>The tests of <see cref="RocksmithPlatformPackageTests" /> that need no package reader; its fixtures are in <c>RocksmithPlatformPackageTests.Fixtures.cs</c>.</summary>
public sealed partial class RocksmithPlatformPackageTests
{
    [Theory]
    [InlineData(false, PinnedEncryptedPcSha256)]
    [InlineData(true, PinnedPlainPcSha256)]
    public async Task Pc_package_is_byte_identical_to_the_build_before_the_mac_package(bool plain, string pinned)
    {
        using var keys = TestRocksmithKeys.UseSynthetic();
        var first = await BuildPinnedAsync("pinned_p.psarc", ExportPlatform.Pc, plain);
        var second = await BuildPinnedAsync("again_p.psarc", ExportPlatform.Pc, plain);

        // The pin is only worth something when the build is a pure function of its input.
        Assert.Equal(Sha256(first.PsarcPath), Sha256(second.PsarcPath));
        Assert.True(pinned == Sha256(first.PsarcPath), $"PC package hash {Sha256(first.PsarcPath)} != pinned {pinned}");
    }

    /// <summary>The PC half of a PC + Mac export is the PC-only package, byte for byte.</summary>
    [Fact]
    public async Task Pc_and_mac_writes_the_unchanged_pc_package_and_a_mac_package_beside_it()
    {
        using var keys = TestRocksmithKeys.UseSynthetic();
        var result = await BuildPinnedAsync("pair_p.psarc", ExportPlatform.PcAndMac);

        var pc = Path.Combine(_tempDir, "pair_p.psarc");
        var mac = Path.Combine(_tempDir, "pair_m.psarc");
        Assert.Equal([pc, mac], result.PackagePaths);
        Assert.Equal(pc, result.PsarcPath);
        Assert.Equal(PinnedEncryptedPcSha256, Sha256(pc));
        Assert.True(File.Exists(mac));
    }

    [Fact]
    public async Task Without_the_mac_key_mac_alone_fails_and_pc_and_mac_writes_pc_with_the_reason()
    {
        using var keys = TestRocksmithKeys.UseKeys(TestRocksmithKeys.SyntheticToc, TestRocksmithKeys.SyntheticSngPc, null);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => BuildPinnedAsync("nokey_m.psarc", ExportPlatform.Mac));
        Assert.Contains("always encrypted", error.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_tempDir, "nokey_m.psarc")));

        var result = await BuildPinnedAsync("nokey_p.psarc", ExportPlatform.PcAndMac);
        Assert.Equal([Path.Combine(_tempDir, "nokey_p.psarc")], result.PackagePaths);
        Assert.Contains(result.Warnings, w => w.StartsWith("Only the PC package was written", StringComparison.Ordinal));
        Assert.False(File.Exists(Path.Combine(_tempDir, "nokey_m.psarc")));
    }

    /// <summary>A Mac package is never plain: asking for a plain build drops it.</summary>
    [Fact]
    public async Task A_plain_build_writes_no_mac_package()
    {
        using var keys = TestRocksmithKeys.UseSynthetic();
        await Assert.ThrowsAsync<InvalidDataException>(() => BuildPinnedAsync("plain_m.psarc", ExportPlatform.Mac, plain: true));

        var result = await BuildPinnedAsync("plain_p.psarc", ExportPlatform.PcAndMac, plain: true);
        Assert.Equal([Path.Combine(_tempDir, "plain_p.psarc")], result.PackagePaths);
    }

    /// <summary>rs7string is Windows-only: a 7-string guitar exports PC only.</summary>
    [Fact]
    public async Task A_seven_string_guitar_writes_no_mac_package()
    {
        using var keys = TestRocksmithKeys.UseSynthetic();
        var builder = new RocksmithPackageBuilder(new FakeAppLogger());
        Task<RocksmithBuildResult> Build(string name, ExportPlatform platform) => builder.BuildPsarcAsync(
            Rs7PackageExportTests.CreateProject(7), new RocksmithBuildOptions
            {
                OutputPath = Path.Combine(_tempDir, name), AudioPath = DeterministicWem(), Platform = platform,
                SongLengthMs = 60_000
            }, cancellationToken: TestContext.Current.CancellationToken);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => Build("seven_m.psarc", ExportPlatform.Mac));
        Assert.Contains("rs7string", error.Message, StringComparison.Ordinal);

        var result = await Build("seven_p.psarc", ExportPlatform.PcAndMac);
        Assert.Equal([Path.Combine(_tempDir, "seven_p.psarc")], result.PackagePaths);
        Assert.Contains(result.Warnings, w => w.Contains("rs7string", StringComparison.Ordinal));
        Assert.False(File.Exists(Path.Combine(_tempDir, "seven_m.psarc")));
    }

    /// <summary>The #747 ASCII/key rules hold for the Mac entry names too (a numbered wem
    /// in <c>audio/mac</c> is a fixed name, nothing else is).</summary>
    [Fact]
    public void Entry_name_rules_accept_the_mac_folders_and_nothing_looser()
    {
        RocksmithPackageBuilder.EnsureEntryNamesFromKey(
            ["songs/bin/macos/abcdef_lead.sng", "audio/mac/song_abcdef.bnk", "audio/mac/123456.wem", "appid.appid"], "abcdef");
        Assert.Throws<InvalidOperationException>(() =>
            RocksmithPackageBuilder.EnsureEntryNamesFromKey(["audio/mac/other.wem"], "abcdef"));
        Assert.Throws<InvalidOperationException>(() =>
            RocksmithPackageBuilder.EnsureEntryNamesFromKey(["songs/bin/macos/abcdéf_lead.sng"], "abcdef"));
    }
}
