using System.Text.Json;
using System.Text.RegularExpressions;
using DotCharter.Services.Audio.StemSeparation.Cache;
using DotCharter.Services.Import.Audio;
using DotCharter.Services.Import.Youtube;

namespace DotCharter.Tests.Infrastructure;

/// <summary>
///     Pins <c>tools/release/ffmpeg-min/components.json</c> — the components the
///     release's minimal ffmpeg is built with (#409) — against the argument
///     vectors the app actually passes ffmpeg. The release ships ONLY these, so a
///     new encoder, muxer, filter or call site that is not in the inventory would
///     build clean, pass every unit test that fakes the process, and fail on every
///     user's machine. Every rule here fails the gate instead.
/// </summary>
public sealed partial class FfmpegComponentInventoryTests
{
    private static readonly string[] ProductProjects =
    [
        "DotCharter.Core", "DotCharter.Analysis", "DotCharter.Rendering", "DotCharter.Localization",
        "DotCharter.Tone", "DotCharter.Services", "DotCharter.Presentation", "DotCharter.Tone.UI",
        "DotCharter.UI", "DotCharter.RigLab"
    ];

    /// <summary>Output extension → the muxer ffmpeg picks for it.</summary>
    private static readonly Dictionary<string, string> MuxerByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        ["opus"] = "opus", ["flac"] = "flac", ["ogg"] = "ogg", ["wav"] = "wav", ["mp3"] = "mp3", ["png"] = "image2"
    };

    /// <summary>yt-dlp <c>--audio-format</c> → the encoder and muxer it makes ffmpeg use.</summary>
    private static readonly Dictionary<string, (string Encoder, string Muxer)> YtDlpAudioFormats = new(StringComparer.OrdinalIgnoreCase)
    {
        ["mp3"] = ("libmp3lame", "mp3"), ["opus"] = ("libopus", "opus"), ["vorbis"] = ("libvorbis", "ogg"),
        ["flac"] = ("flac", "flac"), ["wav"] = ("pcm_s16le", "wav")
    };

    private static JsonElement Inventory() =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(TestPaths.RepoRoot(), "tools", "release", "ffmpeg-min", "components.json")))
            .RootElement;

    private static HashSet<string> Names(JsonElement owner, string kind) =>
        owner.TryGetProperty(kind, out var list)
            ? list.EnumerateArray().Select(e => e.GetString()!).ToHashSet(StringComparer.Ordinal)
            : [];

    private static IEnumerable<JsonElement> CallSites() => Inventory().GetProperty("callSites").EnumerateArray();

    private static string CallSitePath(JsonElement site) =>
        Path.Combine(TestPaths.RepoRoot(), site.GetProperty("file").GetString()!.Replace('/', Path.DirectorySeparatorChar));

    private static void AssertInInventory(string kind, IEnumerable<string> used, string where)
    {
        var have = Names(Inventory(), kind);
        var missing = used.Where(n => !have.Contains(n)).Distinct().ToList();
        Assert.True(missing.Count == 0,
            $"{where} needs ffmpeg {kind} [{string.Join(", ", missing)}], which tools/release/ffmpeg-min/components.json " +
            "does not list: the release's minimal ffmpeg would not have it. Add it there (and rebuild with Build-MinimalFfmpeg.ps1).");
    }

    /// <summary>
    ///     Every product file that starts ffmpeg (an <c>-i</c> argument vector, yt-dlp's
    ///     <c>--ffmpeg-location</c>, or a call into <c>TryRunFfmpeg</c>) is a listed call
    ///     site, so a new job cannot land without someone writing down what it needs.
    /// </summary>
    [Fact]
    public void Every_ffmpeg_call_site_in_the_product_is_in_the_inventory()
    {
        var root = TestPaths.RepoRoot();
        var listed = CallSites().Select(s => s.GetProperty("file").GetString()!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var found = ProductProjects
            .Select(p => Path.Combine(root, p))
            .Where(Directory.Exists)
            .SelectMany(d => Directory.EnumerateFiles(d, "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => StartsFfmpeg().IsMatch(File.ReadAllText(f)))
            .Select(f => Path.GetRelativePath(root, f).Replace(Path.DirectorySeparatorChar, '/'))
            .ToList();

        Assert.True(found.Count >= 9, $"the call-site scan found only {found.Count} files; it has stopped seeing the code.");
        var unlisted = found.Where(f => !listed.Contains(f)).ToList();
        Assert.True(unlisted.Count == 0,
            $"These files run ffmpeg but are not a callSites entry in components.json: {string.Join(", ", unlisted)}");
    }

    /// <summary>A call site that no longer exists is a stale entry, not a harmless one.</summary>
    [Fact]
    public void Every_listed_call_site_exists_and_still_runs_ffmpeg()
    {
        foreach (var site in CallSites())
        {
            var path = CallSitePath(site);
            Assert.True(File.Exists(path), $"components.json lists {site.GetProperty("file").GetString()}, which does not exist.");
            Assert.Matches(StartsFfmpeg(), File.ReadAllText(path));
        }
    }

    /// <summary>
    ///     The literal <c>-c:a</c> encoders and <c>-f</c> muxers in each call site are what
    ///     that site declares, and the inventory has them; <c>-ar</c>/<c>-ac</c> need the
    ///     resampler, <c>-af</c> a declared filter.
    /// </summary>
    [Fact]
    public void Each_call_sites_literal_codecs_and_formats_are_declared_and_in_the_inventory()
    {
        foreach (var site in CallSites())
        {
            var file = site.GetProperty("file").GetString()!;
            var text = File.ReadAllText(CallSitePath(site));

            var encoders = EncoderLiteral().Matches(text).Select(m => m.Groups[1].Value).ToList();
            var muxers = MuxerLiteral().Matches(text).Select(m => m.Groups[1].Value).ToList();
            Assert.Empty(encoders.Except(Names(site, "encoders")));
            Assert.Empty(muxers.Except(Names(site, "muxers")));
            AssertInInventory("encoders", encoders, file);
            AssertInInventory("muxers", muxers, file);

            if (text.Contains("\"-ar\"", StringComparison.Ordinal) || text.Contains("\"-ac\"", StringComparison.Ordinal))
                Assert.Contains("aresample", Names(site, "filters"));
            if (text.Contains("\"-af\"", StringComparison.Ordinal))
                Assert.NotEmpty(Names(site, "filters"));
        }
    }

    /// <summary>What a call site declares is a subset of what the build is configured with.</summary>
    [Fact]
    public void Every_call_site_declaration_is_in_the_inventory()
    {
        foreach (var site in CallSites())
        {
            var file = site.GetProperty("file").GetString()!;
            foreach (var kind in new[] { "decoders", "encoders", "muxers", "demuxers", "filters", "protocols", "bsfs" })
                AssertInInventory(kind, Names(site, kind), file);
        }
    }

    /// <summary>The filters the silence bake and splice really emit.</summary>
    [Fact]
    public void The_silence_filter_graph_uses_only_inventory_filters()
    {
        var graph = ImportAudioPreparer.BuildSilenceFilter(1.25, 2.5);
        Assert.NotNull(graph);
        var filters = graph!.Split(',').Select(part => part.Split('=')[0]).ToList();
        Assert.Equal(["adelay", "apad"], filters);
        AssertInInventory("filters", filters, "ImportAudioPreparer.BuildSilenceFilter");

        var encoders = new[]
            {
                ImportAudioPreparer.BuildEncodeArgs("in.mp3", "out.ogg", graph),
                AudioSilenceSplicer.BuildStemEncodeArgs("in.wav", "out.wav", graph)
            }
            .Select(args => args[args.ToList().IndexOf("-c:a") + 1]);
        AssertInInventory("encoders", encoders, "the silence bake / splice argument vectors");
    }

    /// <summary>Every stem codec the cache can write has its encoder's muxer in the build.</summary>
    [Fact]
    public void Every_stem_cache_codec_has_its_muxer()
    {
        foreach (var codec in Enum.GetValues<StemCodec>())
        {
            var ext = codec.FileExtension();
            Assert.True(MuxerByExtension.TryGetValue(ext, out var muxer), $"no muxer mapping for stem extension .{ext}.");
            AssertInInventory("muxers", [muxer], $"StemCodec.{codec}");
        }
    }

    /// <summary>yt-dlp's <c>--audio-format</c> decides an encoder in OUR ffmpeg.</summary>
    [Fact]
    public void The_youtube_audio_format_has_its_encoder_and_muxer()
    {
        var args = YtDlpAudioDownloader.BuildArguments("C:/tools", "out.%(ext)s", "https://example.invalid/v");
        var format = args[args.ToList().IndexOf("--audio-format") + 1];
        Assert.True(YtDlpAudioFormats.TryGetValue(format, out var needs), $"unknown yt-dlp --audio-format '{format}'.");
        AssertInInventory("encoders", [needs.Encoder], "YtDlpAudioDownloader --audio-format");
        AssertInInventory("muxers", [needs.Muxer], "YtDlpAudioDownloader --audio-format");
    }

    /// <summary>Every audio type the import picker offers can be demuxed by the build.</summary>
    [Fact]
    public void Every_import_audio_type_has_its_demuxer()
    {
        var map = Inventory().GetProperty("importExtensions");
        foreach (var ext in AudioFileTypes.Extensions)
        {
            Assert.True(map.TryGetProperty(ext, out var demuxers),
                $"components.json importExtensions has no entry for {ext}, which AudioFileTypes offers.");
            AssertInInventory("demuxers", demuxers.EnumerateArray().Select(e => e.GetString()!), $"importing {ext}");
        }
    }

    [GeneratedRegex("\"-i\"|\"--ffmpeg-location\"|TryRunFfmpeg\\(")]
    private static partial Regex StartsFfmpeg();

    [GeneratedRegex("\"-c:a\",\\s*\"([A-Za-z0-9_]+)\"")]
    private static partial Regex EncoderLiteral();

    [GeneratedRegex("\"-f\",\\s*\"([A-Za-z0-9_]+)\"")]
    private static partial Regex MuxerLiteral();
}
