using DotCharter.Services.Audio.StemSeparation;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Infrastructure;

/// <summary>
///     One shape, four JSON-file stores: a read that FAILED is not the same answer
///     as a store that is EMPTY.
///     <para>
///         Each of these loaded with a bare <c>catch (Exception)</c> that handed the
///         caller an empty store and let the very next save write it back. Every one
///         of those callers is read-modify-write — add a manual practice entry, set a
///         goal, record a LAN score, register a custom stem model — so a file the OS
///         merely refused to open for a moment (an antivirus scan, a cloud sync, a
///         backup tool) was replaced by whatever this session happened to add.
///     </para>
///     <para>
///         <b>Why the hold shares Delete.</b> The file is opened
///         <see cref="FileAccess.Read" /> and shared <c>Write | Delete</c>: a READER is
///         denied (the load throws <see cref="IOException" />) while a RENAME over the
///         file is still permitted — which is exactly what <c>AtomicFile</c> does. So
///         the writer really could have clobbered it, and the assertion on the
///         unchanged bytes is not passing because Windows happened to stop the write.
///     </para>
/// </summary>
public sealed class DegradedStoreReadTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "DotCharterTests", Guid.NewGuid().ToString("N"));

    public DegradedStoreReadTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => BestEffortDelete.Directory(_dir);

    /// <summary>Runs <paramref name="mutate"/> while the store file cannot be read
    /// but CAN be replaced, and asserts the bytes on disk are untouched.</summary>
    private static void WhileUnreadable(string path, Action mutate)
    {
        Assert.True(File.Exists(path), $"the test must seed '{path}' before locking it");
        var before = File.ReadAllBytes(path);
        Assert.NotEmpty(before);

        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Write | FileShare.Delete))
        {
            mutate();
        }

        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void CustomStemModelStore_Unreadable_IsNotOverwritten_AndHeals()
    {
        var store = new CustomStemModelStore(_dir);
        store.Save([
            new StemModelDescriptor
            {
                Id = "mine", DisplayName = "Mine", Description = "",
                DownloadUrl = "https://example.com/m.onnx", FileName = "mine.onnx",
                Kind = StemModelKind.HtdemucsOnnx
            }
        ]);

        var path = Path.Combine(_dir, "custom_models.json");
        WhileUnreadable(path, () => store.Save(store.Load()));

        Assert.Equal(["mine"], store.Load().Select(m => m.Id));
    }
}
