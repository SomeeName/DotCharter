using System.Security.Cryptography;
using DotCharter.Tone.Audio.Tonelab;

namespace DotCharter.Tests.Audio.Tonelab;

public sealed class TonelabGameMockTests
{
    [Fact]
    public void AReservedIdentity_IsNotReused_AndTonePathsArePerInstance()
    {
        using var first = new TonelabGameMock();
        var attempts = 0;
        using var second = new TonelabGameMock(chooseId: () => ++attempts == 1
            ? first.ProcessId
            : RandomNumberGenerator.GetInt32(1_900_000_000, int.MaxValue));

        Assert.NotEqual(first.ProcessId, second.ProcessId);
        Assert.NotEqual(first.PipeName, second.PipeName);
        Assert.NotEqual(first.TonePath, second.TonePath);
        Assert.True(attempts >= 2);
    }

    [Fact]
    public async Task AnApplyReply_ReleasesTheSourceFile_BeforeTheCallerCanDeleteIt()
    {
        using var game = new TonelabGameMock();
        using var client = new TonelabPipeClient(game.PipeName, TonelabSessionTests.Quick, TonelabSessionTests.ReplyClock);
        await File.WriteAllTextAsync(game.TonePath, """{"GearList":{},"Key":"t"}""", TestContext.Current.CancellationToken);

        Assert.True((await client.ApplyFileAsync(game.TonePath, TestContext.Current.CancellationToken)).IsOk);
        using (var file = File.Open(game.TonePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.True(file.Length > 0);
        File.Delete(game.TonePath);
        Assert.False(File.Exists(game.TonePath));
    }
}
