using DotCharter.Services.Audio.LivePlay;

namespace DotCharter.Tests.Services.Audio.LivePlay;

public sealed class PracticeHistoryTrimTests
{
    private const int Cap = 200;

    [Theory]
    [InlineData(0, false, false, 0)]
    [InlineData(0, false, true, 3)]
    [InlineData(0, true, false, 0)]
    [InlineData(0, true, true, 3)]
    [InlineData(1, false, false, 0)]
    [InlineData(1, false, true, 4)]
    [InlineData(1, true, false, 0)]
    [InlineData(1, true, true, 4)]
    [InlineData(199, false, false, 0)]
    [InlineData(199, false, true, 202)]
    [InlineData(199, true, false, 0)]
    [InlineData(199, true, true, 202)]
    [InlineData(200, false, false, 0)]
    [InlineData(200, false, true, 202)]
    [InlineData(200, true, false, 0)]
    [InlineData(200, true, true, 202)]
    [InlineData(205, false, false, 0)]
    [InlineData(205, false, true, 202)]
    [InlineData(205, true, false, 0)]
    [InlineData(205, true, true, 202)]
    public void DecisionTable_KeepsTheNewRunAndProtectedHistory_AndTrimsOldOrdinaryRuns(
        int protectedCount, bool newStarred, bool hasOrdinaryOlder, int expectedRemovals)
    {
        var newId = Guid.NewGuid();
        var ordinary = Enumerable.Range(0, hasOrdinaryOlder ? Cap + 2 : 0)
            .Select(_ => Guid.NewGuid()).ToArray();
        var sessions = Enumerable.Range(0, protectedCount)
            .Select(_ => (Id: Guid.NewGuid(), Protected: true))
            .Concat(ordinary.Select(id => (Id: id, Protected: false))).ToList();
        // Newest by recording identity can sit earlier after a wall-clock adjustment.
        sessions.Insert(protectedCount / 2, (newId, newStarred));
        var before = sessions.ToArray();

        var removed = PracticeHistoryTrim.SelectRemovals(sessions, newId, Cap);

        Assert.Equal(ordinary.Take(expectedRemovals), removed);
        Assert.DoesNotContain(newId, removed);
        Assert.Equal(before, sessions);
    }
}
