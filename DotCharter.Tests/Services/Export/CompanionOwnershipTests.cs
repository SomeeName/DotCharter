using DotCharter.Services.Export;
using DotCharter.Services.Export.DlcBuilder;
using DotCharter.Tests.Services.Parsing.Eof;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Export;

/// <summary>
///     A save may only overwrite the companion files it wrote itself.
///     <para>
///         Owner report, 2026-08-27: <i>"does dotcharter overwrite
///         Yorushika_August-A-Certain-Moonlight.rs2dlc […] i manually edited the
///         file already causing my tones i set to get lost"</i>. It did — every
///         save, unconditionally, and DotCharter always writes the tone fields
///         EMPTY, so a Ctrl+S made for an unrelated reason silently blanked work
///         done in DLC Builder.
///     </para>
///     <para>
///         The <c>.eof</c> is the same hazard for the same reason: it is Editor on
///         Fire's own project format, not a derived artifact. The <c>DTRS XML</c>
///         bundle and the <c>.psarc</c> are NOT — nobody hand-edits those, and they
///         stay unconditional on purpose.
///     </para>
/// </summary>
public sealed class CompanionOwnershipTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dotcharter_ownership_").FullName;

    public void Dispose() => BestEffortDelete.Directory(_dir);

    // ── the primitive ───────────────────────────────────────────────────

    [Fact]
    public void A_file_we_have_never_written_is_left_alone()
    {
        var path = Path.Combine(_dir, "someone-elses.txt");
        File.WriteAllText(path, "hand written");

        var wrote = CompanionOwnership.WriteIfUnmodified(path, p => File.WriteAllText(p, "ours"),
            out var reason);

        Assert.False(wrote);
        Assert.Contains("not written by DotCharter", reason);
        Assert.Equal("hand written", File.ReadAllText(path));
    }

    [Fact]
    public void A_file_we_wrote_and_nobody_touched_is_regenerated()
    {
        var path = Path.Combine(_dir, "ours.txt");

        Assert.True(CompanionOwnership.WriteIfUnmodified(path, p => File.WriteAllText(p, "v1"), out _));
        Assert.True(CompanionOwnership.WriteIfUnmodified(path, p => File.WriteAllText(p, "v2"),
            out var reason));

        Assert.Null(reason);
        Assert.Equal("v2", File.ReadAllText(path));
    }

    [Fact]
    public void A_file_we_wrote_and_somebody_edited_is_left_alone()
    {
        var path = Path.Combine(_dir, "ours.txt");
        Assert.True(CompanionOwnership.WriteIfUnmodified(path, p => File.WriteAllText(p, "v1"), out _));

        File.WriteAllText(path, "v1 + the charter's own edit");

        var wrote = CompanionOwnership.WriteIfUnmodified(path, p => File.WriteAllText(p, "v2"),
            out var reason);

        Assert.False(wrote);
        Assert.Contains("edited", reason);
        Assert.Equal("v1 + the charter's own edit", File.ReadAllText(path));
    }

    /// <summary>
    ///     Once refused, refused for good: a later save must not decide the file is
    ///     ours again. The stamp is only ever advanced by a write we performed.
    /// </summary>
    [Fact]
    public void A_refusal_sticks_across_later_saves()
    {
        var path = Path.Combine(_dir, "ours.txt");
        Assert.True(CompanionOwnership.WriteIfUnmodified(path, p => File.WriteAllText(p, "v1"), out _));
        File.WriteAllText(path, "edited");

        for (var save = 0; save < 3; save++)
        {
            Assert.False(CompanionOwnership.WriteIfUnmodified(path, p => File.WriteAllText(p, "v2"),
                out _));
        }

        Assert.Equal("edited", File.ReadAllText(path));
    }

    /// <summary>Deleting the companion is how the charter hands it back to us.</summary>
    [Fact]
    public void Deleting_the_companion_lets_the_next_save_recreate_it()
    {
        var path = Path.Combine(_dir, "ours.txt");
        Assert.True(CompanionOwnership.WriteIfUnmodified(path, p => File.WriteAllText(p, "v1"), out _));
        File.WriteAllText(path, "edited");
        Assert.False(CompanionOwnership.WriteIfUnmodified(path, p => File.WriteAllText(p, "v2"), out _));

        File.Delete(path);

        Assert.True(CompanionOwnership.WriteIfUnmodified(path, p => File.WriteAllText(p, "v3"), out _));
        Assert.Equal("v3", File.ReadAllText(path));
    }

    /// <summary>
    ///     One sibling for the whole folder, and NOT hidden — it is the only place
    ///     the charter can see why a companion stopped following the chart.
    /// </summary>
    [Fact]
    public void The_stamp_file_is_a_single_visible_sibling()
    {
        CompanionOwnership.WriteIfUnmodified(Path.Combine(_dir, "a.txt"), p => File.WriteAllText(p, "a"),
            out _);
        CompanionOwnership.WriteIfUnmodified(Path.Combine(_dir, "b.txt"), p => File.WriteAllText(p, "b"),
            out _);

        var stamp = Path.Combine(_dir, CompanionOwnership.StampFileName);
        Assert.True(File.Exists(stamp));
        Assert.Contains("a.txt", File.ReadAllText(stamp));
        Assert.Contains("b.txt", File.ReadAllText(stamp));

        if (OperatingSystem.IsWindows())
        {
            Assert.False(File.GetAttributes(stamp).HasFlag(FileAttributes.Hidden),
                "a hidden stamp file also refuses a plain File.WriteAllText, and hides the answer");
        }
    }

    /// <summary>A stamp file the charter (or a sync client) corrupted must protect
    /// the companions, not throw into the save path.</summary>
    [Fact]
    public void A_corrupt_stamp_file_refuses_rather_than_throwing()
    {
        var path = Path.Combine(_dir, "ours.txt");
        Assert.True(CompanionOwnership.WriteIfUnmodified(path, p => File.WriteAllText(p, "v1"), out _));

        File.WriteAllText(Path.Combine(_dir, CompanionOwnership.StampFileName), "{ not json");

        Assert.False(CompanionOwnership.WriteIfUnmodified(path, p => File.WriteAllText(p, "v2"), out _));
        Assert.Equal("v1", File.ReadAllText(path));
    }

    // ── the .rs2dlc, which is what the owner lost ───────────────────────

    [Fact]
    public void A_hand_edited_rs2dlc_survives_the_next_save()
    {
        var service = Create(new AppConfig { SaveCompanionRs2Dlc = true });
        var projectPath = Path.Combine(_dir, "MySong.rscp");
        Directory.CreateDirectory(DlcBuilderBundle.DirectoryFor(projectPath));

        var project = CreateProject();
        var written = service.WriteDlcBuilderProject(projectPath, project, 60_000);
        Assert.NotNull(written);

        // What the charter does in DLC Builder: assign tones. DotCharter now writes
        // its OWN tone bodies here (see Rs2DlcToneBodyTests), so a rewrite is no
        // longer guaranteed loss - but it is still not ours to make.
        var edited = File.ReadAllText(written!)
            .Replace("\"Tones\": []", "\"Tones\": [ \"my hand built tone\" ]");
        File.WriteAllText(written!, edited);

        // Change something that does NOT rename the file — the name is derived from
        // artist + title, and a rename would write a second file, not test the guard.
        project.Chart.AlbumYear = "1999";
        Assert.Null(service.WriteDlcBuilderProject(projectPath, project, 60_000));

        Assert.Equal(edited, File.ReadAllText(written!));
    }

    /// <summary>
    ///     The same guard, now that the file has something worth losing: the app
    ///     writes tone BODIES into <c>Tones</c>, so a charter who reshapes one in
    ///     DLC Builder has real work in there. The stamp is what protects it - the
    ///     companion is only ever rewritten when the file present is one WE wrote.
    /// </summary>
    [Fact]
    public void A_rs2dlc_whose_tone_bodies_were_edited_is_never_overwritten()
    {
        var service = Create(new AppConfig { SaveCompanionRs2Dlc = true });
        var projectPath = Path.Combine(_dir, "MySong.rscp");
        Directory.CreateDirectory(DlcBuilderBundle.DirectoryFor(projectPath));

        var project = CreateProject();
        project.ToneDefinitions.Add(new ToneDefinition
        {
            Key = "Lead",
            Name = "Lead",
            Json = """{"GearList":{"Amp":{"Type":"Amps","KnobValues":{"Amp_OrangeAD50_Gain":33},"Key":"Amp_OrangeAD50"}},"ToneDescriptors":[],"NameSeparator":" - ","IsCustom":true,"Volume":"-22.000","Key":"Lead","Name":"Lead"}"""
        });
        project.Arrangements[0].ToneBase = "Lead";

        var written = service.WriteDlcBuilderProject(projectPath, project, 60_000);
        Assert.NotNull(written);
        Assert.Contains("Amp_OrangeAD50", File.ReadAllText(written!));

        // The charter opens it in DLC Builder and turns the gain down.
        var edited = File.ReadAllText(written!).Replace("\"Amp_OrangeAD50_Gain\": 33", "\"Amp_OrangeAD50_Gain\": 12");
        Assert.NotEqual(edited, File.ReadAllText(written!));
        File.WriteAllText(written!, edited);

        project.Chart.AlbumYear = "1999";
        Assert.Null(service.WriteDlcBuilderProject(projectPath, project, 60_000));
        Assert.Equal(edited, File.ReadAllText(written!));
    }

    [Fact]
    public void An_untouched_rs2dlc_still_follows_the_chart()
    {
        var service = Create(new AppConfig { SaveCompanionRs2Dlc = true });
        var projectPath = Path.Combine(_dir, "MySong.rscp");
        Directory.CreateDirectory(DlcBuilderBundle.DirectoryFor(projectPath));

        var project = CreateProject();
        var written = service.WriteDlcBuilderProject(projectPath, project, 60_000);
        Assert.NotNull(written);
        Assert.Contains("My Song", File.ReadAllText(written!));

        project.Chart.AlbumYear = "1999";
        Assert.Equal(written, service.WriteDlcBuilderProject(projectPath, project, 60_000));
        Assert.Contains("1999", File.ReadAllText(written!));
    }

    /// <summary>
    ///     The owner's file predates the stamp entirely — that is exactly the state
    ///     every existing project is in on upgrade, and it is the one that must not
    ///     be clobbered.
    /// </summary>
    [Fact]
    public void An_rs2dlc_from_before_this_feature_is_treated_as_hand_authored()
    {
        var logger = new FakeAppLogger();
        var service = Create(new AppConfig { SaveCompanionRs2Dlc = true }, logger);
        var projectPath = Path.Combine(_dir, "MySong.rscp");
        Directory.CreateDirectory(DlcBuilderBundle.DirectoryFor(projectPath));

        var project = CreateProject();
        var path = Path.Combine(_dir, Rs2DlcProjectBuilder.FileNameFor(project));
        File.WriteAllText(path, "{ \"Tones\": [ \"from an older DotCharter, then edited\" ] }");

        Assert.Null(service.WriteDlcBuilderProject(projectPath, project, 60_000));
        Assert.Contains("from an older DotCharter", File.ReadAllText(path));

        // A silent skip is a mystery; the log is where the charter finds out the
        // companion has stopped tracking the chart.
        Assert.True(logger.HasEntryContaining("INFO", "Leaving the .rs2dlc alone"),
            "the refusal must say so somewhere the charter can read it");
    }

    // ── the .eof, the same hazard ───────────────────────────────────────

    [Fact]
    public void A_hand_edited_eof_survives_the_next_save()
    {
        var service = Create(new AppConfig { SaveCompanionEof = true, SaveCompanionEofAudio = false });
        var projectPath = Path.Combine(_dir, "MySong.rscp");

        var eofPath = service.WriteEof(projectPath, EofExportTestCharts.BuildRepresentativeProject());
        Assert.NotNull(eofPath);

        // Stand-in for "the charter opened it in EOF and saved" — any byte change
        // is the same signal.
        var edited = File.ReadAllBytes(eofPath!);
        edited[^1] ^= 0xFF;
        File.WriteAllBytes(eofPath!, edited);

        Assert.Null(service.WriteEof(projectPath, EofExportTestCharts.BuildRepresentativeProject()));
        Assert.Equal(edited, File.ReadAllBytes(eofPath!));
    }

    [Fact]
    public void An_untouched_eof_is_rewritten_on_every_save()
    {
        var service = Create(new AppConfig { SaveCompanionEof = true, SaveCompanionEofAudio = false });
        var projectPath = Path.Combine(_dir, "MySong.rscp");

        Assert.NotNull(service.WriteEof(projectPath, EofExportTestCharts.BuildRepresentativeProject()));
        Assert.NotNull(service.WriteEof(projectPath, EofExportTestCharts.BuildRepresentativeProject()));
    }

    // ---- helpers ------------------------------------------------------------

    private static SaveCompanionService Create(AppConfig config, FakeAppLogger? logger = null) =>
        new(new FakeConfigurationService { AppConfig = config }, logger ?? new FakeAppLogger());

    private static ChartProject CreateProject()
    {
        var project = new ChartProject
        {
            Bpm = 120,
            AudioFileName = "song.wav",
            Chart = { SongName = "My Song", Artist = "Tester" }
        };

        for (var i = 0; i < 32; i++)
        {
            project.Beats.Add(new BeatMarker
            {
                Time = i * 500.0,
                IsMeasure = i % 4 == 0,
                Bpm = 120,
                BeatsInMeasure = 4,
                NoteDenominator = 4
            });
        }

        var arrangement = new Arrangement { Name = "Lead" };
        for (var i = 0; i < 6; i++) arrangement.TuningPitches.Add(new TuningOffset(0));
        arrangement.Levels.Add(new ChartLevel { DifficultyIndex = 0 });
        project.Arrangements.Add(arrangement);

        return project;
    }
}
