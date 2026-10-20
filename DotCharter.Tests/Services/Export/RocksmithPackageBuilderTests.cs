using System.Text.Json;
using DotCharter.Services.Export;
using DotCharter.Services.Export.Package;
using DotCharter.Services.Export.Sng;

namespace DotCharter.Tests.Services.Export;

public sealed partial class RocksmithPackageBuilderTests
{
    [Fact]
    public async Task Typed_non_ascii_dlc_key_is_refused_not_rewritten()
    {
        var error = await Assert.ThrowsAsync<ArgumentException>(() => new RocksmithPackageBuilder(new FakeAppLogger())
            .BuildPsarcAsync(CreateProject(), new RocksmithBuildOptions
            {
                OutputPath = Path.Combine(_tempDir, "typed_p.psarc"),
                AudioPath = CreateFakeWem(),
                SongLengthMs = 32_000,
                DlcKey = "DCAveMujicaザホールA"
            }, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("non-ASCII", error.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_tempDir, "typed_p.psarc")));
    }

    /// <summary>
    ///     Cancelling mid-build releases every handle the build had opened.
    ///     <para>
    ///         Two archive entries are open <see cref="FileStream" />s over the
    ///         source <c>.wem</c> files, and the <c>try/finally</c> that disposes
    ///         the entry list used to start AFTER them — it wrapped only the
    ///         archive write. The cancellation check between the sound-bank stage
    ///         and the write therefore threw straight past both handles, leaving
    ///         the source audio locked until a finalizer ran. Cancel is the one
    ///         thing the dialog's Cancel button exists to do, and the wem it
    ///         locks is the managed encoder's own cache slot, so the next export
    ///         could not rewrite it.
    ///     </para>
    /// </summary>
    [Fact]
    public async Task Cancelling_mid_build_leaves_no_file_locked_and_no_partial_package()
    {
        var project = CreateProject();
        var audioPath = CreateFakeWem();
        var psarcPath = Path.Combine(_tempDir, "cancelled_p.psarc");
        var builder = new RocksmithPackageBuilder(new FakeAppLogger());
        using var cts = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            builder.BuildPsarcAsync(project, new RocksmithBuildOptions
            {
                OutputPath = psarcPath,
                AudioPath = audioPath,
                SongLengthMs = 32_000,
                IncludeVocals = false
            },
                // The LAST stage is reported after the sound banks are built, so
                // both wem handles are open and the cancellation check on the
                // very next line is the one that throws.
                new SyncProgress(p =>
                {
                    if (p.Stage == RocksmithBuildStage.WritingArchive) cts.Cancel();
                }),
                cts.Token));

        // No handle survives: an exclusive open is the direct assertion.
        using (new FileStream(audioPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            // Opening is the assertion; an outstanding reader throws IOException.
        }

        // …and the staging file never became a package (PsarcWriter is atomic).
        Assert.False(File.Exists(psarcPath));
        Assert.False(File.Exists(psarcPath + ".writing"));
    }

    /// <summary>#195: the warning for a declared tone with no definition names the rename that
    /// repairs it when the project leaves no doubt (one defined tone nothing plays), and only then.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_undefined_declared_tone_names_its_repair_only_when_there_is_exactly_one(bool oneCandidate)
    {
        var project = CreateProject();   // Lead declares and plays "clean", which nothing defines
        const string body = "{\"Key\":\"x\"}";
        project.ToneDefinitions.Add(new ToneDefinition { Key = "lead_dist", Name = "lead_dist", Json = body });
        if (!oneCandidate) project.ToneDefinitions.Add(new ToneDefinition { Key = "spare", Name = "spare", Json = body });

        var result = await new RocksmithPackageBuilder(new FakeAppLogger()).BuildPsarcAsync(project, new RocksmithBuildOptions
        {
            OutputPath = Path.Combine(_tempDir, $"tone_repair_hint_{oneCandidate}_p.psarc"),
            AudioPath = CreateFakeWem(),
            SongLengthMs = 32_000,
            IncludeVocals = false
        }, cancellationToken: TestContext.Current.CancellationToken);

        var warning = Assert.Single(result.Warnings, w => w.Contains("declares tone 'clean'", StringComparison.Ordinal));
        Assert.Equal(oneCandidate, warning.Contains("The project defines 'lead_dist'", StringComparison.Ordinal));
    }

    /// <summary>
    ///     …and a slot the project genuinely cannot answer is REPORTED. The miss
    ///     used to be a silent `Where(json =&gt; json != null)`, so the one fault
    ///     the export check is named after left no trace in the build result.
    /// </summary>
    [Fact]
    public async Task A_slot_with_no_definition_anywhere_is_reported_as_a_warning()
    {
        var project = CreateProject();

        var psarcPath = Path.Combine(_tempDir, "tonemissing_p.psarc");
        var builder = new RocksmithPackageBuilder(new FakeAppLogger());
        var result = await builder.BuildPsarcAsync(project, new RocksmithBuildOptions
        {
            OutputPath = psarcPath,
            AudioPath = CreateFakeWem(),
            SongLengthMs = 32_000,
            IncludeVocals = false,
            ToneMap = new Dictionary<string, (string, string[])>
            {
                ["Lead"] = ("", ["ghost_tone"])
            }
        }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains(result.Warnings,
            w => w.Contains("ghost_tone", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Rejects_missing_audio_and_unsupported_formats()
    {
        var project = CreateProject();
        var builder = new RocksmithPackageBuilder(new FakeAppLogger());

        await Assert.ThrowsAsync<FileNotFoundException>(() => builder.BuildPsarcAsync(project,
            new RocksmithBuildOptions
            {
                OutputPath = Path.Combine(_tempDir, "x.psarc"),
                AudioPath = Path.Combine(_tempDir, "missing.wem")
            }, cancellationToken: TestContext.Current.CancellationToken));

        var mp3 = Path.Combine(_tempDir, "audio.mp3");
        await File.WriteAllBytesAsync(mp3, new byte[100], TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<NotSupportedException>(() => builder.BuildPsarcAsync(project,
            new RocksmithBuildOptions
            {
                OutputPath = Path.Combine(_tempDir, "x.psarc"),
                AudioPath = mp3
            }, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Manifest_json_contains_the_fields_the_game_and_importer_need()
    {
        var project = CreateProject();
        var arrangement = project.Arrangements[0];
        var build = SngFromDomainMapper.BuildInstrumental(arrangement, project.Beats, 32_000);

        var meta = new RsPackageMetadata
        {
            DlcKey = "DCGoldenTest",
            Title = "Golden Test Song",
            Artist = "The DotCharter",
            Album = "Test Album",
            Year = 2026,
            AverageTempo = 120f
        };

        var packageArr = new RsPackageArrangement
        {
            ArrangementName = "Lead",
            PartitionName = "lead",
            SongPartition = 1,
            PersistentId = Guid.Parse("11111111-2222-3333-4444-555555555555"),
            MasterId = 12345,
            Sng = build.Sng,
            NoteCounts = build.NoteCounts,
            Domain = arrangement,
            ToneBase = "clean",
            ToneSlots = ["clean"]
        };

        var json = RsManifestGenerator.CreateArrangementManifest(meta, packageArr);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("RSEnumerable_Song", root.GetProperty("ModelName").GetString());
        Assert.Equal(2, root.GetProperty("IterationVersion").GetInt32());
        Assert.Equal("Static.Songs.Entries", root.GetProperty("InsertRoot").GetString());

        var attributes = root.GetProperty("Entries")
            .GetProperty("11111111222233334444555555555555")
            .GetProperty("Attributes");

        Assert.Equal("Lead", attributes.GetProperty("ArrangementName").GetString());
        Assert.Equal("DCGoldenTest", attributes.GetProperty("SongKey").GetString());
        Assert.Equal("DCGoldenTest", attributes.GetProperty("DLCKey").GetString());
        Assert.Equal("Golden Test Song", attributes.GetProperty("SongName").GetString());
        Assert.Equal("The DotCharter", attributes.GetProperty("ArtistName").GetString());
        Assert.Equal("DotCharter", attributes.GetProperty("ArtistNameSort").GetString());
        Assert.Equal("song_dcgoldentest.bnk", attributes.GetProperty("SongBank").GetString());
        Assert.Equal("song_dcgoldentest_preview.bnk", attributes.GetProperty("PreviewBankPath").GetString());
        Assert.Equal(2026, attributes.GetProperty("SongYear").GetInt32());
        Assert.Equal("clean", attributes.GetProperty("Tone_Base").GetString());
        Assert.Equal("clean", attributes.GetProperty("Tone_A").GetString());
        Assert.Equal(-2, attributes.GetProperty("Tuning").GetProperty("string0").GetInt32());
        Assert.Equal(32f, attributes.GetProperty("SongLength").GetSingle(), 0.01f);
        Assert.Equal(120f, attributes.GetProperty("SongAverageTempo").GetSingle());
        Assert.Equal(4, attributes.GetProperty("NotesHard").GetSingle()); // 3 notes + 1 chord
        Assert.Equal(1, attributes.GetProperty("SongPartition").GetInt32());
        Assert.Equal(100_000, attributes.GetProperty("TargetScore").GetInt32());
        Assert.Equal(20, attributes.GetProperty("DynamicVisualDensity").GetArrayLength());
        Assert.Equal(3, attributes.GetProperty("PhraseIterations").GetArrayLength());
        Assert.Equal(3, attributes.GetProperty("Phrases").GetArrayLength());
        Assert.Equal(2, attributes.GetProperty("Sections").GetArrayLength());
        Assert.Equal("$[34304] Verse [1]",
            attributes.GetProperty("Sections")[0].GetProperty("UIName").GetString());
        // The fixture's only template is a TWO-STRING "E5", and since 0.41 a
        // double stop ships unnamed — RsManifestGenerator has always excluded
        // nameless templates, so it correctly drops out of this list. That a
        // NAMED template still reaches the manifest is pinned separately by
        // AThreeStringNamedTemplateStillReachesTheManifest.
        Assert.Equal(0, attributes.GetProperty("ChordTemplates").GetArrayLength());
        Assert.True(attributes.GetProperty("ArrangementProperties").GetProperty("pathLead").GetInt32() == 1);
        // No capo: the attribute is omitted entirely.
        Assert.False(attributes.TryGetProperty("CapoFret", out _));

        // A named template must still make it through — otherwise the assertion
        // above would pass just as well if the list were broken outright.
        AThreeStringNamedTemplateStillReachesTheManifest(meta);

        // Header variant.
        var headerJson = RsManifestGenerator.CreateHeader(meta, [packageArr]);
        using var headerDoc = JsonDocument.Parse(headerJson);
        var headerRoot = headerDoc.RootElement;
        Assert.Equal("Static.Songs.Headers", headerRoot.GetProperty("InsertRoot").GetString());
        Assert.False(headerRoot.TryGetProperty("ModelName", out _));
        var headerAttributes = headerRoot.GetProperty("Entries")
            .GetProperty("11111111222233334444555555555555")
            .GetProperty("Attributes");
        Assert.Equal(1, headerAttributes.GetProperty("Representative").GetInt32());
        Assert.Equal(1, headerAttributes.GetProperty("RouteMask").GetInt32());
        Assert.False(headerAttributes.TryGetProperty("SongAsset", out _));
    }
}
