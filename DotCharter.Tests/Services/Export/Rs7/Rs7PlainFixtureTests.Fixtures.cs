using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Export.Rs7;

/// <summary>
///     #736, the always-on half: a small PLAIN 7-string package checked into the repo
///     (<c>Fixtures/Rs7/rs7fixture7_p.psarc</c> + its <c>chords-ext.json</c>, built once by
///     <c>Write_the_fixture</c> from <see cref="Rs7PackageExportTests.CreateProject" />
///     with 4 KB of placeholder audio). Reading a file this build did NOT write keeps the
///     reader honest if the writer and reader ever drift together; CI has it too. The
///     fixture predates the RS7X block (#777), so it is also the always-on case of the
///     SIDECAR import path that older packages still take.
///     <para>
///         The opt-in cases write what the owner tries in the game:
///         <c>DOTCHARTER_RS7_OUT=&lt;dir&gt;</c> re-exports the owner's verified
///         <c>rs7test_7str_p.psarc</c> and rs7chart's chord tests (<c>rs7c_m4_*_p.psarc</c>)
///         through DotCharter's importer and exporter (their own audio, new DLC keys);
///         <c>DOTCHARTER_RS7_FIXTURE_OUT=&lt;dir&gt;</c> rebuilds the fixture (a rebuild
///         writes the block and no sidecar).
///     </para>
/// </summary>
public sealed partial class Rs7PlainFixtureTests : IDisposable
{
    private static readonly string FixturePath =
        Path.Combine(AppContext.BaseDirectory, "Services", "Export", "Fixtures", "Rs7", "rs7fixture7_p.psarc");

    private readonly string _tempDir = Directory.CreateTempSubdirectory("dotcharter-rs7-fixture-").FullName;

    public void Dispose() => BestEffortDelete.Directory(_tempDir);
}
