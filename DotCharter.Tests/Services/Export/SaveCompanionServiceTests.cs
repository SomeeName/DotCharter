using DotCharter.Services.Export;
using DotCharter.Services.Export.Audio;
using DotCharter.Services.Parsing.Eof;
using DotCharter.Tests.Services.Parsing.Eof;

namespace DotCharter.Tests.Services.Export;

/// <summary>The tests of <see cref="SaveCompanionServiceTests" /> that need no package reader; its fixtures are in <c>SaveCompanionServiceTests.Fixtures.cs</c>.</summary>
public sealed partial class SaveCompanionServiceTests
{
    [Theory]
    [InlineData(@"\\server\share\song.wav")]
    [InlineData("//server/share/song.wav")]
    [InlineData(@"\\?\C:\song.wav")]
    [InlineData(@"\\.\C:\song.wav")]
    [InlineData(@"C:song.wav")]
    [InlineData(@"\song.wav")]
    [InlineData("NUL.wav")]
    public void Refused_audio_references_never_reach_a_file_probe(string reference)
    {
        var probed = new List<string>();
        var resolved = SaveCompanionService.ResolveAudio(_dir, reference, path =>
        {
            probed.Add(path);
            return true;
        });

        Assert.Empty(probed);
        Assert.Null(resolved);
    }

    [Fact]
    public void Companion_audio_uses_the_loaders_parent_search_for_the_declared_source()
    {
        var chartDirectory = Directory.CreateDirectory(Path.Combine(_dir, "lead")).FullName;
        var audio = Path.Combine(_dir, "song.flac");
        File.WriteAllText(audio, "audio");

        Assert.Equal(audio, SaveCompanionService.ResolveAudio(chartDirectory, "song.flac"));
        Assert.Equal(audio, SaveCompanionService.ResolveAudio(chartDirectory, audio));
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(-1, true)]
    [InlineData(-1, false)]
    [InlineData(null, true)]
    [InlineData(1, false)]
    [InlineData(null, false)]
    public void A_failed_eof_encode_never_publishes_partial_audio_and_the_next_save_repairs_it(int? exitCode, bool previous)
    {
        var source = Path.Combine(_dir, "song.wav");
        var target = Path.Combine(_dir, "export", "song.ogg");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllText(source, "source");
        if (previous)
        {
            CompanionOwnership.WriteIfUnmodified(target, p => File.WriteAllText(p, "previous good audio"), out _);
            File.SetLastWriteTimeUtc(target, File.GetLastWriteTimeUtc(source).AddDays(-1));
        }

        var ffmpeg = Path.Combine(_dir, "ffmpeg.exe");
        File.WriteAllText(ffmpeg, "fake executable; the runner is injected");
        var attempts = 0;
        var logger = new FakeAppLogger();
        var service = new SaveCompanionService(new FakeConfigurationService
        {
            AppConfig = new AppConfig { SaveCompanionEof = true, SaveCompanionEofAudio = true, FfmpegPath = ffmpeg }
        }, logger, (_, args, _) =>
        {
            attempts++;
            File.WriteAllText(args[^1], attempts == 1 ? "partial" : "complete audio");
            if (attempts == 1 && exitCode == -1) throw new IOException("encode interrupted");
            return new MediaProcess.MediaProcessResult(attempts == 1 ? exitCode : 0, "encode failed");
        });
        var projectPath = Path.Combine(_dir, "MySong.dcproj");

        Assert.NotNull(service.WriteEof(projectPath, CreateProject()));
        Assert.Equal(1, attempts);
        if (previous) Assert.Equal("previous good audio", File.ReadAllText(target));
        else Assert.False(File.Exists(target));
        Assert.Empty(Directory.GetFiles(_dir, "*.writing.ogg"));
        Assert.True(logger.HasEntryContaining("WARN", exitCode == null ? "timed out" : exitCode == -1 ? "could not be prepared" : "failed"));

        Assert.NotNull(service.WriteEof(projectPath, CreateProject()));
        Assert.Equal(3, attempts);
        Assert.Equal("complete audio", File.ReadAllText(target));
        Assert.Empty(Directory.GetFiles(_dir, "*.writing.ogg"));
        service.WriteEof(projectPath, CreateProject());
        Assert.Equal(3, attempts); // successful audio is fresh on the third save
    }

    [Fact]
    public void Writing_the_eof_puts_it_next_to_the_project_and_it_reimports()
    {
        var service = Create(new AppConfig { SaveCompanionEof = true, SaveCompanionEofAudio = false });
        var projectPath = Path.Combine(_dir, "MySong.rscp");

        // A chart with real notes — EOF only reports tracks that have content.
        var eofPath = service.WriteEof(projectPath, EofExportTestCharts.BuildRepresentativeProject());

        Assert.Equal(Path.Combine(_dir, "export", "MySong.eof"), eofPath);
        Assert.True(File.Exists(eofPath));

        // EOF must actually be able to read what we wrote — a file only we can parse
        // would defeat the entire point of the companion.
        var tracks = new EofImporter().GetAvailableTracks(eofPath!);
        Assert.NotEmpty(tracks);
    }

    [Fact]
    public void The_eof_is_not_written_when_the_companion_is_disabled()
    {
        var service = Create(new AppConfig { SaveCompanionEof = false });

        Assert.Null(service.WriteEof(Path.Combine(_dir, "MySong.rscp"), CreateProject()));
        Assert.Empty(Directory.GetFiles(_dir, "*.eof"));
    }

    [Fact]
    public async Task Psarc_companion_lands_next_to_the_project_named_by_dlc_key()
    {
        WriteWav(Path.Combine(_dir, "song.wav"));

        var service = Create(new AppConfig
        {
            SaveCompanionPsarc = true,
            SaveCompanionPsarcCodec = "Vorbis"
        });

        var path = await service.BuildPsarcAsync(
            Path.Combine(_dir, "MySong.rscp"), CreateProject(), audioLengthMs: null, CancellationToken.None);

        Assert.NotNull(path);
        Assert.True(File.Exists(path));
        Assert.EndsWith("_p.psarc", path);
        Assert.Equal(Path.Combine(_dir, "export"), Path.GetDirectoryName(path));

        // The staging file must not survive a successful build.
        Assert.Empty(Directory.GetFiles(_dir, "*.building"));
    }

    [Fact]
    public async Task A_cancelled_build_leaves_the_previous_psarc_intact()
    {
        WriteWav(Path.Combine(_dir, "song.wav"));
        var service = Create(new AppConfig { SaveCompanionPsarc = true });
        var projectPath = Path.Combine(_dir, "MySong.rscp");

        var good = await service.BuildPsarcAsync(projectPath, CreateProject(), audioLengthMs: null, CancellationToken.None);
        Assert.NotNull(good);
        var goodBytes = await File.ReadAllBytesAsync(good!, TestContext.Current.CancellationToken);

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.BuildPsarcAsync(projectPath, CreateProject(), audioLengthMs: null, cancelled.Token));

        // Staging-then-swap is what makes this true: a build that dies mid-flight
        // must not truncate the psarc a previous save produced.
        Assert.Equal(goodBytes, await File.ReadAllBytesAsync(good!, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetFiles(_dir, "*.building"));
    }

    [Fact]
    public async Task Psarc_is_skipped_when_the_project_has_no_audio()
    {
        var service = Create(new AppConfig { SaveCompanionPsarc = true });

        // No audio file on disk — skip quietly rather than throwing into the save path.
        var path = await service.BuildPsarcAsync(
            Path.Combine(_dir, "MySong.rscp"), CreateProject(), audioLengthMs: null, CancellationToken.None);

        Assert.Null(path);
    }

    [Fact]
    public async Task Psarc_is_not_built_when_the_companion_is_disabled()
    {
        WriteWav(Path.Combine(_dir, "song.wav"));
        var service = Create(new AppConfig { SaveCompanionPsarc = false });

        Assert.Null(await service.BuildPsarcAsync(
            Path.Combine(_dir, "MySong.rscp"), CreateProject(), audioLengthMs: null, CancellationToken.None));
        Assert.Empty(Directory.GetFiles(_dir, "*.psarc"));
    }

    [Fact]
    public async Task Psarc_is_skipped_when_the_chart_has_blocking_validation_errors()
    {
        WriteWav(Path.Combine(_dir, "song.wav"));
        var service = Create(new AppConfig { SaveCompanionPsarc = true, SaveCompanionPsarcCodec = "Vorbis" });

        var project = CreateProject();
        // Fret 30 is beyond the game's 24-fret ceiling — a blocking error the
        // export dialog refuses; the companion must skip, not ship it.
        project.Arrangements[0].Levels[0].Anchors.Add(
            new FretAnchor(TimelineMath.PositionFromMs(1000, project.Beats), 1, 4));
        project.Arrangements[0].Levels[0].Notes.Add(new GuitarNote
        {
            Position = TimelineMath.PositionFromMs(2000, project.Beats),
            StringIndex = 0,
            Fret = 30
        });

        Assert.Null(await service.BuildPsarcAsync(
            Path.Combine(_dir, "MySong.rscp"), project, audioLengthMs: null, CancellationToken.None));
        Assert.Empty(Directory.GetFiles(_dir, "*.psarc"));
    }

    [Theory]
    [InlineData(6)] // a six-string guitar has no string 6
    [InlineData(7)] // a seven-string one has strings 0..6, not 7
    public async Task Psarc_is_skipped_for_a_string_the_arrangement_does_not_have(int stringIndex)
    {
        WriteWav(Path.Combine(_dir, "song.wav"));
        var service = Create(new AppConfig { SaveCompanionPsarc = true, SaveCompanionPsarcCodec = "Vorbis" });

        var project = CreateProject();
        project.Arrangements[0].StringCount = stringIndex;
        project.Arrangements[0].Levels[0].Anchors.Add(
            new FretAnchor(TimelineMath.PositionFromMs(1000, project.Beats), 1, 4));
        project.Arrangements[0].Levels[0].Notes.Add(new GuitarNote
        {
            Position = TimelineMath.PositionFromMs(2000, project.Beats),
            StringIndex = stringIndex, // would fold onto the last string the SNG has
            Fret = 3
        });

        Assert.Null(await service.BuildPsarcAsync(
            Path.Combine(_dir, "MySong.rscp"), project, audioLengthMs: null, CancellationToken.None));
        Assert.Empty(Directory.GetFiles(_dir, "*.psarc"));
    }

    [Fact]
    public async Task Psarc_is_built_plain_for_a_seven_string_guitar()
    {
        var path = await BuildSevenStringCompanionAsync();
        var bytes = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        Assert.Equal(0, bytes[31] & 4);
        Assert.Equal(Rsxr.RsxrTestSupport.PackageMagic(true), bytes[..4]);
        Assert.EndsWith("_rsxr_p.psarc", path);
        Assert.Empty(Directory.GetFiles(_dir, "*.building*"));
        Assert.Empty(Directory.GetFiles(_dir, "*.chords-ext.json"));
    }

    [Fact]
    public async Task Psarc_build_warnings_reach_the_log_instead_of_being_discarded()
    {
        WriteWav(Path.Combine(_dir, "song.wav"));

        var logger = new FakeAppLogger();
        var service = Create(
            new AppConfig { SaveCompanionPsarc = true, SaveCompanionPsarcCodec = "Vorbis" }, logger);

        var path = await service.BuildPsarcAsync(
            Path.Combine(_dir, "MySong.rscp"), CreateProject(), audioLengthMs: null, CancellationToken.None);

        Assert.NotNull(path);

        // The fixture has no show lights, so the builder always warns about it —
        // a stand-in for the ones that actually matter ("arrangement X was
        // skipped", "if the song is silent in-game, this is why"). The builder
        // logs only a COUNT, so unless this path re-logs the texts they reach
        // nobody at all on the Ctrl+S companion path.
        Assert.True(
            logger.HasEntryContaining("WARN", "show lights"),
            "every build warning must be logged, not just counted");
    }

    /// <summary>Config is hand-editable, so a typo must not take down every save; and
    /// "ImaAdpcm", the stored default before #717, must not keep producing packages
    /// the game cannot play. Everything but "Pcm" reads as Vorbis.</summary>
    [Theory]
    [InlineData("Vorbis", WemCodec.Vorbis)]
    [InlineData("vorbis", WemCodec.Vorbis)]
    [InlineData("ImaAdpcm", WemCodec.Vorbis)]
    [InlineData("imaadpcm", WemCodec.Vorbis)]
    [InlineData("not-a-codec", WemCodec.Vorbis)]
    [InlineData("", WemCodec.Vorbis)]
    [InlineData(null, WemCodec.Vorbis)]
    [InlineData("Pcm", WemCodec.Pcm)]
    [InlineData("PCM", WemCodec.Pcm)]
    public void The_codec_setting_never_reads_as_adpcm(string? configured, WemCodec expected)
    {
        Assert.Equal(expected, SaveCompanionService.ParseCodec(configured));
        Assert.Equal("Vorbis", new AppConfig().SaveCompanionPsarcCodec);
    }
}
