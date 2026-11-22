using System.Reflection;
using DotCharter.Core.Domain.Vocals;
using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.Tests.UI.ViewModels.Editor;

public sealed class LyricTapTargetTests
{
    public static TheoryData<string, int[], int[][], int, int, int> States => new()
    {
        {"unstarted", [], [[0, 1], [2]], 4, -1, -1},
        {"split", [1, 0], [[0], [1, 2]], 4, 1, 1},
        {"joined", [1, 0], [[0, 1, 2]], 4, 0, 1},
        {"latest deleted", [1, 0], [[0], [2]], 3, 0, 0},
        {"first deleted", [1, 0], [[1], [2]], 4, 0, 1},
        {"all deleted", [1, 0], [[2]], 4, -1, -1},
        {"equal start", [1, 0], [[0, 1, 2]], 3, 0, 0},
        {"tap before all starts", [1, 0], [[0, 1, 2]], 1, 0, -1},
        {"predecessor already ended", [1, 0], [[0, 1, 2]], 6, 0, -1},
        {"unsorted owner", [1, 0], [[2, 1, 0]], 4, 0, 1},
        {"fallback follows moved survivor", [1, 0], [[2, 0]], 3, 0, 0}
    };


    [Theory]
    [MemberData(nameof(States))]
    public void CurrentOwnershipAndChronologyDetermineTheTargetWithoutMutation(string state, int[] anchors,
        int[][] groups, int tapBeat, int ownerIndex, int trimIndex)
    {
        var words = new[] { 2, 3, 8 }.Select(beat => new LyricEvent
        {
            Text = state, Position = new FractionalPosition(beat), EndPosition = new FractionalPosition(beat + 2)
        }).ToArray();
        var phrases = groups.Select(group => new LyricPhrase
        {
            Lyrics = new(group.Select(i => words[i]))
        }).ToArray();
        var before = words.Select(w => (w.Position, w.EndPosition, w.IsPhraseEnd)).ToArray();

        // Reflect the pure resolver so the same unchanged test assembly can build against
        // the pre-fix source tree during the production-only reverse-patch proof.
        var type = typeof(LyricTapSession).Assembly.GetType("DotCharter.UI.ViewModels.Editor.LyricTapTarget");
        Assert.NotNull(type);
        var resolve = type.GetMethod("Resolve", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(resolve);
        var target = resolve.Invoke(null, [phrases, anchors.Select(i => words[i]), new FractionalPosition(tapBeat)]);
        Assert.NotNull(target);
        Assert.Same(ownerIndex < 0 ? null : phrases[ownerIndex], type.GetProperty("Phrase")!.GetValue(target));
        Assert.Same(trimIndex < 0 ? null : words[trimIndex], type.GetProperty("Trim")!.GetValue(target));
        Assert.Equal(before, words.Select(w => (w.Position, w.EndPosition, w.IsPhraseEnd)));
        for (var i = 0; i < groups.Length; i++) Assert.Equal(groups[i].Select(j => words[j]), phrases[i].Lyrics);
    }
}
