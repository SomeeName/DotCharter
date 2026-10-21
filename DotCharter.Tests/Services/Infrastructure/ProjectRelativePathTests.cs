using DotCharter.Services.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Infrastructure;

/// <summary>
///     The validator for paths that come out of a shared file (#107). Pure string
///     work: every case uses a synthetic base folder and nothing here touches the
///     disk — which is the property under test, since a probe of a UNC path is
///     itself the credential leak.
/// </summary>
public sealed class ProjectRelativePathTests
{
    private const string Base = @"C:\proj";

    [Theory]
    [InlineData(@"C:\other\x.ogg")] // rooted
    [InlineData(@"\\server\share\x.ogg")] // UNC
    [InlineData("//server/share/x.ogg")] // UNC, forward slashes
    [InlineData(@"\\?\C:\x.ogg")] // device, long-path prefix
    [InlineData(@"\\.\C:\x.ogg")] // device namespace
    [InlineData(@"C:x.ogg")] // drive-relative
    [InlineData(@"\x.ogg")] // root-relative
    [InlineData("/x.ogg")] // root-relative, forward slash
    [InlineData(@"..\x.ogg")] // escapes
    [InlineData("a/../../b.ogg")] // escapes through a subfolder
    [InlineData(@"sub\..\x.ogg")] // any .. segment, even one that stays inside
    [InlineData("CON")]
    [InlineData("nul.txt")]
    [InlineData(@"sub\aux.png")]
    [InlineData("COM1.ogg")]
    [InlineData("LPT9")]
    [InlineData("x.ogg:stream")] // alternate data stream
    [InlineData("x.ogg.")] // Windows strips the trailing dot
    [InlineData("x.ogg ")] // …and the trailing space
    [InlineData("*.ogg")]
    [InlineData("")]
    [InlineData("   ")]
    public void TryResolve_RefusesEverythingButAPlainProjectRelativePath(string declared)
    {
        Assert.Null(ProjectRelativePath.TryResolve(Base, declared));
        Assert.False(ProjectRelativePath.IsSafeRelative(declared));
    }

    [Theory]
    [InlineData("song.ogg", @"C:\proj\song.ogg")]
    [InlineData(@"sub\cover.png", @"C:\proj\sub\cover.png")]
    [InlineData("sub/cover.png", @"C:\proj\sub\cover.png")]
    [InlineData(@".\song.ogg", @"C:\proj\song.ogg")]
    [InlineData("console.ogg", @"C:\proj\console.ogg")] // a reserved PREFIX is just a name
    public void TryResolve_AcceptsAPlainNameOrSubfolder(string declared, string expected)
        => Assert.Equal(expected, ProjectRelativePath.TryResolve(Base, declared));

    [Fact]
    public void TryResolve_NeedsABaseDirectory()
        => Assert.Null(ProjectRelativePath.TryResolve(null, "song.ogg"));

    [Theory]
    [InlineData(@"\\server\share\x.ogg")]
    [InlineData("//server/share/x.ogg")]
    [InlineData(@"\\?\C:\x.ogg")]
    [InlineData(@"\\.\C:\x.ogg")]
    [InlineData(@"C:x.ogg")]
    [InlineData(@"\x.ogg")]
    [InlineData("CON")]
    [InlineData(@"stems\x.wav:stream")]
    [InlineData("")]
    public void MediaReference_RefusesRemoteDeviceAndDriveRelativePaths(string declared)
        => Assert.False(ProjectRelativePath.IsReadableMediaReference(declared));

    /// <summary>
    ///     What the app itself writes: a bare name, a <c>..</c> stem path beside a
    ///     parent-folder audio, and an absolute LOCAL path (Save As out of the audio's
    ///     folder, audio attached to an unsaved session). C: is the system drive, so it
    ///     is never a network drive.
    /// </summary>
    [Theory]
    [InlineData("song.ogg")]
    [InlineData(@"..\stems\guitar.wav")]
    [InlineData(@"C:\Music\song.ogg")]
    [InlineData("C:/Music/song.ogg")]
    public void MediaReference_AcceptsWhatTheAppWrites(string declared)
        => Assert.True(ProjectRelativePath.IsReadableMediaReference(declared));
}
