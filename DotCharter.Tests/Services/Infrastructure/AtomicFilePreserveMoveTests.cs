using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Infrastructure;

public sealed class AtomicFilePreserveMoveTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("preserve-move-").FullName;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Nonreplacing_moves_keep_original_bytes_when_another_file_claims_the_selected_name(bool collision)
    {
        var original = Path.Combine(_root, "original.rscp.bak");
        var target = Path.Combine(_root, "backup.rscp.bak");
        File.WriteAllText(original, "only original bytes");
        if (collision) File.WriteAllText(target, "other publisher bytes");
        if (collision)
        {
            Assert.Throws<IOException>(() => AtomicFile.MoveAside(original, target, overwrite: false));
            Assert.Equal("only original bytes", File.ReadAllText(original));
            Assert.Equal("other publisher bytes", File.ReadAllText(target));
        }
        else
        {
            AtomicFile.MoveAside(original, target, overwrite: false);
            Assert.False(File.Exists(original));
            Assert.Equal("only original bytes", File.ReadAllText(target));
        }
    }

    public void Dispose() => Directory.Delete(_root, true);
}
