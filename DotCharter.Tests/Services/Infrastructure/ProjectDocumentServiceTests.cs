using System.Collections.ObjectModel;
using System.Xml.Linq;
using DotCharter.Core.Domain.Vocals;
using DotCharter.Services.Infrastructure.Documents;
using DotCharter.Services.Parsing;
using DotCharter.Services.Parsing.Rscp;
using DotCharter.Services.Parsing.Xml;
using DotCharter.Tone.Infrastructure.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Tests.Services.Infrastructure;

public class ProjectDocumentServiceTests : IDisposable
{
    private readonly ServiceProvider _serviceProvider;
    private readonly string _tempDirectory;

    public ProjectDocumentServiceTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "DotCharterProjectDocumentTests", Guid.NewGuid().ToString("N"));

        var services = new ServiceCollection();
        services.AddKeyedSingleton<IChartSerializer, RscpChartSerializer>("rscp");
        services.AddKeyedSingleton<IChartSerializer, XmlChartSerializer>("xml");
        _serviceProvider = services.BuildServiceProvider();
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
        BestEffortDelete.Directory(_tempDirectory);
    }

    [Fact]
    public void RefreshArrangementFileManifest_UsesRsStyleArrangementFileNames()
    {
        var service = CreateService();
        var project = new ChartProject();
        project.Arrangements.Add(new Arrangement
        {
            ArrangementType = ArrangementType.Lead,
            Subtype = ArrangementSubtype.Main
        });
        project.Arrangements.Add(new Arrangement
        {
            ArrangementType = ArrangementType.Rhythm,
            Subtype = ArrangementSubtype.Bonus
        });

        service.RefreshArrangementFileManifest(project);

        Assert.Equal(["1_Lead_Main_RS2.xml", "2_Rhythm_Bonus_RS2.xml"], project.ArrangementFiles);
    }

    [Fact]
    public void SaveDlcBuilderXmlBundle_WritesArrangementVocalsAndShowlightsToDtrsXmlFolder()
    {
        Directory.CreateDirectory(_tempDirectory);
        var service = CreateService();
        var project = BuildProject();
        var projectPath = Path.Combine(_tempDirectory, "project.rscp");

        service.SaveDlcBuilderXmlBundle(projectPath, project);

        var exportDirectory = Path.Combine(_tempDirectory, "DTRS XML");
        Assert.True(Directory.Exists(exportDirectory));

        var arrangementPath = Path.Combine(exportDirectory, "1_Lead_Main_RS2.xml");
        var vocalsPath = Path.Combine(exportDirectory, "1_Vocals_Lead Vox_RS2.xml");
        var showlightsPath = Path.Combine(exportDirectory, "showlights.xml");

        Assert.True(File.Exists(arrangementPath));
        Assert.True(File.Exists(vocalsPath));
        Assert.True(File.Exists(showlightsPath));

        Assert.Equal("song", XDocument.Load(arrangementPath).Root?.Name.LocalName);
        Assert.Equal("vocals", XDocument.Load(vocalsPath).Root?.Name.LocalName);
        Assert.Equal("showlights", XDocument.Load(showlightsPath).Root?.Name.LocalName);
    }

    [Fact]
    public void SaveDlcBuilderXmlBundle_GeneratesPhraseStructureForUnphrasedCharts()
    {
        // The DTRS XML is the DLC-Builder-importable artifact; a chart with no
        // authored phrases used to export with phrases/phraseIterations/
        // sections all count="0" — a structureless arrangement nothing
        // downstream (DDC, DLC Builder, EOF re-import) can work with. The
        // psarc build path already generated the COUNT/p0…pN/END scaffold;
        // the bundle must too — but only on the written clone.
        Directory.CreateDirectory(_tempDirectory);
        var service = CreateService();
        var project = BuildProject();
        var level = project.Arrangements[0].Levels[0];
        for (var i = 0; i < 4; i++)
            level.Notes.Add(new GuitarNote { Position = new FractionalPosition(i + 1), StringIndex = 0, Fret = 3 });

        service.SaveDlcBuilderXmlBundle(Path.Combine(_tempDirectory, "project.rscp"), project);

        var doc = XDocument.Load(Path.Combine(_tempDirectory, "DTRS XML", "1_Lead_Main_RS2.xml"));
        int Count(string el) => int.Parse(doc.Root!.Element(el)!.Attribute("count")!.Value);
        Assert.True(Count("phrases") >= 2, "expected at least the COUNT and END phrases");
        Assert.True(Count("phraseIterations") >= 2, "every phrase must be placed");
        Assert.True(Count("sections") >= 1, "expected at least the riff section");

        // Derived output only — the live project graph must stay untouched.
        Assert.Empty(project.Arrangements[0].Phrases);
        Assert.Empty(project.Arrangements[0].PhraseIterations);
        Assert.Empty(project.Arrangements[0].EventPoints);
    }

    [Fact]
    public void SaveDlcBuilderXmlBundle_RemovesStaleXmlFilesBeforeRegenerating()
    {
        Directory.CreateDirectory(Path.Combine(_tempDirectory, "DTRS XML"));
        File.WriteAllText(Path.Combine(_tempDirectory, "DTRS XML", "stale.xml"), "<stale/>");
        var service = CreateService();
        var project = BuildProject();

        service.SaveDlcBuilderXmlBundle(Path.Combine(_tempDirectory, "project.rscp"), project);

        Assert.False(File.Exists(Path.Combine(_tempDirectory, "DTRS XML", "stale.xml")));
    }

    /// <summary>
    ///     The bundle swap must MOVE the old folder aside, never delete it in place.
    ///     A recursive delete is not atomic: it removes files one by one, so a single
    ///     locked file left the export folder HALF-deleted and the cleanup then wiped
    ///     staging too — both copies of the bundle gone. A rename is all-or-nothing,
    ///     so a locked file now leaves the previous bundle completely intact.
    /// </summary>
    [Fact]
    public void SaveDlcBuilderXmlBundle_WhenTheExportFolderCannotBeReplaced_LeavesThePreviousBundleIntact()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Relies on Windows file locking.");

        var exportDirectory = Path.Combine(_tempDirectory, "DTRS XML");
        Directory.CreateDirectory(exportDirectory);

        // A complete previous bundle, plus a file something else holds open.
        var previousArrangement = Path.Combine(exportDirectory, "1_Lead_Main_RS2.xml");
        File.WriteAllText(previousArrangement, "<song id=\"previous\" />");
        var lockedPath = Path.Combine(exportDirectory, "locked.xml");
        File.WriteAllText(lockedPath, "<locked/>");

        var service = CreateService();
        var project = BuildProject();

        using (new FileStream(lockedPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.ThrowsAny<IOException>(() =>
                service.SaveDlcBuilderXmlBundle(Path.Combine(_tempDirectory, "project.rscp"), project));
        }

        // Nothing was destroyed: the previous bundle is still whole (the old
        // delete-in-place would have removed this arrangement before it hit the
        // locked file), and staging did not survive.
        Assert.Equal("<song id=\"previous\" />", File.ReadAllText(previousArrangement));
        Assert.True(File.Exists(lockedPath));
        Assert.False(Directory.Exists(exportDirectory + ".saving"));
        Assert.False(Directory.Exists(exportDirectory + ".old"));
    }

    [Fact]
    public void SaveDlcBuilderXmlBundle_CleansUpAStaleOldFolderFromAnEarlierFailure()
    {
        var exportDirectory = Path.Combine(_tempDirectory, "DTRS XML");
        Directory.CreateDirectory(exportDirectory + ".old");
        File.WriteAllText(Path.Combine(exportDirectory + ".old", "leftover.xml"), "<leftover/>");

        CreateService().SaveDlcBuilderXmlBundle(Path.Combine(_tempDirectory, "project.rscp"), BuildProject());

        Assert.True(File.Exists(Path.Combine(exportDirectory, "1_Lead_Main_RS2.xml")));
        Assert.False(Directory.Exists(exportDirectory + ".old"));
    }

    /// <summary>
    ///     The save coordinator writes to <c>project.rscp.saving</c> and swaps the
    ///     temp in afterwards (crash-durable save). The format dispatch must see the
    ///     REAL extension under the temp suffix — dispatching on <c>.saving</c>
    ///     itself threw NotSupportedException, which broke EVERY project save
    ///     (shipped broken since at least v0.20; caught by the owner 2026-07-17).
    ///     This test replays the coordinator's exact pattern end-to-end.
    /// </summary>
    [Fact]
    public void SaveChart_ToTheAtomicTempPath_WritesRscpAndSurvivesTheSwap()
    {
        Directory.CreateDirectory(_tempDirectory);
        var service = CreateService();
        var project = BuildProject();
        var projectPath = Path.Combine(_tempDirectory, "project.rscp");
        var tempPath = projectPath + ".saving";

        service.SaveChart(tempPath, project);
        Assert.True(File.Exists(tempPath));

        File.Move(tempPath, projectPath, overwrite: true);
        var roundTripped = service.LoadChart(projectPath);

        Assert.Equal("Test Song", roundTripped.Chart?.SongName);
        Assert.Equal(project.Beats.Count, roundTripped.Beats.Count);
        Assert.Single(roundTripped.Arrangements);
    }

    /// <summary>A leftover temp from a crashed save must stay loadable directly.</summary>
    [Fact]
    public void LoadChart_FromALeftoverAtomicTempFile_Loads()
    {
        Directory.CreateDirectory(_tempDirectory);
        var service = CreateService();
        var tempPath = Path.Combine(_tempDirectory, "project.rscp.SAVING");

        service.SaveChart(tempPath, BuildProject());

        Assert.Equal("Test Song", service.LoadChart(tempPath).Chart?.SongName);
    }

    [Fact]
    public void SaveChart_UnknownExtension_StillThrows()
    {
        var service = CreateService();

        Assert.Throws<NotSupportedException>(() =>
            service.SaveChart(Path.Combine(_tempDirectory, "project.foo"), BuildProject()));
        Assert.Throws<NotSupportedException>(() =>
            service.SaveChart(Path.Combine(_tempDirectory, "project.saving"), BuildProject()));
    }

    // ── songLength plumbing (EOF: min(chart, audio) - 1 ms) ──────────────
    //
    // RsXmlSongWriter has implemented the rule since 0.33.2, but nothing passed
    // it an audio length, so every export silently took the no-audio fallback.
    // ChartProject carries no duration (it lives on ProjectState), so the value
    // is an explicit argument threaded from the UI through these two entry
    // points. These tests pin the wiring, not the arithmetic.

    /// <summary>
    ///     The clamp is the whole point of EOF's rule: a chart longer than its
    ///     audio must end WITH the audio, or the arrangement never reaches an end
    ///     in-game.
    /// </summary>
    [Fact]
    public void SaveDlcBuilderXmlBundle_WhenAudioIsShorterThanTheChart_ClampsSongLengthToTheAudio()
    {
        Directory.CreateDirectory(_tempDirectory);
        var projectPath = Path.Combine(_tempDirectory, "project.rscp");

        // BuildProject's last beat is at 3500 ms; the audio stops at 2000 ms.
        CreateService().SaveDlcBuilderXmlBundle(projectPath, BuildProject(), 2000);

        Assert.Equal("1.999", ExportedSongLength());
    }

    [Fact]
    public void SaveDlcBuilderXmlBundle_WhenAudioOutlastsTheChart_UsesTheChartLength()
    {
        Directory.CreateDirectory(_tempDirectory);
        var projectPath = Path.Combine(_tempDirectory, "project.rscp");

        CreateService().SaveDlcBuilderXmlBundle(projectPath, BuildProject(), 10_000);

        Assert.Equal("3.499", ExportedSongLength());
    }

    [Fact]
    public void SaveDlcBuilderXmlBundle_WithNoAudioLength_FallsBackToTheChartLength()
    {
        Directory.CreateDirectory(_tempDirectory);
        var projectPath = Path.Combine(_tempDirectory, "project.rscp");

        // EOF's no-audio-loaded branch — the caller simply has nothing to pass.
        CreateService().SaveDlcBuilderXmlBundle(projectPath, BuildProject());

        Assert.Equal("3.499", ExportedSongLength());
    }

    [Theory]
    [InlineData(2000.0, "1.999")]  // audio shorter -> clamp to audio
    [InlineData(10_000.0, "3.499")] // audio longer  -> chart length wins
    [InlineData(null, "3.499")]     // no audio      -> chart length
    public void SaveChart_ToSongXml_ThreadsTheAudioLengthIntoSongLength(double? audioLengthMs, string expected)
    {
        Directory.CreateDirectory(_tempDirectory);
        var path = Path.Combine(_tempDirectory, "arrangement.xml");

        CreateService().SaveChart(path, BuildProject(), audioLengthMs);

        Assert.Equal(expected, XDocument.Load(path).Root!.Element("songLength")!.Value);
    }

    // ── AssembleProject and the vocal tracks ─────────────────────────────
    //
    // AssembleProject used to Clear() VocalTracks and add ONE default-constructed
    // track built from the phrases it was handed. It runs on the LIVE project
    // from the save path, the crash snapshot AND the auto-backup timer, so a
    // harmony track or an imported track's name/colour was destroyed with no
    // user action at all.

    [Fact]
    public void AssembleProject_KeepsEveryVocalTrack_AndTheActiveTracksNameAndColour()
    {
        var service = CreateService();
        var project = BuildProject();
        project.VocalTracks[0].ColorR = 12;
        project.VocalTracks[0].ColorG = 34;
        project.VocalTracks[0].ColorB = 56;
        project.VocalTracks[0].ColorA = 78;
        var harmony = new VocalTrack { Name = "Harmony" };
        harmony.Phrases.Add(new LyricPhrase { Position = new FractionalPosition(4) });
        project.VocalTracks.Add(harmony);

        // What the editor binds: the ACTIVE (first) track's own collection.
        service.AssembleProject(project, 120, "song.ogg", project.VocalTracks[0].Phrases);

        Assert.Equal(2, project.VocalTracks.Count);
        Assert.Equal("Lead Vox", project.VocalTracks[0].Name);
        Assert.Equal((12, 34, 56, 78),
            (project.VocalTracks[0].ColorR, project.VocalTracks[0].ColorG,
                project.VocalTracks[0].ColorB, project.VocalTracks[0].ColorA));
        Assert.Equal("Harmony", project.VocalTracks[1].Name);
        Assert.Single(project.VocalTracks[1].Phrases);
    }

    /// <summary>
    ///     The editor can make any track active, so the phrases handed in are not
    ///     always the first track's — only that track may be touched.
    /// </summary>
    [Fact]
    public void AssembleProject_WithASecondTrackActive_LeavesTheFirstTrackAlone()
    {
        var service = CreateService();
        var project = BuildProject();
        var harmony = new VocalTrack { Name = "Harmony" };
        harmony.Phrases.Add(new LyricPhrase { Position = new FractionalPosition(4) });
        project.VocalTracks.Add(harmony);

        service.AssembleProject(project, 120, "song.ogg", harmony.Phrases);

        Assert.Equal(2, project.VocalTracks.Count);
        Assert.Equal("Lead Vox", project.VocalTracks[0].Name);
        Assert.Single(project.VocalTracks[0].Phrases);
        Assert.Same(harmony, project.VocalTracks[1]);
    }

    /// <summary>A chart with no vocals yet still has to get one on first use.</summary>
    [Fact]
    public void AssembleProject_WithNoVocalTracks_CreatesOneFromTheEditedPhrases()
    {
        var service = CreateService();
        var project = BuildProject();
        project.VocalTracks.Clear();
        var detached = new ObservableCollection<LyricPhrase> { new() { Position = FractionalPosition.Zero } };

        service.AssembleProject(project, 120, "song.ogg", detached);

        var track = Assert.Single(project.VocalTracks);
        Assert.Single(track.Phrases);
    }

    private string ExportedSongLength()
    {
        var arrangementPath = Path.Combine(_tempDirectory, "DTRS XML", "1_Lead_Main_RS2.xml");
        return XDocument.Load(arrangementPath).Root!.Element("songLength")!.Value;
    }

    private ProjectDocumentService CreateService() => new(_serviceProvider, new FakeAppLogger());

    private static ChartProject BuildProject()
    {
        var project = new ChartProject
        {
            Chart = new ChartData
            {
                SongName = "Test Song",
                Artist = "Test Artist",
                ArtistSort = "Test Artist",
                AlbumName = "Test Album",
                AlbumYear = "2026"
            },
            AudioFileName = "song.ogg",
            Bpm = 120
        };

        for (var i = 0; i < 8; i++)
        {
            project.Beats.Add(new BeatMarker
            {
                Time = i * 500,
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
            ArtistName = "Test Artist",
            ArtistNameSort = "Test Artist",
            AlbumName = "Test Album",
            AlbumYear = 2026,
            StringCount = 6
        };
        for (var i = 0; i < 6; i++)
            arrangement.TuningPitches.Add(new TuningOffset(0));
        arrangement.Levels.Add(new ChartLevel { DifficultyIndex = 0 });
        project.Arrangements.Add(arrangement);

        var vocalTrack = new VocalTrack { Name = "Lead Vox" };
        var phrase = new LyricPhrase
        {
            Position = FractionalPosition.Zero,
            EndPosition = new FractionalPosition(2)
        };
        phrase.Lyrics.Add(new LyricEvent
        {
            Position = FractionalPosition.Zero,
            EndPosition = new FractionalPosition(1),
            Text = "Hello",
            Note = 64
        });
        vocalTrack.Phrases.Add(phrase);
        project.VocalTracks.Add(vocalTrack);

        project.Showlights.Add(new Showlight
        {
            Position = FractionalPosition.Zero,
            Note = 24
        });

        return project;
    }
}
