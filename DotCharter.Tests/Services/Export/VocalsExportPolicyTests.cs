using System.Text;
using DotCharter.Core.Domain.Vocals;
using DotCharter.Services.Export;
using DotCharter.Services.Export.DlcBuilder;
using DotCharter.Services.Infrastructure.Documents;
using DotCharter.Services.Parsing;
using DotCharter.Services.Parsing.Eof;
using DotCharter.Services.Parsing.Rscp;
using DotCharter.Services.Parsing.Xml;
using DotCharter.Tone.Infrastructure.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Tests.Services.Export;

/// <summary>
///     <c>AppConfig.ExportIncludeVocals</c> used to be honoured by the psarc/SNG
///     path ALONE. The companion artifacts a save also writes — the
///     <c>.rs2dlc</c>, the DTRS XML bundle and the <c>.eof</c> — each rebuilt
///     the "which vocal tracks count" test for themselves and none of them
///     consulted the flag, so switching vocals off still shipped lyrics. Every writer now asks
///     <see cref="VocalsExportPolicy" />, and these cases hold each one to it.
/// </summary>
public sealed class VocalsExportPolicyTests : IDisposable
{
    private const string LyricWord = "Zebrafish";

    private readonly string _dir =
        Directory.CreateTempSubdirectory("dotcharter_vocalspolicy_").FullName;

    public void Dispose() => BestEffortDelete.Directory(_dir);

    // ---- the shared decision itself ---------------------------------------

    [Fact]
    public void The_policy_reads_the_flag_and_defaults_to_including_vocals()
    {
        Assert.True(VocalsExportPolicy.IncludeVocals(new AppConfig()));
        Assert.True(VocalsExportPolicy.IncludeVocals(null));
        Assert.False(VocalsExportPolicy.IncludeVocals(new AppConfig { ExportIncludeVocals = false }));
    }

    [Fact]
    public void With_vocals_off_no_track_is_written_however_many_lyrics_it_holds()
    {
        var project = BuildProject();

        Assert.Single(VocalsExportPolicy.TracksToWrite(project, includeVocals: true));
        Assert.Empty(VocalsExportPolicy.TracksToWrite(project, includeVocals: false));
        Assert.False(VocalsExportPolicy.ShouldWrite(project.VocalTracks[0], includeVocals: false));
    }

    [Fact]
    public void A_lyricless_track_is_still_skipped_when_vocals_are_on()
    {
        var project = BuildProject();
        project.VocalTracks.Add(new VocalTrack { Name = "Empty" });

        Assert.Single(VocalsExportPolicy.TracksToWrite(project, includeVocals: true));
    }

    /// <summary>#283: a package carries ONE Vocals arrangement, the first track that has
    /// lyrics, with its project index so the bundle file name still matches.</summary>
    [Fact]
    public void A_package_carries_the_first_lyric_bearing_track_only()
    {
        var project = WithASecondLyricTrackAndALeadingEmptyOne(BuildProject());

        var picked = VocalsExportPolicy.PackageTrack(project, includeVocals: true);

        Assert.Equal((project.VocalTracks[1], 1), picked);
        Assert.Null(VocalsExportPolicy.PackageTrack(project, includeVocals: false));
    }

    /// <summary>#283: the .rs2dlc named every lyric-bearing track as a Vocals arrangement
    /// while the psarc carried one. Both carry the same one now.</summary>
    [Fact]
    public void The_rs2dlc_names_one_vocals_arrangement_like_the_psarc()
    {
        var project = WithASecondLyricTrackAndALeadingEmptyOne(BuildProject());

        var dto = Rs2DlcProjectBuilder.Build(project, new Rs2DlcProjectBuilder.Rs2DlcBuildOptions(
            AudioFileName: "song.ogg",
            CoverArtFileName: "",
            AudioLengthMs: 20_000,
            FallbackCharter: "Tester",
            IncludeVocals: true));

        var vocals = Assert.Single(dto.Arrangements, a => a.Case == "Vocals");
        Assert.Equal(Path.Combine("DTRS XML", DlcBuilderBundle.VocalTrackFileName(2, project.VocalTracks[1])),
            ((Rs2DlcVocalsDto)vocals.Fields[0]).Xml);
    }

    private static ChartProject WithASecondLyricTrackAndALeadingEmptyOne(ChartProject project)
    {
        project.VocalTracks.Insert(0, new VocalTrack { Name = "Empty" });
        var harmony = new VocalTrack { Name = "Harmony" };
        var phrase = new LyricPhrase { Position = new FractionalPosition(1), EndPosition = new FractionalPosition(2) };
        phrase.Lyrics.Add(new LyricEvent
        {
            Position = new FractionalPosition(1), EndPosition = new FractionalPosition(2), Text = "Ooh", IsPhraseEnd = true
        });
        harmony.Phrases.Add(phrase);
        project.VocalTracks.Add(harmony);
        return project;
    }

    // ---- the four companion writers ---------------------------------------

    [Fact]
    public void The_eof_companion_writes_no_lyrics_when_vocals_are_off()
    {
        var project = BuildProject();

        var withVocals = EofExporter.Export(project).Bytes;
        var withoutVocals = EofExporter.Export(project, includeVocals: false).Bytes;

        Assert.Contains(LyricWord, Encoding.ASCII.GetString(withVocals), StringComparison.Ordinal);
        Assert.DoesNotContain(LyricWord, Encoding.ASCII.GetString(withoutVocals), StringComparison.Ordinal);
    }

    [Fact]
    public void The_rs2dlc_names_no_vocals_arrangement_when_vocals_are_off()
    {
        var project = BuildProject();

        Assert.Contains(Build(project, includeVocals: true).Arrangements, a => a.Case == "Vocals");
        Assert.DoesNotContain(Build(project, includeVocals: false).Arrangements, a => a.Case == "Vocals");

        // The instrumental half is untouched by the flag — turning vocals off
        // must not quietly drop the arrangement the charter came for.
        Assert.Contains(Build(project, includeVocals: false).Arrangements, a => a.Case == "Instrumental");
        return;

        static Rs2DlcProjectDto Build(ChartProject p, bool includeVocals) =>
            Rs2DlcProjectBuilder.Build(p, new Rs2DlcProjectBuilder.Rs2DlcBuildOptions(
                AudioFileName: "song.ogg",
                CoverArtFileName: "",
                AudioLengthMs: 20_000,
                FallbackCharter: "Tester",
                IncludeVocals: includeVocals));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_xml_bundle_writes_a_vocals_file_only_when_vocals_are_on(bool includeVocals)
    {
        var services = new ServiceCollection();
        services.AddKeyedSingleton<IChartSerializer, RscpChartSerializer>("rscp");
        services.AddKeyedSingleton<IChartSerializer, XmlChartSerializer>("xml");
        services.AddSingleton<IConfigurationService>(new FakeConfigurationService
        {
            AppConfig = new AppConfig { ExportIncludeVocals = includeVocals }
        });
        using var provider = services.BuildServiceProvider();

        var service = new ProjectDocumentService(provider, new FakeAppLogger());
        service.SaveDlcBuilderXmlBundle(Path.Combine(_dir, "project.rscp"), BuildProject());

        var bundle = Path.Combine(_dir, DlcBuilderBundle.DirectoryName);
        Assert.NotEmpty(UiSourceTree.GetFiles(bundle, "*_Lead_*.xml"));
        Assert.Equal(includeVocals, UiSourceTree.GetFiles(bundle, "*_Vocals_*.xml").Length == 1);
    }

    // ---- the flag has exactly ONE reader ----------------------------------

    [Fact]
    public void Only_the_policy_reads_ExportIncludeVocals()
    {
        var offenders = new List<string>();
        var declarations = 0;

        foreach (var project in new[] { "DotCharter.Services", "DotCharter.Tone", "DotCharter.UI", "DotCharter.Presentation" })
        {
            foreach (var file in UiSourceTree.EnumerateFiles(
                     Path.Combine(TestPaths.RepoRoot(), project), "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                        StringComparison.Ordinal))
                {
                    continue;
                }

                var name = Path.GetFileName(file);
                var text = SourceText.StripComments(UiSourceTree.ReadAllText(file));
                if (!text.Contains("ExportIncludeVocals", StringComparison.Ordinal)) continue;

                // AppConfig declares it; the policy is its one reader; the export
                // dialog is the one WRITER (the checkbox the charter ticks).
                if (name is "AppConfig.cs") { declarations++; continue; }
                if (name is "VocalsExportPolicy.cs" or "RocksmithExportViewModel.cs" or "RocksmithExportViewModel.Build.cs" or "RocksmithExportSupport.cs") continue;

                offenders.Add(Path.GetRelativePath(TestPaths.RepoRoot(), file));
            }
        }

        Assert.True(declarations == 1,
            "AppConfig no longer declares ExportIncludeVocals — this lint is passing vacuously.");
        Assert.True(offenders.Count == 0,
            "these files read AppConfig.ExportIncludeVocals directly instead of going through " +
            "VocalsExportPolicy, which is how four companion writers came to ignore it:\n  " +
            string.Join("\n  ", offenders));
    }

    // ---- helpers ----------------------------------------------------------

    private static ChartProject BuildProject()
    {
        var project = new ChartProject
        {
            AudioFileName = "song.ogg",
            Bpm = 120,
            Chart =
            {
                SongName = "Test Song", Artist = "Test Artist",
                AlbumName = "Test Album", AlbumYear = "2026"
            }
        };

        for (var i = 0; i < 16; i++)
        {
            project.Beats.Add(new BeatMarker
            {
                Time = i * 500.0,
                Bpm = 120,
                BeatsInMeasure = 4,
                NoteDenominator = 4,
                IsMeasure = i % 4 == 0
            });
        }

        var arrangement = new Arrangement
        {
            ArrangementType = ArrangementType.Lead,
            Subtype = ArrangementSubtype.Main,
            Name = "Lead Guitar",
            StringCount = 6
        };
        for (var i = 0; i < 6; i++) arrangement.TuningPitches.Add(new TuningOffset(0));
        arrangement.Levels.Add(new ChartLevel { DifficultyIndex = 0 });
        arrangement.Levels[0].Notes.Add(new GuitarNote
        {
            Position = new FractionalPosition(1), StringIndex = 0, Fret = 3
        });
        project.Arrangements.Add(arrangement);

        var track = new VocalTrack { Name = "Lead Vox" };
        var phrase = new LyricPhrase
        {
            Position = FractionalPosition.Zero,
            EndPosition = new FractionalPosition(2)
        };
        phrase.Lyrics.Add(new LyricEvent
        {
            Text = LyricWord,
            Position = FractionalPosition.Zero,
            EndPosition = new FractionalPosition(1)
        });
        track.Phrases.Add(phrase);
        project.VocalTracks.Add(track);

        return project;
    }
}
