using System.Text;
using DotCharter.Services.Update;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Update;

public sealed class GitHubTokenStoreTests : IDisposable
{
    private readonly string _dir;

    public GitHubTokenStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "dc-token-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() => BestEffortDelete.Directory(_dir);

    [Fact]
    public void SaveLoad_RoundTripsTheToken()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "DPAPI token storage is Windows-only.");

        var store = new GitHubTokenStore(_dir);
        store.Save("ghp_super_secret_token_value");

        Assert.True(store.HasToken);
        Assert.Equal("ghp_super_secret_token_value", store.Load());
    }

    [Fact]
    public void Save_NeverWritesThePlaintextTokenToDisk()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "DPAPI token storage is Windows-only.");

        const string token = "ghp_super_secret_token_value";
        var store = new GitHubTokenStore(_dir);
        store.Save(token);

        var raw = File.ReadAllBytes(Path.Combine(_dir, GitHubTokenStore.FileName));
        Assert.False(ContainsSubsequence(raw, Encoding.UTF8.GetBytes(token)));
        Assert.False(ContainsSubsequence(raw, Encoding.Unicode.GetBytes(token)));
    }

    [Fact]
    public void Delete_RemovesTheToken()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "DPAPI token storage is Windows-only.");

        var store = new GitHubTokenStore(_dir);
        store.Save("ghp_temporary");
        store.Delete();

        Assert.False(store.HasToken);
        Assert.Null(store.Load());
    }

    [Fact]
    public void Load_MissingOrCorruptedFile_ReturnsNull()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "DPAPI token storage is Windows-only.");

        var store = new GitHubTokenStore(_dir);
        Assert.Null(store.Load());

        File.WriteAllBytes(Path.Combine(_dir, GitHubTokenStore.FileName), [1, 2, 3, 4]);
        Assert.Null(store.Load());
    }

    [Fact]
    public void Save_EmptyToken_Throws()
    {
        var store = new GitHubTokenStore(_dir);
        Assert.Throws<ArgumentException>(() => store.Save("   "));
    }

    private static bool ContainsSubsequence(byte[] haystack, byte[] needle)
    {
        for (var i = 0; i + needle.Length <= haystack.Length; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    match = false;
                    break;
                }
            }

            if (match) return true;
        }

        return false;
    }
}
