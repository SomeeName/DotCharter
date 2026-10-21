using DotCharter.Services.Infrastructure.Recovery;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Recovery;

/// <summary>
///     A recovery sidecar names the file "Save" writes the recovered work back to,
///     and it sits in a folder a restored backup can write to. The path is
///     adopted only when it is a plain local <c>.rscp</c> outside the system folders;
///     otherwise the session is recovered as untitled and the user chooses.
/// </summary>
public sealed class RecoveredProjectPathTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dc_recpath_" + Guid.NewGuid().ToString("N"));

    public void Dispose() => BestEffortDelete.Directory(_root);

    [Theory]
    [InlineData(@"C:\Users\someone\Documents\Charts\song.rscp")]
    [InlineData(@"D:\Music\Charts\song.RSCP")]
    [InlineData(@"C:\Users\someone\Documents\Charts\my song (v2).rscp")]
    public void Adopts_APlainLocalRscp(string path) => Assert.True(RecoveredProjectPath.IsAdoptable(path), path);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"C:\Users\someone\Documents\Charts\song.exe")]       // not a project
    [InlineData(@"C:\Users\someone\Documents\thesis.docx")]
    [InlineData(@"C:\Users\someone\Documents\Charts\song")]
    [InlineData(@"C:\Users\someone\Documents\Charts\song.rscp.exe")]
    [InlineData(@"\\evil-host\share\song.rscp")]                       // UNC
    [InlineData("//evil-host/share/song.rscp")]
    [InlineData(@"\\?\C:\Users\someone\song.rscp")]                    // device
    [InlineData(@"relative\song.rscp")]                                // not rooted
    [InlineData(@"\song.rscp")]
    [InlineData(@"C:song.rscp")]
    [InlineData(@"C:\Users\someone\..\..\Windows\song.rscp")]          // dotted
    [InlineData(@"C:\Users\someone\song.rscp:evil.exe")]               // alternate data stream
    [InlineData(@"C:\Windows\System32\drivers\etc\song.rscp")]         // OS folder
    [InlineData(@"C:\Program Files\Some App\song.rscp")]               // installed programs
    [InlineData(@"C:\Program Files (x86)\Some App\song.rscp")]
    public void Refuses_Everything_Else(string path) => Assert.False(RecoveredProjectPath.IsAdoptable(path), path);

    [Fact]
    public void ListSessions_TakesAGoodOriginalPath_AndShowsItToTheDialog()
    {
        var store = NewStore();
        var original = Path.Combine(_root, "originals", "song.rscp");
        store.WriteSnapshot(Project(), Metadata(original));

        var session = Assert.Single(store.ListSessions());

        Assert.Equal(original, session.Metadata.OriginalProjectPath);
        Assert.False(session.IsUntitled);
    }

    [Theory]
    [InlineData(@"\\evil-host\share\thesis.rscp")]
    [InlineData(@"C:\Windows\System32\config.rscp")]
    [InlineData(@"C:\Users\someone\Documents\thesis.docx")]
    [InlineData(@"..\..\startup.rscp")]
    public void ListSessions_RecoversTheWorkAsUntitled_WhenTheSidecarNamesABadPath(string badPath)
    {
        var store = NewStore();
        store.WriteSnapshot(Project(), Metadata(badPath));

        var session = Assert.Single(store.ListSessions());

        Assert.True(session.IsUntitled);
        Assert.Equal(string.Empty, session.Metadata.OriginalProjectPath);
        Assert.True(store.IsRecoverable(session)); // the work is still offered
    }

    private RecoverySnapshotStore NewStore() => new(Path.Combine(_root, "recovery"));

    private static RecoverySnapshotMetadata Metadata(string originalPath) => new()
    {
        OriginalProjectPath = originalPath,
        OriginalAudioFileName = "song.ogg",
        SongName = "Recovery Song",
        TimestampUtc = DateTimeOffset.UtcNow,
        AppVersion = "test"
    };

    private static ChartProject Project()
    {
        var arrangement = new Arrangement { Name = "Lead Guitar" };
        for (var i = 0; i < 6; i++) arrangement.TuningPitches.Add(new TuningOffset(0));
        arrangement.Levels.Add(new ChartLevel { DifficultyIndex = 0 });
        var project = new ChartProject
        {
            Bpm = 120,
            AudioFileName = "song.ogg",
            Chart = new ChartData { SongName = "Recovery Song", Artist = "Test", BeatsPerMinute = 120 }
        };
        project.Arrangements.Add(arrangement);
        for (var i = 0; i < 8; i++)
        {
            project.Beats.Add(new BeatMarker
            {
                Time = i * 500.0, Bpm = 120, IsMeasure = i % 4 == 0, BeatsInMeasure = 4, NoteDenominator = 4
            });
        }

        return project;
    }
}
