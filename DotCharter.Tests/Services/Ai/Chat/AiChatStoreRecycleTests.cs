using DotCharter.Services.Ai.Chat;

namespace DotCharter.Tests.Services.Ai.Chat;

public sealed class AiChatStoreRecycleTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("chat_recycle_refusal_").FullName;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Refused_recycling_returns_a_failure_and_preserves_the_conversation(bool unsupported)
    {
        var chart = new AiChatChart(Guid.NewGuid().ToString("N"), _root);
        Assert.Equal(AiChatStatus.Ok, AiChatStore.TryOpen(chart, out var store));
        var conversation = AiChatConversation.Start(chart.ChartId, "model", DateTimeOffset.UnixEpoch);
        Assert.Equal(AiChatStatus.Ok, store!.Write(conversation).Status);
        var path = Path.Combine(store.FolderPath, conversation.Id + ".json");
        var before = File.ReadAllBytes(path);

        var result = store.Delete(conversation.Id, file =>
        {
            Assert.Equal(path, file);
            if (unsupported) throw new PlatformNotSupportedException("Recycle Bin unavailable");
        });

        Assert.Equal(AiChatStatus.WriteFailed, result.Status);
        if (unsupported) Assert.Equal("Recycle Bin unavailable", result.Detail);
        else Assert.Null(result.Detail);
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Equal(AiChatStatus.Ok, store.Read(conversation.Id, out var read, out _));
        Assert.Equal(conversation.Id, read!.Id);
        Assert.Equal(conversation.ChartId, read.ChartId);
    }

    public void Dispose() => Directory.Delete(_root, true);
}
