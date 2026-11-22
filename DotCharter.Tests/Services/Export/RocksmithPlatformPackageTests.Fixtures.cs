using System.Security.Cryptography;
using System.Text.RegularExpressions;
using DotCharter.Services.Export;
using DotCharter.Tests.Services.Export.Rs7;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Export;

/// <summary>
///     #782: the Rocksmith package per platform. Adding the Mac package preserves the
///     PC package bytes: every random id, the SNG IV and the synthetic keys are pinned.
///     The pre-#782 package hashes advanced for #565 row 2's shared song-length policy.
///     The Mac package differs from it in exactly
///     what Rocksmith2014.NET's <c>Platform.fs</c> / <c>GraphItem.fs</c> and official PC/Mac
///     pairs differ in: the SNG folder and key, the audio folder and the graph's platform tags.
/// </summary>
public sealed partial class RocksmithPlatformPackageTests : IDisposable
{
    /// <summary>Encrypted PC package pin, advanced for #875 (generated show lights replace the fixed fallback).</summary>
    private const string PinnedEncryptedPcSha256 = "b23f444e08caac184f1916f6dc6562f6b36898f08e6cc4cafd77c46e8aa58ccd";

    /// <summary>Plain PC package pin, advanced for #875 (generated show lights replace the fixed fallback).</summary>
    private const string PinnedPlainPcSha256 = "9de1596ce9f1961f480f26884cc21e51d1b200ace85af58dbc5d05ef29b0e327";

    private readonly string _tempDir = Directory.CreateTempSubdirectory("dotcharter-platform-").FullName;

    public void Dispose() => BestEffortDelete.Directory(_tempDir);

    private async Task<RocksmithBuildResult> BuildPinnedAsync(string fileName, ExportPlatform platform, bool plain = false)
    {
        var path = Path.Combine(_tempDir, fileName);
        return await new RocksmithPackageBuilder(new FakeAppLogger()).BuildPsarcAsync(RocksmithPackageBuilderTests.CreateProject(),
            new RocksmithBuildOptions
            {
                OutputPath = path,
                Platform = platform,
                AudioPath = DeterministicWem(),
                DlcKey = "PinnedGolden",
                SongLengthMs = 32_000,
                PlainPackage = plain,
                Identity = PinnedIdentity()
            }, cancellationToken: TestContext.Current.CancellationToken);
    }

    /// <summary>Every id the build would draw, pinned. Graph items: xblock, 2 per arrangement
    /// (Lead + Vocals), showlights, hsan, three album-art sizes, two banks.</summary>
    internal static RocksmithBuildIdentity PinnedIdentity() => new()
    {
        ConversionTime = new DateTime(2026, 10, 13, 12, 34, 0, DateTimeKind.Unspecified),
        // Pinned too: the package carries the tool version, which would otherwise follow every version bump.
        ToolVersion = "1.3.0",
        Arrangements =
        [
            new RsArrangementIdentity(Guid.Parse("0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0"), 1_234_567),
            new RsArrangementIdentity(Guid.Parse("1f2e3d4c-5b6a-7988-9796-b5c4d3e2f101"), 7_654_321)
        ],
        GraphItems = [.. Enumerable.Range(1, 12).Select(i => new RsGraphItemIdentity(
            new Guid(i, 0x11, 0x22, [1, 2, 3, 4, 5, 6, 7, (byte)i]), new Guid(1000 + i, 0, 0, new byte[8])))],
        MainBank = new SoundBankIds(0x1111_2222, 0x0333_4444, 0x0555_6666, 0x7777_8888, 0x0999_AAAA),
        PreviewBank = new SoundBankIds(0x2111_2222, 0x0433_4444, 0x0655_6666, 0x8777_8888, 0x0999_AAAA),
        SngIv = [.. Enumerable.Range(0, 16).Select(i => (byte)(0xA0 + i))]
    };

    private static string ToMacName(string pcName) =>
        pcName.Replace("songs/bin/generic/", "songs/bin/macos/", StringComparison.Ordinal)
            .Replace("audio/windows/", "audio/mac/", StringComparison.Ordinal);

    /// <summary>One graph item as (canonical, sorted tags): its platform-relevant shape.</summary>
    internal static IEnumerable<string> GraphShape(string graph) =>
        GraphLine().Matches(graph)
            .GroupBy(m => m.Groups["uuid"].Value)
            .Select(item =>
            {
                var canonical = item.First(m => m.Groups["pred"].Value == "canonical").Groups["value"].Value;
                var tags = item.Where(m => m.Groups["pred"].Value == "tag").Select(m => m.Groups["value"].Value).Order(StringComparer.Ordinal);
                return $"{canonical}|{string.Join(",", tags)}";
            });

    /// <summary>What a PC graph item becomes on the Mac (GraphItem.fs): the folders move, dx9
    /// becomes macos, and the SNG gains the macos tag.</summary>
    private static string ExpectedMacShape(string pcShape)
    {
        var mac = pcShape.Replace("/songs/bin/generic", "/songs/bin/macos", StringComparison.Ordinal)
            .Replace("/audio/windows", "/audio/mac", StringComparison.Ordinal);
        var parts = mac.Split('|');
        var tags = parts[1].Split(',').Select(t => t == "dx9" ? "macos" : t).ToList();
        if (parts[0] == "/songs/bin/macos") tags.Add("macos");
        return $"{parts[0]}|{string.Join(",", tags.Order(StringComparer.Ordinal))}";
    }

    [GeneratedRegex("<urn:uuid:(?<uuid>[^>]+)> <http://emergent.net/aweb/1.0/(?<pred>\\w+)> \"(?<value>[^\"]*)\"")]
    private static partial Regex GraphLine();

    internal static bool TocEncrypted(string psarc)
    {
        using var file = File.OpenRead(psarc);
        var header = new byte[32];
        file.ReadExactly(header);
        return (header[31] & 4) != 0;
    }

    private static List<Rs7Sound> Sounds(Arrangement arrangement, IList<BeatMarker> beats) => Rs7TestSupport.Sounds(arrangement, beats);

    private string DeterministicWem()
    {
        var path = Path.Combine(_tempDir, "audio.wem");
        if (File.Exists(path)) return path;
        var data = new byte[80_000];
        new Random(42).NextBytes(data);
        File.WriteAllBytes(path, data);
        return path;
    }

    private static string Sha256(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
}
