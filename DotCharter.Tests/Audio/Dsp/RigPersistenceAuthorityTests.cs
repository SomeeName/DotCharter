using DotCharter.Tone.Audio.Dsp;

namespace DotCharter.Tests.Audio.Dsp;

/// <summary>Literal oracle for every signature/live/pending/read partition and every owner event.</summary>
public sealed class RigPersistenceAuthorityTests
{
    // A/B are symbolic rig identities: U equals the known saved A; C differs from it (B),
    // E is empty. Unknown saved identity cannot certify a matching observation.
    // Both values of every axis are retained even for unreachable combinations: outcome
    // events in an ineligible state are no-ops. Each cell spells next state and action bits.
    // Actions: 1 flush pending, 2 snapshot, 4 retry read, 8 publish, 16 restore, 32 preserve recovery.
    private const string Table = """
        0E0L 0C0L:0 0C0L:0 0E0L:0 0E0L:0 0E0L:0 0E0L:3 0E0L:0 0E0L:16 1U0L:0 0C1L:0 0E0I:0 0E0R:0 0E0L:0 0E0R:16 0E0L:3
        0E0R 0C0R:0 0C0R:0 0E0R:0 0E0R:0 0E0R:0 0E0R:5 0E0R:0 0E0R:16 1U0L:0 0C1L:0 0E0I:0 0E0R:0 0E0R:0 0E0R:16 0E0R:5
        0E0I 0C0I:0 0C0I:0 0E0I:0 0E0I:0 0E0I:0 0E0I:1 0E0I:0 0E0I:16 1U0L:0 0C1L:0 0E0I:0 0E0R:0 0E0I:0 0E0R:16 0E0I:1
        0E1L 0C1L:0 0C1L:0 0E1L:0 0E1L:0 0E1L:0 0E1L:3 0E1L:0 0E1L:16 1U0L:0 0C1L:0 0E1I:0 0E1R:0 0E1L:0 0E0R:16 0E1L:3
        0E1R 0C1R:0 0C1R:0 0E1R:0 0E1R:0 0E1R:0 0E1R:5 0E1R:0 0E1R:16 1U0L:0 0C1L:0 0E1I:0 0E1R:0 0E1R:0 0E0R:16 0E1R:5
        0E1I 0C1I:0 0C1I:0 0E1I:0 0E1I:0 0E1I:0 0E1I:1 0E1I:0 0E1I:16 1U0L:0 0C1L:0 0E1I:0 0E1R:0 0E1I:0 0E0R:16 0E1I:1
        0U0L 0C0L:0 0C0L:0 0E0L:0 1U0L:0 0C1L:0 0U0L:3 0U0L:8 0E0L:16 1U0L:0 0C1L:0 0U0I:0 0U0R:0 0C0L:0 0E0R:16 0U0L:3
        0U0R 0C0R:0 0C0R:0 0E0R:0 0U0R:0 0U0R:0 0U0R:5 0U0R:0 0E0R:16 1U0L:0 0C1L:0 0U0I:0 0U0R:0 0U0R:0 0E0R:16 0U0R:5
        0U0I 0C0I:0 0C0I:0 0E0I:0 0U0I:0 0U0I:0 0U0I:1 0U0I:0 0E0I:16 1U0L:0 0C1L:0 0U0I:0 0U0R:0 0C0L:32 0E0R:16 0U0I:1
        0U1L 0C1L:0 0C1L:0 0E1L:0 1U0L:0 0C1L:0 0U1L:3 0U1L:8 0E1L:16 1U0L:0 0C1L:0 0U1I:0 0U1R:0 0C1L:0 0E0R:16 0U1L:3
        0U1R 0C1R:0 0C1R:0 0E1R:0 0U1R:0 0U1R:0 0U1R:5 0U1R:0 0E1R:16 1U0L:0 0C1L:0 0U1I:0 0U1R:0 0U1R:0 0E0R:16 0U1R:5
        0U1I 0C1I:0 0C1I:0 0E1I:0 0U1I:0 0U1I:0 0U1I:1 0U1I:0 0E1I:16 1U0L:0 0C1L:0 0U1I:0 0U1R:0 0C1L:32 0E0R:16 0U1I:1
        0C0L 0C0L:0 0C0L:0 0E0L:0 1U0L:0 0C1L:0 0C0L:3 0C0L:8 0E0L:16 1U0L:0 0C1L:0 0C0I:0 0C0R:0 0C0L:0 0E0R:16 0C0L:3
        0C0R 0C0R:0 0C0R:0 0E0R:0 0C0R:0 0C0R:0 0C0R:5 0C0R:0 0E0R:16 1U0L:0 0C1L:0 0C0I:0 0C0R:0 0C0R:0 0E0R:16 0C0R:5
        0C0I 0C0I:0 0C0I:0 0E0I:0 0C0I:0 0C0I:0 0C0I:1 0C0I:0 0E0I:16 1U0L:0 0C1L:0 0C0I:0 0C0R:0 0C0L:32 0E0R:16 0C0I:1
        0C1L 0C1L:0 0C1L:0 0E1L:0 1U0L:0 0C1L:0 0C1L:3 0C1L:8 0E1L:16 1U0L:0 0C1L:0 0C1I:0 0C1R:0 0C1L:0 0E0R:16 0C1L:3
        0C1R 0C1R:0 0C1R:0 0E1R:0 0C1R:0 0C1R:0 0C1R:5 0C1R:0 0E1R:16 1U0L:0 0C1L:0 0C1I:0 0C1R:0 0C1R:0 0E0R:16 0C1R:5
        0C1I 0C1I:0 0C1I:0 0E1I:0 0C1I:0 0C1I:0 0C1I:1 0C1I:0 0E1I:16 1U0L:0 0C1L:0 0C1I:0 0C1R:0 0C1L:32 0E0R:16 0C1I:1
        1E0L 1C0L:0 1U0L:0 1E0L:0 1E0L:0 1E0L:0 1E0L:3 1E0L:0 1E0L:16 1U0L:0 0C1L:0 0E0I:0 0E0R:0 1E0L:0 0E0R:16 1E0L:3
        1E0R 1C0R:0 1U0R:0 1E0R:0 1E0R:0 1E0R:0 1E0R:5 1E0R:0 1E0R:16 1U0L:0 0C1L:0 0E0I:0 0E0R:0 1E0R:0 0E0R:16 1E0R:5
        1E0I 1C0I:0 1U0I:0 1E0I:0 1E0I:0 1E0I:0 1E0I:1 1E0I:0 1E0I:16 1U0L:0 0C1L:0 0E0I:0 0E0R:0 1E0I:0 0E0R:16 1E0I:1
        1E1L 1C1L:0 1U1L:0 1E1L:0 1E1L:0 1E1L:0 1E1L:3 1E1L:0 1E1L:16 1U0L:0 0C1L:0 0E1I:0 0E1R:0 1E1L:0 0E0R:16 1E1L:3
        1E1R 1C1R:0 1U1R:0 1E1R:0 1E1R:0 1E1R:0 1E1R:5 1E1R:0 1E1R:16 1U0L:0 0C1L:0 0E1I:0 0E1R:0 1E1R:0 0E0R:16 1E1R:5
        1E1I 1C1I:0 1U1I:0 1E1I:0 1E1I:0 1E1I:0 1E1I:1 1E1I:0 1E1I:16 1U0L:0 0C1L:0 0E1I:0 0E1R:0 1E1I:0 0E0R:16 1E1I:1
        1U0L 1C0L:0 1U0L:0 1E0L:0 1U0L:0 0C1L:0 1U0L:3 1U0L:0 1E0L:16 1U0L:0 0C1L:0 0U0I:0 0U0R:0 0C0L:0 0E0R:16 1U0L:3
        1U0R 1C0R:0 1U0R:0 1E0R:0 1U0R:0 1U0R:0 1U0R:5 1U0R:0 1E0R:16 1U0L:0 0C1L:0 0U0I:0 0U0R:0 1U0R:0 0E0R:16 1U0R:5
        1U0I 1C0I:0 1U0I:0 1E0I:0 1U0I:0 1U0I:0 1U0I:1 1U0I:0 1E0I:16 1U0L:0 0C1L:0 0U0I:0 0U0R:0 0C0L:32 0E0R:16 1U0I:1
        1U1L 1C1L:0 1U1L:0 1E1L:0 1U0L:0 0C1L:0 1U1L:3 1U1L:8 1E1L:16 1U0L:0 0C1L:0 0U1I:0 0U1R:0 0C1L:0 0E0R:16 1U1L:3
        1U1R 1C1R:0 1U1R:0 1E1R:0 1U1R:0 1U1R:0 1U1R:5 1U1R:0 1E1R:16 1U0L:0 0C1L:0 0U1I:0 0U1R:0 1U1R:0 0E0R:16 1U1R:5
        1U1I 1C1I:0 1U1I:0 1E1I:0 1U1I:0 1U1I:0 1U1I:1 1U1I:0 1E1I:16 1U0L:0 0C1L:0 0U1I:0 0U1R:0 0C1L:32 0E0R:16 1U1I:1
        1C0L 1C0L:0 1U0L:0 1E0L:0 1U0L:0 0C1L:0 1C0L:3 1C0L:8 1E0L:16 1U0L:0 0C1L:0 0C0I:0 0C0R:0 0C0L:0 0E0R:16 1C0L:3
        1C0R 1C0R:0 1U0R:0 1E0R:0 1C0R:0 1C0R:0 1C0R:5 1C0R:0 1E0R:16 1U0L:0 0C1L:0 0C0I:0 0C0R:0 1C0R:0 0E0R:16 1C0R:5
        1C0I 1C0I:0 1U0I:0 1E0I:0 1C0I:0 1C0I:0 1C0I:1 1C0I:0 1E0I:16 1U0L:0 0C1L:0 0C0I:0 0C0R:0 0C0L:32 0E0R:16 1C0I:1
        1C1L 1C1L:0 1U1L:0 1E1L:0 1U0L:0 0C1L:0 1C1L:3 1C1L:8 1E1L:16 1U0L:0 0C1L:0 0C1I:0 0C1R:0 0C1L:0 0E0R:16 1C1L:3
        1C1R 1C1R:0 1U1R:0 1E1R:0 1C1R:0 1C1R:0 1C1R:5 1C1R:0 1E1R:16 1U0L:0 0C1L:0 0C1I:0 0C1R:0 1C1R:0 0E0R:16 1C1R:5
        1C1I 1C1I:0 1U1I:0 1E1I:0 1C1I:0 1C1I:0 1C1I:1 1C1I:0 1E1I:16 1U0L:0 0C1L:0 0C1I:0 0C1R:0 0C1L:32 0E0R:16 1C1I:1
        """;

    public static IEnumerable<object[]> Cases()
    {
        foreach (var row in Table.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var cells = row.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < Enum.GetValues<RigPersistenceEvent>().Length; i++)
                yield return [cells[0], i, cells[i + 1]];
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void EveryStateAndEventHasTheWrittenNextStateAndAction(string before, int input, string expected)
    {
        var parts = expected.Split(':');
        var result = Parse(before).Decide((RigPersistenceEvent)input);
        Assert.Equal(Parse(parts[0]), result.State);
        Assert.Equal((RigPersistenceAction)int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture), result.Action);
    }

    [Fact]
    public void TheLiteralTableCoversTheFullCartesianProductExactlyOnce()
    {
        var states = (from known in new[] { false, true }
                      from live in Enum.GetValues<RigLiveRig>()
                      from pending in new[] { false, true }
                      from read in Enum.GetValues<RigReadAuthority>()
                      select new RigPersistenceAuthority(known, live, pending, read)).ToHashSet();
        var cases = Cases().ToList();
        Assert.Equal(states.Count * Enum.GetValues<RigPersistenceEvent>().Length, cases.Count);
        Assert.Equal(cases.Count, cases.Select(c => (Parse((string)c[0]), (int)c[1])).Distinct().Count());
        Assert.True(states.SetEquals(cases.Select(c => Parse((string)c[0]))));
    }

    private static RigPersistenceAuthority Parse(string code) => new(code[0] == '1',
        code[1] switch { 'E' => RigLiveRig.Empty, 'U' => RigLiveRig.Unchanged, _ => RigLiveRig.Changed },
        code[2] == '1', code[3] switch { 'L' => RigReadAuthority.Live, 'R' => RigReadAuthority.Unreadable, _ => RigReadAuthority.Incomplete });
}
