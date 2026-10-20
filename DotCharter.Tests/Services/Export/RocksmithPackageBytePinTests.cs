using System.Buffers.Binary;
using System.Security.Cryptography;
using DotCharter.Services.Export;
using DotCharter.Services.Export.Audio;
using DotCharter.Tests.Services.Export.Rs7;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Export;

/// <summary>
///     #885: every package the read-back tests build, pinned by the SHA-256 of the whole
///     file, without the package reader (which leaves the public tree). Each scenario
///     mirrors a reader-side test (named in <see cref="Scenarios" />) with everything a build
///     draws at random pinned (<see cref="RocksmithBuildIdentity" />), the synthetic keys and
///     a deterministic wem, and is built twice: the pin is only worth something when the
///     build is a pure function of its input. A failure means the package changed —
///     investigate, never re-pin. The semantic checks (manifest fields, entries, the SNG
///     read back) stay in the reader-side twins.
/// </summary>
public sealed class RocksmithPackageBytePinTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("dotcharter-package-pin-").FullName;

    public void Dispose() => BestEffortDelete.Directory(_tempDir);

    /// <summary>Scenario → pinned SHA-256, and the reader-side test(s) it stands in for.</summary>
    private static readonly Dictionary<string, string> Pins = new(StringComparer.Ordinal)
    {
        // RocksmithPackageBuilderTests.Built_psarc_reimports_with_equal_chart_data, .Package_contains_the_full_flat_file_layout
        ["golden"] = "201fd7660b15fe00cab173e401d18e75042465129b0fff2b41eb90f7273f0ffe",
        // RocksmithPackageBuilderTests: the same chart, plain (#736)
        ["golden_plain"] = "1cc4b6b11bf4f0f6e58ae468bb29bb5181b9a255c0a13b1b4d269a037d7b4d65",
        // .Japanese_title_builds_a_package_with_only_ascii_names
        // The song's version reaches toolkit.version (the VersionBytePinTests half below)
        ["version"] = "438055344c0778676a09a0c23348e7d5b46a5ec0d97012ea50022769019c53ec",
        ["japanese"] = "ab43967aa11ccebbf5d3905b52c1edbb27a669d7207ebde88eec5d50ee2f7d45",
        // .Arrangement_filter_limits_the_exported_arrangements
        ["filter_rhythm"] = "b5b2d585cbbce49fae9c7a904405b53cef14672df7a41552501927e0be5b21b8",
        // .Tone_map_and_tone_definitions_flow_into_the_manifest
        ["tone_map"] = "ccf88ebd16feeec48a96b7a114815cb0333f9511ae04c35bfe35e2e943927535",
        // .Tone_change_events_index_the_slot_table_the_manifest_declares
        ["tone_order"] = "7ce0d511c5f0efe512c79a442c0e5ca2e357fd1f4a71288aa05162a0e3db6dc7",
        // .Arrangement_without_any_tone_gets_a_default_base_tone_name
        ["no_tone"] = "40ed6b2ce64e6e3c1235bc298a12cb6ef594b871922dbee9a9c941fb56c891b2",
        // .Charter_flows_into_toolkit_version_with_options_fallback (both halves)
        ["charter"] = "d34f8e5ae4e2d676e7b88c364e036b640893190f2b732be220e0441948f8529e",
        ["charter_fallback"] = "3d3a174689bb02f2dd4651b7c32becf17a204106cfcc3baebfbc974bc91f2541",
        // .Project_authored_tone_definitions_win_over_imported_passthrough
        ["authored_tones"] = "466acd23c0e4d0963e10649dfe44e72e8487c0c298725034b68c381bb77a8e00",
        // .A_slot_naming_a_tone_by_its_NAME_still_exports_the_body
        ["tone_by_name"] = "64d6843a1a219cc16eb8eb3a18ce0890ff1cf5196ea4a36db317b24f056c03aa",
        // .Packages_without_tone_definitions_export_an_empty_tones_array, .The_xblock_names_exactly_the_three_album_art_sizes
        ["no_vocals"] = "a53b56fa112bf752b06f3e890b78837078592e46c8c418bfad71c0d22164df0d",
        // Rs7PackageExportTests.Plain_package_carries_every_byte_the_mod_reads, .Export_then_import_is_lossless(true)
        ["rs7_plain"] = "86e0dc720f7d3673061b8ec2cba0260863d4eae55ab4bcbedfad70d362e77dfa",
        // Rs7PackageExportTests.A_seven_string_package_is_encrypted_by_default_and_the_block_survives_the_decryption,
        // .Export_then_import_is_lossless(null/false), SaveCompanionServiceTests.Psarc_is_built_encrypted_for_a_seven_string_guitar
        ["rs7_encrypted"] = "92fa57839e2ffd19f861b32c9e049ab758167b97a3ed7e620ec58c0a6597ecd3",
        // RocksmithPlatformPackageTests.Mac_package_differs_from_the_pc_one_only_where_the_platform_does, .Mac_package_round_trips_through_the_import
        ["mac"] = "d647027b08396bd8bdfc614b3d221ba6a996a7540c237be4dd811c8249b5931a",
        // WwiseFreeExportTests.Builds_a_psarc_from_a_wav_with_no_wwise(Pcm), .A_cached_wem_whose_codec_disagrees_is_re_encoded_instead_of_mis_declared
        ["wav_pcm"] = "9fc72bdc40c6de15c3113c0417ed42c6c949ac4dee5d0a7631d9eeee0f097542",
        // WwiseFreeExportTests.Builds_a_psarc_from_a_wav_with_no_wwise(Vorbis), .Without_wwise_every_request_but_pcm_is_written_as_vorbis
        ["wav_vorbis"] = "17c3eab19306c620c4d0b1ec2647004cef60c9ff92d5935b8b216df944dec1b5",
        // RocksmithPackageBuilderTests.Auto_preview_is_a_short_clip_not_the_full_song
        ["wav_preview"] = "e57aae6f93e8f29f885d57bae4820e8750311f43fc3ff1b82406fcf683d38ff1"
    };

    public static TheoryData<string> Scenarios => [.. Pins.Keys];

    [Theory]
    [MemberData(nameof(Scenarios))]
    public async Task The_package_is_a_pinned_pure_function_of_its_input(string scenario)
    {
        using var keys = TestRocksmithKeys.UseSynthetic();
        var first = await BuildAsync(scenario, "a");
        var second = await BuildAsync(scenario, "b");

        Assert.Equal("PSAR"u8.ToArray(), first[..4]);
        Assert.True(first.AsSpan().SequenceEqual(second), $"{scenario}: two builds of the same input differ");
        var actual = Convert.ToHexStringLower(SHA256.HashData(first));
        Assert.True(Pins[scenario] == actual, $"{scenario}: package SHA-256 {actual} != pinned {Pins[scenario]}");
    }

    /// <summary>
    ///     The song's version is the only difference between the "golden" build and the "version" one,
    ///     and "golden" (version 1) is byte-identical to what builds wrote before the option existed.
    /// </summary>
    [Fact]
    public async Task The_version_changes_the_package_and_the_default_does_not()
    {
        using var keys = TestRocksmithKeys.UseSynthetic();
        var golden = await BuildAsync("golden", "v1");
        var versioned = await BuildAsync("version", "v1");
        Assert.False(golden.AsSpan().SequenceEqual(versioned));
    }

    /// <summary>The TOC flag is in the header (big-endian flags word, bit 4): plain where
    /// asked, encrypted by default — readable without the reader.</summary>
    [Theory]
    [InlineData("golden", true)]
    [InlineData("golden_plain", false)]
    [InlineData("rs7_plain", false)]
    [InlineData("rs7_encrypted", true)]
    [InlineData("mac", true)]
    public async Task The_toc_is_encrypted_exactly_when_the_build_asks(string scenario, bool encrypted)
    {
        using var keys = TestRocksmithKeys.UseSynthetic();
        var bytes = await BuildAsync(scenario, "flag");
        Assert.Equal(encrypted, (BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(28)) & 4u) != 0);
    }

    private async Task<byte[]> BuildAsync(string scenario, string run)
    {
        var directory = Directory.CreateDirectory(Path.Combine(_tempDir, $"{scenario}_{run}")).FullName;
        var (project, options) = Scenario(scenario, Path.Combine(directory, "pin_p.psarc"), directory, DeterministicWem());
        var result = await new RocksmithPackageBuilder(new FakeAppLogger())
            .BuildPsarcAsync(project, options, cancellationToken: TestContext.Current.CancellationToken);
        var path = scenario == "mac" ? result.PackagePaths.Single(p => p.EndsWith("_m.psarc", StringComparison.Ordinal)) : result.PsarcPath;
        return await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
    }

    /// <summary>What a scenario changes in the build options; everything else is fixed.</summary>
    private sealed record Spec(int Arrangements)
    {
        public bool Vocals { get; init; }
        public bool? Plain { get; init; }
        public string DlcKey { get; init; } = "PinnedGolden";
        public string Version { get; init; } = "1";
        public double SongLengthMs { get; init; } = 32_000;
        public double? PreviewStartMs { get; init; }
        public ExportPlatform Platform { get; init; } = ExportPlatform.Pc;
        public IReadOnlyCollection<string>? Filter { get; init; }
        public Dictionary<string, (string ToneBase, string[] Slots)> ToneMap { get; init; } = [];
        public IReadOnlyDictionary<string, string> ToneDefinitions { get; init; } = new Dictionary<string, string>();
        public string FallbackCharter { get; init; } = "";
        public int WavSeconds { get; init; }
        public WemCodec Codec { get; init; } = WemCodec.Vorbis;
    }

    private static (ChartProject Project, RocksmithBuildOptions Options) Scenario(string name, string output, string directory,
        string wem)
    {
        var project = name.StartsWith("rs7_", StringComparison.Ordinal)
            ? ChartProjectCloner.Clone(Rs7PackageExportTests.CreateProject(7))
            : RocksmithPackageBuilderTests.CreateProject();
        var spec = Mutate(name, project);
        return (project, new RocksmithBuildOptions
        {
            OutputPath = output,
            // A wav is encoded per run, in the run's own folder: the encoder's wem cache
            // sits beside the source, so the second build re-encodes rather than reusing.
            AudioPath = spec.WavSeconds > 0 ? WriteWav(directory, spec.WavSeconds) : wem,
            AudioCodec = spec.Codec,
            DlcKey = spec.DlcKey,
            Version = spec.Version,
            SongLengthMs = spec.SongLengthMs,
            PreviewStartMs = spec.PreviewStartMs,
            IncludeVocals = spec.Vocals,
            PlainPackage = spec.Plain,
            Platform = spec.Platform,
            ArrangementFilter = spec.Filter,
            ToneMap = spec.ToneMap,
            ToneDefinitions = spec.ToneDefinitions,
            FallbackCharter = spec.FallbackCharter,
            Identity = Identity(spec.Arrangements)
        });
    }

    private static Spec Mutate(string name, ChartProject project)
    {
        var lead = project.Arrangements[0];
        var rs7 = new Spec(1) { DlcKey = "rs7pin", SongLengthMs = 64 * Rs7TestSupport.BeatMs, PreviewStartMs = 0 };
        switch (name)
        {
            case "golden":
                return new Spec(2) { Vocals = true };
            case "version":
                return new Spec(2) { Vocals = true, Version = "2.5" };
            case "golden_plain":
                return new Spec(2) { Vocals = true, Plain = true };
            case "japanese":
                project.Chart.SongName = "ザホールブルーワールド";
                project.Chart.Artist = "Ave Mujica";
                return new Spec(2) { Vocals = true, DlcKey = "" };
            case "filter_rhythm":
                project.Arrangements.Add(Rhythm(project.Beats));
                return new Spec(1) { Filter = ["Rhythm"] };
            case "tone_map":
                return new Spec(1)
                {
                    ToneMap = new() { ["Lead"] = ("dc_clean", ["dc_clean", "dc_lead"]) },
                    ToneDefinitions = new Dictionary<string, string>
                    {
                        ["dc_clean"] = CleanTone,
                        ["dc_lead"] = LeadTone,
                        ["unused_tone"] = LeadTone
                    }
                };
            case "tone_order":
                lead.ToneBase = "clean";
                lead.ToneA = "clean";
                lead.ToneB = "dist";
                lead.ToneChanges.Clear();
                lead.ToneChanges.Add(new ToneChange { Position = Pos(project.Beats, 4000), ToneName = "dist" });
                return new Spec(1) { ToneMap = new() { ["Lead"] = ("dist", ["dist", "clean"]) } };
            case "no_tone":
                ClearTones(lead);
                return new Spec(1);
            case "charter":
                project.Chart.Charter = "ChartCharter";
                return new Spec(1) { FallbackCharter = "FallbackGuy" };
            case "charter_fallback":
                return new Spec(1) { FallbackCharter = "FallbackGuy" };
            case "authored_tones":
                project.ToneDefinitions.Add(new ToneDefinition { Key = "dc_clean", Name = "Authored Clean", Json = AuthoredClean });
                project.ToneDefinitions.Add(new ToneDefinition { Key = "dc_lead", Name = "Authored Lead", Json = AuthoredLead });
                return new Spec(1)
                {
                    ToneMap = new() { ["Lead"] = ("dc_clean", ["dc_clean", "dc_lead"]) },
                    ToneDefinitions = new Dictionary<string, string> { ["dc_clean"] = ImportedClean }
                };
            case "tone_by_name":
                project.ToneDefinitions.Add(new ToneDefinition { Key = "Clean_Verse", Name = "Clean Verse", Json = Generated });
                return new Spec(1) { ToneMap = new() { ["Lead"] = ("Clean Verse", [" clean verse "]) } };
            case "no_vocals":
                return new Spec(1);
            case "rs7_plain":
                return rs7 with { Plain = true };
            case "rs7_encrypted":
                return rs7;
            case "mac":
                return new Spec(2) { Vocals = true, Platform = ExportPlatform.PcAndMac };
            case "wav_pcm":
                return new Spec(1) { WavSeconds = 2, Codec = WemCodec.Pcm };
            case "wav_vorbis":
                return new Spec(1) { WavSeconds = 2 };
            case "wav_preview":
                return new Spec(1) { WavSeconds = 40 };
            default:
                throw new ArgumentOutOfRangeException(nameof(name), name, null);
        }
    }

    /// <summary>Every id the build would draw, for <paramref name="arrangements" /> arrangements
    /// (graph items: xblock, hsan, showlights, three album-art sizes, two banks, and two per
    /// arrangement).</summary>
    private static RocksmithBuildIdentity Identity(int arrangements) => new()
    {
        ConversionTime = new DateTime(2026, 10, 14, 9, 30, 0, DateTimeKind.Unspecified),
        Arrangements = [.. Enumerable.Range(0, arrangements).Select(i => new RsArrangementIdentity(
            new Guid(0x5EED0000 + i, 0x11, 0x22, [9, 8, 7, 6, 5, 4, 3, (byte)i]), 2_000_000 + i))],
        GraphItems = [.. Enumerable.Range(1, 8 + (2 * arrangements)).Select(i => new RsGraphItemIdentity(
            new Guid(i, 0x33, 0x44, [1, 2, 3, 4, 5, 6, 7, (byte)i]), new Guid(2000 + i, 0, 0, new byte[8])))],
        MainBank = new SoundBankIds(0x1111_2222, 0x0333_4444, 0x0555_6666, 0x7777_8888, 0x0999_AAAA),
        PreviewBank = new SoundBankIds(0x2111_2222, 0x0433_4444, 0x0655_6666, 0x8777_8888, 0x0999_AAAA),
        SngIv = [.. Enumerable.Range(0, 16).Select(i => (byte)(0xC0 + i))]
    };

    private static void ClearTones(Arrangement arrangement)
    {
        arrangement.ToneBase = "";
        arrangement.ToneA = "";
        arrangement.ToneChanges.Clear();
    }

    private static FractionalPosition Pos(IList<BeatMarker> beats, double ms) => TimelineMath.PositionFromMs(ms, beats);

    private static Arrangement Rhythm(IList<BeatMarker> beats)
    {
        var rhythm = new Arrangement { ArrangementType = ArrangementType.Rhythm, Name = "Rhythm", StringCount = 6 };
        for (var i = 0; i < 6; i++) rhythm.TuningPitches.Add(new TuningOffset(0));
        rhythm.Phrases.AddRange(["COUNT", "verse", "END"]);
        rhythm.PhraseIterations.Add(new PhraseIteration(Pos(beats, 0), 0));
        rhythm.PhraseIterations.Add(new PhraseIteration(Pos(beats, 2000), 1));
        rhythm.PhraseIterations.Add(new PhraseIteration(Pos(beats, 30000), 2));
        var level = new ChartLevel { DifficultyIndex = 0 };
        level.Anchors.Add(new FretAnchor(Pos(beats, 2000), 1, 4));
        level.Notes.Add(new GuitarNote { Position = Pos(beats, 2000), StringIndex = 0, Fret = 3 });
        rhythm.Levels.Add(level);
        return rhythm;
    }

    private static string WriteWav(string directory, int seconds)
    {
        var path = Path.Combine(directory, $"audio_{seconds}s.wav");
        var samples = new short[48_000 * seconds * 2];
        for (var i = 0; i < samples.Length; i++)
            samples[i] = (short)(Math.Sin(2 * Math.PI * 220 * (i / 2) / 48_000.0) * 8000);
        PcmAudioLoader.WriteWav(path, new PcmAudio(samples, 2, 48_000));
        return path;
    }

    private string DeterministicWem()
    {
        var path = Path.Combine(_tempDir, "audio.wem");
        if (File.Exists(path)) return path;
        var data = new byte[80_000];
        new Random(42).NextBytes(data);
        File.WriteAllBytes(path, data);
        return path;
    }

    private const string CleanTone =
        """
        {"GearList":{"Amp":{"Type":"Amps","KnobValues":{"Volume":-12.0},"Key":"Amp_Default"}},
         "IsCustom":true,"Volume":"-12.000","Key":"dc_clean","Name":"DC Clean","SortOrder":0.0}
        """;

    private const string LeadTone =
        """
        {"GearList":{},"IsCustom":true,"Volume":"-11.000","Key":"dc_lead","Name":"DC Lead","SortOrder":1.0}
        """;

    private const string ImportedClean =
        """{"GearList":{},"IsCustom":true,"Volume":"-12.000","Key":"dc_clean","Name":"Imported Clean"}""";

    private const string AuthoredClean =
        """{"GearList":{},"IsCustom":true,"Volume":"-9.000","Key":"dc_clean","Name":"Authored Clean"}""";

    private const string AuthoredLead =
        """{"GearList":{},"IsCustom":true,"Volume":"-11.000","Key":"dc_lead","Name":"Authored Lead"}""";

    private const string Generated =
        """{"GearList":{},"IsCustom":true,"Volume":"-10.000","Key":"Clean_Verse","Name":"Clean Verse"}""";
}
