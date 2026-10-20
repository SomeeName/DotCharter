using DotCharter.Services.Parsing;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Parsing;

/// <summary>
///     The read half of the atomic-save pairing, for the XML documents this app
///     owns — above all the <c>.rscp</c> project itself.
///     <para>
///         <c>AtomicFile</c> writes by staging + RENAME, and on Windows a rename
///         is refused by any open handle on the destination that does not share
///         DELETE. <c>SafeXml.LoadFromFile</c> used a plain
///         <c>File.OpenRead</c>, which denies it — so a load overlapping a save
///         of the SAME file made the writer burn its whole bounded retry budget
///         and, past it, drop the write entirely. That overlap is not
///         hypothetical: the crash-recovery timer writes the snapshot the
///         recovery dialog is opening, and the auto-backup reads a project the
///         user may be saving.
///     </para>
/// </summary>
public sealed class SafeXmlShareModeTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dc-safexml-share-").FullName;

    public void Dispose() => BestEffortDelete.Directory(_dir);

    [Fact]
    public void AnOpenXmlReader_DoesNotBlockAnAtomicSaveOfTheSameFile()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows rename semantics.");

        var path = Path.Combine(_dir, "song.rscp");
        File.WriteAllText(path, "<project><title>OLD</title></project>");
        var staged = path + ".saving";
        File.WriteAllText(staged, "<project><title>NEW</title></project>");

        // The exact handle SafeXml.LoadFromFile holds while it parses.
        using var reading = SafeXml.OpenRead(path);

        // The save must go through anyway — with a delete-denying handle this
        // exhausts the retry budget and throws.
        AtomicFile.ReplaceWithRetry(staged, path);

        using (var reader = new StreamReader(reading))
            Assert.Contains("OLD", reader.ReadToEnd()); // the held handle: the whole old document
        Assert.Contains("NEW", AtomicFile.ReadAllText(path)); // the name: the new one
    }

    [Fact]
    public void LoadFromFile_StillReadsTheDocument()
    {
        var path = Path.Combine(_dir, "plain.xml");
        File.WriteAllText(path, "<project><title>Hello</title></project>");

        var doc = SafeXml.LoadFromFile(path);

        Assert.Equal("Hello", doc.Root?.Element("title")?.Value);
    }
}
