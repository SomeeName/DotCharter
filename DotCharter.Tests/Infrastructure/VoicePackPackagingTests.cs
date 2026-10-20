using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace DotCharter.Tests.Infrastructure;

/// <summary>
///     The built-in guitar / bass voice plays real recordings from eight sample
///     packs (218 MB losslessly coded, 594 MB of audio once loaded, #408: two Karoryfer sets, one EGFxSet Strat pack per pickup
///     position, #50, and the guitar articulation pack from Guitar-TECHS, #52) that are NOT in git. Four links make them reach a user,
///     and each one fails silently: without a pack the voice falls back to its
///     string model and still makes a sound, so nobody hears that the release
///     shipped the fallback. This pins every link.
/// </summary>
/// <remarks>
///     1. the committed MANIFEST pins every source file (a URL at a commit, a
///        SHA-256) and lists only CC0 / CC BY sources — no separately licensed
///        source, and no recorded bass slap, is in it (JuceSharp #69);
///     2. <c>DotCharter.UI.csproj</c> copies <c>artifacts/voice-pack/</c> into
///        <c>voices/</c> beside the exe, for a build AND a publish;
///     3. <c>tools/release/New-Release.ps1</c> builds missing AND stale packs
///        (their <c>voice-pack.stamp</c> against the manifest and the builder)
///        before it publishes and refuses a publish layout without them;
///     4. <c>artifacts/</c> stays git-ignored, so a pack is never committed.
/// </remarks>
public sealed class VoicePackPackagingTests
{
    private static string Root => TestPaths.RepoRoot();

    /// <summary>One pack per Stratocaster pickup position (#50), as the native
    /// model table (dcvoice_models.h) names them.</summary>
    private static readonly string[] StratPacks =
        ["strat-neck", "strat-neck-middle", "strat-middle", "strat-middle-bridge", "strat-bridge"];

    /// <summary>The guitar articulation pack every guitar model loads beside its own (#52).</summary>
    private const string ArtsPack = "guitar-arts";

    [Fact]
    public void The_manifest_pins_every_source_by_commit_and_hash_and_lists_only_free_licences()
    {
        var path = Path.Combine(Root, "JuceSharp", "src", "JuceSharp.Native", "tools", "voice", "voice_pack_manifest.json");
        Assert.SkipUnless(File.Exists(path), "JuceSharp is not checked out in this worktree.");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var repos = doc.RootElement.GetProperty("repos");
        var commits = new Dictionary<string, (string Repo, string Commit)>();
        foreach (var r in repos.EnumerateObject())
        {
            var licence = r.Value.GetProperty("license").GetString();
            Assert.True(licence is "CC0-1.0" or "CC-BY-4.0", $"{r.Name} is {licence}: only CC0 / CC BY material may ship");
            Assert.False(r.Value.TryGetProperty("private", out _), $"{r.Name} is marked \"private\": no separately licensed source may come back");
            var commit = r.Value.GetProperty("commit").GetString()!;
            Assert.Matches("^[0-9a-f]{40}$", commit);
            commits[r.Name] = (r.Value.GetProperty("repo").GetString()!, commit);
        }

        // A pinned ARCHIVE (EGFxSet's Clean.zip on Zenodo, #50): its own SHA-256
        // and the publisher's MD5, and a free licence like any repository.
        var archives = new Dictionary<string, string>();
        if (doc.RootElement.TryGetProperty("archives", out var arch))
        {
            foreach (var a in arch.EnumerateObject())
            {
                var licence = a.Value.GetProperty("license").GetString();
                Assert.True(licence is "CC0-1.0" or "CC-BY-4.0", $"{a.Name} is {licence}: only CC0 / CC BY material may ship");
                Assert.Matches("^[0-9a-f]{64}$", a.Value.GetProperty("sha256").GetString()!);
                Assert.Matches("^[0-9a-f]{32}$", a.Value.GetProperty("md5").GetString()!);
                archives[a.Name] = a.Value.GetProperty("url").GetString()!;
            }
        }

        var files = doc.RootElement.GetProperty("files").EnumerateArray().ToList();
        Assert.True(files.Count > 500, $"only {files.Count} files in the manifest — the selection is broken");
        foreach (var f in files)
        {
            var key = f.GetProperty("repo").GetString()!;
            var url = f.GetProperty("url").GetString()!;
            if (archives.TryGetValue(key, out var archiveUrl))
            {
                Assert.StartsWith(archiveUrl + "#", url, StringComparison.Ordinal);
            }
            else
            {
                var (repo, commit) = commits[key];
                Assert.StartsWith($"https://raw.githubusercontent.com/{repo}/{commit}/", url, StringComparison.Ordinal);
            }
            Assert.Matches("^[0-9a-f]{64}$", f.GetProperty("sha256").GetString()!);
        }

        // Both instruments are covered, with the articulations the voice plays
        // from recordings (0 sustain, 1 palm, 3 release; guitar 2 legato, 5 dead;
        // bass 4 ghost, 6 slap thumb). The recorded slaps (9, #109) came only from
        // the separately licensed source and left with it (JuceSharp #69).
        var arts = files.Select(f => (f.GetProperty("instrument").GetString(), f.GetProperty("art").GetInt32())).ToHashSet();
        foreach (var need in new[] { ("guitar", 0), ("guitar", 1), ("guitar", 2), ("guitar", 3), ("guitar", 5),
                                     ("bass", 0), ("bass", 1), ("bass", 3), ("bass", 4), ("bass", 6) })
        {
            Assert.Contains(need, arts);
        }
        Assert.DoesNotContain(("bass", 9), arts);
        // #50: every Strat pickup position, every string and fret 0..22 of it.
        foreach (var pack in StratPacks)
        {
            Assert.Equal(6 * 23, files.Count(f => f.GetProperty("instrument").GetString() == pack));
        }
        // #52: the shared guitar articulations — real palm mutes (1), natural (7)
        // and pinch (8) harmonics — on every string, each note cut out of a long
        // recording at a pinned offset.
        var artsFiles = files.Where(f => f.GetProperty("instrument").GetString() == ArtsPack).ToList();
        foreach (var art in new[] { 1, 7, 8 })
        {
            var strings = artsFiles.Where(f => f.GetProperty("art").GetInt32() == art).Select(f => f.GetProperty("string").GetInt32()).ToHashSet();
            Assert.True(strings.SetEquals([0, 1, 2, 3, 4, 5]), $"articulation {art} is not recorded on every string");
        }
        Assert.All(artsFiles, f => Assert.True(f.GetProperty("offset").GetDouble() >= 0.0 && f.GetProperty("length").GetDouble() > 0.1));
        Assert.True(artsFiles.Count(f => f.GetProperty("art").GetInt32() == 1) >= 6 * 20, "fewer palm mutes than six strings x twenty frets");
    }

    [Fact]
    public void The_app_build_and_publish_copy_the_packs_into_voices()
    {
        var csproj = XDocument.Load(Path.Combine(Root, "DotCharter.UI", "DotCharter.UI.csproj"));
        var item = csproj.Descendants("Content")
            .FirstOrDefault(c => ((string?)c.Attribute("Include") ?? "").Contains(@"artifacts\voice-pack\*.dcvp", StringComparison.Ordinal));
        Assert.NotNull(item);
        Assert.Equal("voices", (string?)item!.Attribute("LinkBase"));
        Assert.Contains("VOICE-CREDITS.txt", (string?)item.Attribute("Include") ?? "", StringComparison.Ordinal);
        Assert.Equal("PreserveNewest", item.Element("CopyToOutputDirectory")?.Value);
        Assert.Equal("PreserveNewest", item.Element("CopyToPublishDirectory")?.Value);
        // #757, #767: the separately licensed packs never ride along into voices/.
        Assert.DoesNotContain("voice-pack-nc", File.ReadAllText(Path.Combine(Root, "DotCharter.UI", "DotCharter.UI.csproj")), StringComparison.Ordinal);
    }

    /// <summary>
    ///     #757, #767: the release's packs hold only recordings that may ship with the
    ///     GPL program, and no release builds or uploads the non-commercial pack. The
    ///     release asks the builder for the plain build only, refuses a <c>voices/</c>
    ///     with a recorded slap or a pack licence (in preflight AND in the publish
    ///     layout), and has no separate voice-pack asset.
    /// </summary>
    [Fact]
    public void No_release_builds_or_uploads_the_non_commercial_voice_pack()
    {
        var script = File.ReadAllText(Path.Combine(Root, "tools", "release", "New-Release.ps1")).ReplaceLineEndings("\n");
        Assert.Contains("& python $packer build --out $packDir\n", script, StringComparison.Ordinal);
        Assert.Equal(1, Regex.Count(script, Regex.Escape("& python $packer build")));
        foreach (var gone in new[] { "--separately-licensed", "voices-nc", "SeparateVoicePack", "$SkipSeparateVoicePacks" })
        {
            Assert.DoesNotContain(gone, script, StringComparison.Ordinal);
        }

        var readme = File.ReadAllText(Path.Combine(Root, "tools", "release", "README.md"));
        Assert.DoesNotContain("voices-nc", readme, StringComparison.Ordinal);
        Assert.DoesNotContain("--separately-licensed build", readme, StringComparison.Ordinal);

        var layout = script[script.IndexOf("function Test-PublishLayout", StringComparison.Ordinal)..];
        layout = layout[..layout.IndexOf("\nfunction ", 10, StringComparison.Ordinal)];
        Assert.Contains("Assert-NoSeparatelyLicensedVoice (Join-Path $PublishDir 'voices')", layout, StringComparison.Ordinal);
        var preflight = script.IndexOf("\nInitialize-VoicePacks\n", StringComparison.Ordinal);
        Assert.True(preflight > 0, "the preflight no longer calls Initialize-VoicePacks");
        Assert.Contains("Assert-NoSeparatelyLicensedVoice (Join-Path $RepoRoot 'artifacts/voice-pack')", script[preflight..(preflight + 200)], StringComparison.Ordinal);

        var guard = script[script.IndexOf("function Assert-NoSeparatelyLicensedVoice", StringComparison.Ordinal)..];
        guard = guard[..guard.IndexOf("\nfunction ", 10, StringComparison.Ordinal)];
        Assert.Contains("Test-VoicePackHasArticulation $bass 9", guard, StringComparison.Ordinal);
        Assert.Contains("'*.LICENSE.txt'", guard, StringComparison.Ordinal);
    }

    [Fact]
    public void The_release_builds_missing_packs_and_refuses_a_payload_without_them()
    {
        var script = File.ReadAllText(Path.Combine(Root, "tools", "release", "New-Release.ps1"));
        Assert.Contains("build_voice_pack.py", script, StringComparison.Ordinal);
        var init = script.IndexOf("\nInitialize-VoicePacks", StringComparison.Ordinal);
        var publish = script.LastIndexOf(ReleasePackChecksLintTests.PublishCall, StringComparison.Ordinal);
        Assert.True(init > 0 && publish > init, "Initialize-VoicePacks must run before the UI is published");
        // A pack built from an OLDER manifest or builder is rebuilt, not shipped:
        // #109 added the recorded slaps to an existing bass.dcvp.
        var initFn = script[script.IndexOf("function Initialize-VoicePacks", StringComparison.Ordinal)..];
        initFn = initFn[..initFn.IndexOf("\nfunction ", 10, StringComparison.Ordinal)];
        Assert.Contains("$stampPath = Join-Path $packDir 'voice-pack.stamp'", initFn, StringComparison.Ordinal);
        Assert.Contains("(Get-Content $stampPath -Raw) -ceq (Get-VoicePackSourceStamp)", initFn, StringComparison.Ordinal);
        Assert.Contains("if ($have -and $fresh) {", initFn, StringComparison.Ordinal);
        var stampFn = script[script.IndexOf("function Get-VoicePackSourceStamp", StringComparison.Ordinal)..];
        stampFn = stampFn[..stampFn.IndexOf("\nfunction ", 10, StringComparison.Ordinal)];
        foreach (var hashed in new[] { "voice_pack_manifest.json", "build_voice_pack.py" })
        {
            Assert.Contains(hashed, stampFn, StringComparison.Ordinal);
        }
        var builder = Path.Combine(Root, "JuceSharp", "src", "JuceSharp.Native", "tools", "voice", "build_voice_pack.py");
        if (File.Exists(builder))
        {
            var py = File.ReadAllText(builder);
            Assert.Contains("STAMP = \"voice-pack.stamp\"", py, StringComparison.Ordinal);
            Assert.Contains("f\"manifest={sha(MANIFEST)}\\nbuilder={sha(os.path.abspath(__file__))}\\n\"", py, StringComparison.Ordinal);
        }
        var layout = script[script.IndexOf("function Test-PublishLayout", StringComparison.Ordinal)..];
        layout = layout[..layout.IndexOf("\nfunction ", 10, StringComparison.Ordinal)];
        Assert.Contains("$VoicePackFiles", layout, StringComparison.Ordinal);
        var list = script[script.IndexOf("$VoicePackFiles = @(", StringComparison.Ordinal)..];
        list = list[..list.IndexOf(')', StringComparison.Ordinal)];
        foreach (var file in new[] { "guitar.dcvp", "bass.dcvp", ArtsPack + ".dcvp", "VOICE-CREDITS.txt" }.Concat(StratPacks.Select(p => p + ".dcvp")))
        {
            Assert.Contains($"'{file}'", list, StringComparison.Ordinal);
        }
    }

    /// <summary>
    ///     The builder and the native reader agree on the pack FORMAT (#408): the
    ///     version and codec the builder writes are the ones dcvoice_pack.h
    ///     decodes. A bump on one side only would ship packs every DLL rejects
    ///     (the voice then falls back without a sound of complaint), or a reader
    ///     that misreads them.
    /// </summary>
    [Fact]
    public void The_builder_writes_the_pack_version_and_codec_the_native_reader_decodes()
    {
        var voice = Path.Combine(Root, "JuceSharp", "src", "JuceSharp.Native");
        var builder = Path.Combine(voice, "tools", "voice", "build_voice_pack.py");
        var reader = Path.Combine(voice, "src", "dsp", "voice", "dcvoice_pack.h");
        Assert.SkipUnless(File.Exists(builder) && File.Exists(reader), "JuceSharp is not checked out in this worktree.");
        var written = Regex.Match(File.ReadAllText(builder), @"^PACK_VERSION, PACK_CODEC = (\d+), (\d+)$", RegexOptions.Multiline);
        Assert.True(written.Success, "build_voice_pack.py no longer declares PACK_VERSION, PACK_CODEC");
        var header = File.ReadAllText(reader);
        var version = Regex.Match(header, @"kPackVersionCoded = (\d+);");
        var codec = Regex.Match(header, @"kPackCodecLossless = (\d+);");
        Assert.True(version.Success && codec.Success, "dcvoice_pack.h no longer declares kPackVersionCoded / kPackCodecLossless");
        Assert.Equal(version.Groups[1].Value, written.Groups[1].Value);
        Assert.Equal(codec.Groups[1].Value, written.Groups[2].Value);
        // The reader decodes coded zones; the builder codes every zone it writes.
        Assert.Contains("codec::decodeZone(", header, StringComparison.Ordinal);
        Assert.Contains("blob = encode_zone(q)", File.ReadAllText(builder), StringComparison.Ordinal);
    }

    [Fact]
    public void Packs_are_never_committed()
    {
        var ignore = File.ReadAllLines(Path.Combine(Root, ".gitignore")).Select(l => l.Trim());
        Assert.Contains("artifacts/", ignore);
    }
}
