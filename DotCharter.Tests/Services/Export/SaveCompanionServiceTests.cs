using DotCharter.Services.Export;
using DotCharter.Services.Export.Audio;
using DotCharter.Services.Parsing.Eof;
using DotCharter.Tests.Services.Parsing.Eof;

namespace DotCharter.Tests.Services.Export;

/// <summary>The tests of <see cref="SaveCompanionServiceTests" /> that need no package reader; its fixtures are in <c>SaveCompanionServiceTests.Fixtures.cs</c>.</summary>
public sealed partial class SaveCompanionServiceTests
{
    [Fact]
    public void Writing_the_eof_puts_it_next_to_the_project_and_it_reimports()
    {
        var service = Create(new AppConfig { SaveCompanionEof = true, SaveCompanionEofAudio = false });
        var projectPath = Path.Combine(_dir, "MySong.rscp");

        // A chart with real notes — EOF only reports tracks that have content.
        var eofPath = service.WriteEof(projectPath, EofExportTestCharts.BuildRepresentativeProject());

        Assert.Equal(Path.Combine(_dir, "MySong.eof"), eofPath);
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
        Assert.Equal(_dir, Path.GetDirectoryName(path));

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
    public async Task Psarc_is_built_encrypted_for_a_seven_string_guitar()
    {
        // #736, #804: a 7-string guitar ships for the rs7string mod instead of being refused, encrypted like any package.
        var path = await BuildSevenStringCompanionAsync();
        var bytes = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        Assert.Equal(4, bytes[31] & 4); // encrypted TOC (#804)
        Assert.Empty(Directory.GetFiles(_dir, "*.building*"));
        // The low-B chord note is in the SNG's RS7X block (#777), not in a sidecar.
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
