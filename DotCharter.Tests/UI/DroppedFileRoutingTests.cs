using System.Text.RegularExpressions;
using System.Xml.Linq;
using DotCharter.Services.Import.Audio;
using DotCharter.UI.ViewModels.Shell;

namespace DotCharter.Tests.UI;

/// <summary>
///     The drop/open routing table (<see cref="DroppedFileRouter" />) plus the
///     source lint that <c>ProjectMenuViewModel.LoadFile</c> is the ONLY consumer
///     and never re-decides an extension itself.
///     <para>
///     Before the router, only <c>.eof</c> and the audio set were routed: a dropped
///     <c>.gp5</c>/<c>.mid</c>/<c>.psarc</c> reached the project loader, threw
///     inside <c>ResolveFormat</c> and surfaced as "Error loading project" — a
///     corrupt-file message for a file the app has an importer for.
///     </para>
/// </summary>
public class DroppedFileRoutingTests
{
    // ---- the table, one case per extension, both casings --------------------

    [Theory]
    // Projects
    [InlineData("song.rscp", DroppedFileRoute.Project)]
    [InlineData("project.dcproj", DroppedFileRoute.Project)]
    [InlineData("SONG.DCPROJ", DroppedFileRoute.Project)]
    [InlineData("SONG.RSCP", DroppedFileRoute.Project)]
    [InlineData("song.dcsong", DroppedFileRoute.Project)]
    [InlineData("SONG.DCSONG", DroppedFileRoute.Project)]
    // Editor on Fire
    [InlineData("song.eof", DroppedFileRoute.EofImport)]
    [InlineData("Song.EOF", DroppedFileRoute.EofImport)]
    // Guitar Pro — every extension the picker filter offers
    [InlineData("tab.gp", DroppedFileRoute.GuitarProImport)]
    [InlineData("tab.GP", DroppedFileRoute.GuitarProImport)]
    [InlineData("tab.gpx", DroppedFileRoute.GuitarProImport)]
    [InlineData("tab.GPX", DroppedFileRoute.GuitarProImport)]
    [InlineData("tab.gp7", DroppedFileRoute.GuitarProImport)]
    [InlineData("tab.GP7", DroppedFileRoute.GuitarProImport)]
    [InlineData("tab.gpa", DroppedFileRoute.GuitarProImport)]
    [InlineData("tab.GPA", DroppedFileRoute.GuitarProImport)]
    [InlineData("tab.gp5", DroppedFileRoute.GuitarProImport)]
    [InlineData("tab.GP5", DroppedFileRoute.GuitarProImport)]
    [InlineData("tab.gp4", DroppedFileRoute.GuitarProImport)]
    [InlineData("tab.GP4", DroppedFileRoute.GuitarProImport)]
    [InlineData("tab.gp3", DroppedFileRoute.GuitarProImport)]
    [InlineData("tab.GP3", DroppedFileRoute.GuitarProImport)]
    // MIDI
    [InlineData("song.mid", DroppedFileRoute.MidiImport)]
    [InlineData("song.MID", DroppedFileRoute.MidiImport)]
    [InlineData("song.midi", DroppedFileRoute.MidiImport)]
    [InlineData("song.MIDI", DroppedFileRoute.MidiImport)]
    // Rocksmith XML
    [InlineData("song_lead.xml", DroppedFileRoute.RocksmithXmlImport)]
    [InlineData("SONG_LEAD.XML", DroppedFileRoute.RocksmithXmlImport)]
    // Rocksmith package
    [InlineData("dlc_p.psarc", DroppedFileRoute.RocksmithPackage)]
    [InlineData("DLC_P.PSARC", DroppedFileRoute.RocksmithPackage)]
    // Audio
    [InlineData("song.wav", DroppedFileRoute.Audio)]
    [InlineData("song.WAV", DroppedFileRoute.Audio)]
    [InlineData("song.ogg", DroppedFileRoute.Audio)]
    [InlineData("song.mp3", DroppedFileRoute.Audio)]
    [InlineData("song.MP3", DroppedFileRoute.Audio)]
    [InlineData("song.flac", DroppedFileRoute.Audio)]
    [InlineData("song.m4a", DroppedFileRoute.Audio)]
    [InlineData("song.aac", DroppedFileRoute.Audio)]
    // Nothing reads these
    [InlineData("notes.txt", DroppedFileRoute.Unsupported)]
    [InlineData("NOTES.TXT", DroppedFileRoute.Unsupported)]
    [InlineData("cover.png", DroppedFileRoute.Unsupported)]
    [InlineData("archive.zip", DroppedFileRoute.Unsupported)]
    [InlineData("bundle.rs2dlc", DroppedFileRoute.Unsupported)]
    [InlineData("README", DroppedFileRoute.Unsupported)]
    public void Route_MapsEveryKnownExtension_CaseInsensitively(string fileName, DroppedFileRoute expected)
    {
        Assert.Equal(expected, DroppedFileRouter.Route(fileName, readsGamePackages: true));
        // …and the same answer with a full path in front of it.
        Assert.Equal(expected, DroppedFileRouter.Route(Path.Combine(@"C:\songs\My Band", fileName), readsGamePackages: true));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("noextension")]
    [InlineData(@"C:\songs\folder.name\file")]
    public void Route_IsUnsupported_ForAnythingWithoutAUsableExtension(string? path)
    {
        Assert.Equal(DroppedFileRoute.Unsupported, DroppedFileRouter.Route(path, readsGamePackages: true));
        Assert.Equal(string.Empty, DroppedFileRouter.ExtensionOf(path));
    }

    /// <summary>
    ///     <c>ProjectDocumentService</c> loads THROUGH the atomic-save temp suffix,
    ///     so the router must too — otherwise a leftover <c>project.rscp.saving</c>
    ///     (exactly the file a crash leaves behind) routes as "unsupported".
    /// </summary>
    [Fact]
    public void Route_LooksThroughTheAtomicSaveTempSuffix()
    {
        Assert.Equal(DroppedFileRoute.Project, DroppedFileRouter.Route("project.rscp.saving", readsGamePackages: true));
        Assert.Equal(DroppedFileRoute.Project, DroppedFileRouter.Route("project.dcproj.saving", readsGamePackages: true));
        Assert.Equal(".rscp", DroppedFileRouter.ExtensionOf("project.rscp.SAVING"));
    }

    /// <summary>
    ///     The audio list has ONE owner (<see cref="AudioFileTypes" />) — the router
    ///     may not carry a second copy that drifts, which is the exact bug that made
    ///     a dropped <c>.mp3</c> report "Error loading project".
    /// </summary>
    [Fact]
    public void EveryAudioExtension_RoutesToAudio()
    {
        foreach (var extension in AudioFileTypes.Extensions)
            Assert.Equal(DroppedFileRoute.Audio, DroppedFileRouter.Route("song" + extension, readsGamePackages: true));
    }

    /// <summary>Every extension the table advertises must actually have a route.</summary>
    [Fact]
    public void NoKnownExtension_FallsThroughToUnsupported()
    {
        foreach (var extension in DroppedFileRouter.KnownExtensions)
        {
            var route = DroppedFileRouter.Route("file" + extension, readsGamePackages: true);
            Assert.True(route != DroppedFileRoute.Unsupported,
                $"KnownExtensions advertises '{extension}' but Route() has no case for it.");
            Assert.Equal(extension, DroppedFileRouter.ExtensionOf("file" + extension));
        }
    }

    [Fact]
    public void KnownExtensions_HasNoDuplicates()
    {
        var duplicates = DroppedFileRouter.KnownExtensions
            .GroupBy(e => e, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();
        Assert.True(duplicates.Count == 0, $"Duplicate extensions in the table: {string.Join(", ", duplicates)}");
    }

    // ---- the lint: the funnel consults the table and decides nothing itself --

    /// <summary>
    ///     <c>ProjectMenuViewModel.LoadFile</c> must ASK the router. It used to test
    ///     <c>filePath.EndsWith(".eof")</c> inline, which is how every other
    ///     importable format stayed unrouted for releases: an inline test is
    ///     invisible to any table and nobody notices the missing arms.
    /// </summary>
    [Fact]
    public void LoadFileFunnel_ConsultsTheRouter_AndTestsNoExtensionItself()
    {
        var code = SourceText.StripComments(Read("DotCharter.UI", "ViewModels", "Shell", "ProjectMenuViewModel.cs"));

        Assert.Contains("DroppedFileRouter.Route(", code, StringComparison.Ordinal);

        var literals = Regex.Matches(code, "\"\\.[A-Za-z0-9]+\"")
            .Select(m => m.Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        Assert.True(literals.Count == 0,
            "ProjectMenuViewModel.cs decides a file's route by extension itself: " +
            string.Join(", ", literals) + ". Add the case to DroppedFileRouter instead.");

        Assert.DoesNotContain("EndsWith(\".", code, StringComparison.Ordinal);
        Assert.DoesNotContain("GetExtension(", code, StringComparison.Ordinal);
    }

    /// <summary>The window's file DROP must go through that one funnel, not open
    /// anything itself.</summary>
    [Fact]
    public void WindowDropHandler_GoesThroughTheLoadFileFunnel()
    {
        var code = SourceText.StripComments(Read("DotCharter.UI", "Views", "MainWindow.axaml.cs"));
        Assert.Contains("DragDrop.DropEvent", code, StringComparison.Ordinal);
        Assert.Contains("vm.Project.LoadFile(", code, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Every importer arm hands the dropped FILE to its dialog's path-taking
    ///     seam. The Guitar Pro arm used to open the dialog's PICKER at the file's
    ///     folder instead (the GP dialog had no seam), so a dropped tab still made
    ///     the user find and choose it again — a half-route that read as a bug.
    /// </summary>
    [Theory]
    [InlineData("OpenEofImport(filePath)")]
    [InlineData("OpenMidiImport(filePath)")]
    [InlineData("OpenGpImport(filePath)")]
    [InlineData("OpenRsXmlImport(filePath)")]
    public void EveryImporterArm_HandsTheDroppedFileToTheDialogsSeam(string seamCall)
    {
        var code = SourceText.StripComments(Read("DotCharter.UI", "ViewModels", "Shell", "ProjectMenuViewModel.cs"));
        Assert.Contains(seamCall, code, StringComparison.Ordinal);
        Assert.DoesNotContain("OpenCommand.Execute", code, StringComparison.Ordinal);
    }

    /// <summary>Both resx files carry every string the routing arms show.</summary>
    [Theory]
    [InlineData("Status_DroppedUnsupportedFileType")]
    [InlineData("Status_DroppedUnknownFileType")]
    [InlineData("Status_DroppedPsarcNotSupported")]
    public void EveryRoutingMessage_ExistsInBothResx(string key)
    {
        Assert.Contains(key, ReadResxKeys("Resources.resx"));
        Assert.Contains(key, ReadResxKeys("Resources.de-DE.resx"));
    }

    // ---- helpers ------------------------------------------------------------

    private static HashSet<string> ReadResxKeys(string fileName)
    {
        var path = Path.Combine(TestPaths.RepoRoot(), "DotCharter.Localization", fileName);
        Assert.True(File.Exists(path), $"resx not found at {path}");
        return XDocument.Load(path).Root!
            .Elements("data")
            .Select(d => d.Attribute("name")?.Value)
            .Where(n => n != null)
            .Select(n => n!)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static string Read(params string[] parts)
    {
        var path = Path.Combine(new[] { TestPaths.RepoRoot() }.Concat(parts).ToArray());
        Assert.True(File.Exists(path), $"Source file not found at {path}");
        return UiSourceTree.ReadAllText(path);
    }
}
