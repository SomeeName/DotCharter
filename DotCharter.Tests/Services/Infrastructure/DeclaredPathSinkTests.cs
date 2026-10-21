using System.IO.Compression;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using DotCharter.Services.Audio.StemSeparation;
using DotCharter.Services.Export;
using DotCharter.Services.Gameplay.Profiles;
using DotCharter.Services.Import.Eof;
using DotCharter.Services.Infrastructure.Storage;
using DotCharter.Services.Parsing.Eof;
using DotCharter.Services.Parsing.Rscp;
using DotCharter.Services.Parsing.Xml.Song;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Infrastructure;

/// <summary>
///     Every place a path from a shared file reaches the disk goes through
///     <see cref="ProjectRelativePath" /> (#107). A remote path is never put in front
///     of the real file system here: the sinks that take an
///     <see cref="IExportTargetWriter" /> get a recording fake, and the ones that probe
///     the disk directly are fed a <c>\\?\</c> device path to a LOCAL temp file, which
///     the real <see cref="File.Exists(string)" /> would happily confirm.
/// </summary>
public sealed class DeclaredPathSinkTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("dotcharter_declared_").FullName;

    public void Dispose() => BestEffortDelete.Directory(_root);

    private string Touch(string relativePath)
    {
        var full = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, "");
        return full;
    }

    private string ProjectIn(string folder)
    {
        var dir = Path.Combine(_root, folder);
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "project.rscp");
    }

    // ---- rscp musicFileName (ProjectAudioLocator) ----

    [Theory]
    [InlineData(@"\\?\")]
    [InlineData(@"\\.\")]
    public void Audio_ADevicePathIsNeverProbed(string prefix)
    {
        var elsewhere = Touch(@"elsewhere\track.ogg");
        var project = ProjectIn("proj");

        Assert.Null(ProjectAudioLocator.TryResolve(project, prefix + elsewhere));
    }

    [Fact]
    public void Audio_ARefusedPathStillFallsBackToItsBareNameBesideTheProject()
    {
        var project = ProjectIn("proj");
        var beside = Touch(@"proj\track.ogg");

        Assert.Equal(beside, ProjectAudioLocator.TryResolve(project, @"\\?\" + Path.Combine(_root, "gone", "track.ogg")));
    }

    [Fact]
    public void Audio_TheMissingDefaultStaysUnderTheProjectFolder()
    {
        var project = ProjectIn("proj");

        var fallback = ProjectAudioLocator.ResolveOrDefault(project, @"\\?\" + Path.Combine(_root, "gone", "track.ogg"));

        Assert.Equal(Path.Combine(_root, "proj", "track.ogg"), fallback);
    }

    /// <summary>The walk up the parents is for a BARE name; a declared sub-path is not hunted for elsewhere.</summary>
    [Fact]
    public void Audio_ADeclaredSubPathIsNotSearchedForInTheParentFolders()
    {
        Touch(@"private\track.ogg");
        var project = ProjectIn(@"song\lead");

        Assert.Null(ProjectAudioLocator.TryResolve(project, @"private\track.ogg"));
    }

    /// <summary>
    ///     The owner's own case: Save As out of the audio's folder writes an absolute
    ///     LOCAL path, and it keeps playing. Its outputs (stems, padded audio) go into
    ///     the PROJECT folder, never into the folder the file named.
    /// </summary>
    [Fact]
    public void Audio_ALocalAbsolutePathIsReadButNeverWrittenBeside()
    {
        var audio = Touch(@"elsewhere\track.ogg");
        var project = ProjectIn("proj");

        Assert.Equal(audio, ProjectAudioLocator.TryResolve(project, audio));
        Assert.Equal(Path.Combine(_root, "proj"), ProjectAudioLocator.ResolveOutputDirectory(project, audio));
    }

    [Fact]
    public void Audio_AnUnsavedSessionStillWritesBesideItsOwnAudio()
    {
        var audio = Touch(@"cache\track.ogg");

        Assert.Equal(Path.Combine(_root, "cache"), ProjectAudioLocator.ResolveOutputDirectory(null, audio));
    }

    // ---- rscp stems ----

    [Fact]
    public void Stem_ADevicePathIsNeverProbed()
    {
        var elsewhere = Touch(@"elsewhere\guitar.wav");
        var projectDir = Path.Combine(_root, "proj");

        Assert.Equal("", StemFolderProbe.ResolveStemPath(projectDir, projectDir, @"\\?\" + elsewhere));
    }

    [Fact]
    public void Stem_TheParentRelativePathTheAppWritesStillResolves()
    {
        var stem = Touch(@"song\stems\guitar.wav");
        var projectDir = Path.Combine(_root, "song", "lead");

        Assert.Equal(Path.GetFullPath(stem),
            Path.GetFullPath(StemFolderProbe.ResolveStemPath(projectDir, "", @"..\stems\guitar.wav")));
    }

    // ---- rscp arrangementFiles (pre-v3 recovery) ----

    [Fact]
    public void ArrangementFile_ARootedNameIsNotReadIntoTheProject()
    {
        var smuggled = Path.Combine(_root, "elsewhere", "lead.xml");
        Directory.CreateDirectory(Path.GetDirectoryName(smuggled)!);
        new RsXmlSongWriter().WriteTo(ProjectWithANote(), smuggled);

        var project = new RscpChartSerializer().Load(LegacyProject("proj", smuggled));

        Assert.DoesNotContain(project.Arrangements.SelectMany(a => a.Levels).SelectMany(l => l.Notes), _ => true);
    }

    [Fact]
    public void ArrangementFile_ASubfolderOfTheProjectIsStillRecovered()
    {
        Directory.CreateDirectory(Path.Combine(_root, "proj", "xml"));
        new RsXmlSongWriter().WriteTo(ProjectWithANote(), Path.Combine(_root, "proj", "xml", "lead.xml"));

        var project = new RscpChartSerializer().Load(LegacyProject("proj", @"xml\lead.xml"));

        Assert.Contains(project.Arrangements.SelectMany(a => a.Levels).SelectMany(l => l.Notes), _ => true);
    }

    // ---- EOF companion audio ----

    [Theory]
    [InlineData(@"\\server\share\guitar.ogg")]
    [InlineData(@"..\guitar.ogg")]
    [InlineData(@"C:\elsewhere\guitar.ogg")]
    public void EofCompanion_ADeclaredNameOutsideTheFolderIsNeverProbed(string declared)
    {
        var files = new RecordingFiles();
        var eofPath = Path.Combine(_root, "eof", "notes.eof");

        var found = EofProjectCreator.FindCompanionAudio(files, eofPath, new EofImportMetadata { AudioFileName = declared });

        Assert.Equal("", found);
        Assert.NotEmpty(files.Probed); // the defaults were still looked for…
        Assert.All(files.Probed, p => Assert.StartsWith(Path.Combine(_root, "eof") + Path.DirectorySeparatorChar, Path.GetFullPath(p)));
    }

    [Fact]
    public void EofCompanion_ThePlainRecordedNameStillWins()
    {
        var eofDir = Path.Combine(_root, "eof");
        var files = new RecordingFiles(Path.Combine(eofDir, "mix.ogg"));

        var found = EofProjectCreator.FindCompanionAudio(files, Path.Combine(eofDir, "notes.eof"),
            new EofImportMetadata { AudioFileName = "mix.ogg" });

        Assert.Equal(Path.Combine(eofDir, "mix.ogg"), found);
    }

    // ---- cover art + export audio defaults ----

    [Theory]
    [InlineData(@"\\server\share\cover.png")]
    [InlineData(@"C:\elsewhere\cover.png")]
    [InlineData(@"..\cover.png")]
    public void ExportCover_ADeclaredPathOutsideTheProjectIsNeverProbed(string declared)
    {
        var files = new RecordingFiles();
        var project = new ChartProject { Chart = { CoverArtPath = declared } };

        Assert.Equal("", ExportPathDefaults.CoverArt(project, Path.Combine(_root, "proj", "project.rscp"), files));
        Assert.Empty(files.Probed);
    }

    [Fact]
    public void ExportAudio_AUncPathIsNeverProbed()
    {
        var files = new RecordingFiles();

        Assert.Null(ExportPathDefaults.ProjectAudio(@"\\server\share\song.wav", Path.Combine(_root, "proj", "project.rscp"), files));
        Assert.Empty(files.Probed);
    }

    // ---- profile import avatar ----

    /// <summary>
    ///     The imported avatar name is later READ and, on the next avatar change,
    ///     DELETED. One that climbs out of the profile's data folder is dropped on import.
    /// </summary>
    [Fact]
    public void ProfileImport_AnAvatarOutsideTheDataFolderIsDropped()
    {
        var storePath = Path.Combine(_root, "profiles.json");
        var service = new PlayerProfileService(FakeLocalization.English(), logger: null, filePath: storePath, defaultProfileName: "P");
        var original = service.Create("Tour");
        var zipPath = Path.Combine(_root, "tour.dcprofile");
        Assert.True(service.ExportProfile(original.Id, zipPath));

        // <root>/profiles/<id>/../../keep.txt is <root>/keep.txt, which exists.
        Touch("keep.txt");
        RewriteProfileJson(zipPath, json => json["AvatarFileName"] = @"..\..\keep.txt");

        var imported = service.ImportProfile(zipPath);

        Assert.NotNull(imported);
        Assert.Equal("", imported.AvatarFileName);
        Assert.Null(service.GetAvatarPath(imported.Id));
        Assert.True(File.Exists(Path.Combine(_root, "keep.txt")));
    }

    private static void RewriteProfileJson(string zipPath, Action<JsonObject> edit)
    {
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Update);
        var entry = zip.GetEntry("profile.json")!;
        JsonObject json;
        using (var reader = new StreamReader(entry.Open())) json = JsonNode.Parse(reader.ReadToEnd())!.AsObject();
        edit(json);
        entry.Delete();
        using var writer = new StreamWriter(zip.CreateEntry("profile.json").Open());
        writer.Write(json.ToJsonString());
    }

    private string LegacyProject(string folder, string arrangementFile)
    {
        var path = ProjectIn(folder);
        new RscpChartSerializer().Save(path, ProjectWithoutNotes());

        // The v2 layout: arrangements stripped out, a sibling file named instead.
        var doc = XDocument.Load(path);
        doc.Descendants("arrangements").Remove();
        var files = doc.Descendants("arrangementFiles").FirstOrDefault();
        if (files is null)
        {
            files = new XElement("arrangementFiles");
            doc.Root!.Add(files);
        }

        files.RemoveNodes();
        files.Add(new XElement("string", arrangementFile));
        doc.Save(path);
        return path;
    }

    private static ChartProject ProjectWithANote()
    {
        var project = ProjectWithoutNotes();
        project.Arrangements[0].Levels[0].Notes.Add(new GuitarNote { Position = new FractionalPosition(2), StringIndex = 1, Fret = 5 });
        return project;
    }

    private static ChartProject ProjectWithoutNotes()
    {
        var project = new ChartProject { Bpm = 120, Chart = { SongName = "Old Song", Artist = "Legacy" } };
        for (var i = 0; i < 16; i++)
        {
            project.Beats.Add(new BeatMarker
            {
                Time = i * 500.0, IsMeasure = i % 4 == 0, Bpm = 120, BeatsInMeasure = 4, NoteDenominator = 4
            });
        }

        var arrangement = new Arrangement { Name = "Lead", ArrangementType = ArrangementType.Lead };
        for (var i = 0; i < 6; i++) arrangement.TuningPitches.Add(new TuningOffset(0));
        arrangement.Levels.Add(new ChartLevel { DifficultyIndex = 0 });
        project.Arrangements.Add(arrangement);
        return project;
    }

    /// <summary>Records every existence probe; answers yes only for the listed paths.</summary>
    private sealed class RecordingFiles(params string[] existing) : IExportTargetWriter
    {
        public List<string> Probed { get; } = [];

        public bool FileExists(string path)
        {
            Probed.Add(path);
            return existing.Contains(path, StringComparer.OrdinalIgnoreCase);
        }

        public bool DirectoryExists(string path)
        {
            Probed.Add(path);
            return false;
        }

        public void CopyFile(string source, string destination) => throw new NotSupportedException();
        public void WriteAllBytes(string path, byte[] bytes) => throw new NotSupportedException();
        public byte[] ReadAllBytes(string path) => throw new NotSupportedException();
        public Task<string> ReadAllTextAsync(string path) => throw new NotSupportedException();
        public Task WriteAllTextAsync(string path, string text) => throw new NotSupportedException();
        public Stream Create(string path) => throw new NotSupportedException();
        public void DeleteIfExists(string path) => throw new NotSupportedException();
        public string[] FindFiles(string folder, string pattern) => throw new NotSupportedException();
        public string DocumentsFolder => throw new NotSupportedException();
    }
}
