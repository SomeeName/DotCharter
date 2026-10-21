using DotCharter.Core.Linking;
using DotCharter.Tone.Infrastructure.Storage;
using DotCharter.Services.Editing.Collisions;
using DotCharter.Services.Parsing.Rscp;
using DotCharter.Tests.Core.Linking;
using static DotCharter.Tests.Core.Linking.LinkFixture;

namespace DotCharter.Tests.Services.State.Linking;

/// <summary>
///     The release blockers of linked measures (#76, #88): on random tempo maps with per-bar and
///     per-beat drift, in every spacing mode, a link must survive any number of edit → save →
///     load rounds, and the clean-up passes (the spacing enforcer's sustain clamp, redundant-anchor
///     removal, the 5 ms anchor snap) must never break one.
/// </summary>
public sealed class LinkedMeasuresReleaseBlockerTests
{
    private const int Rounds = 8;

    /// <summary>
    ///     #82's tempo maps (random tempo per bar, up to ±8 % drift per beat, every spacing mode),
    ///     minus the maps on which the minimum note distance is COARSER than the quarter-beat grid
    ///     the random edits use at some bar's tempo (80 ms above ~187 BPM). There the clean-up pass
    ///     itself must shorten a sustain in the fast copy only, so the copies stop sounding alike and
    ///     the comparison rule rightly calls them different; that case is pinned on its own below
    ///     (<see cref="A_clamp_only_one_copys_tempo_forces_unlinks_that_copy_and_keeps_the_edit" />).
    /// </summary>
    public static TheoryData<int, MinNoteDistanceMode, double, int> TempoMaps()
    {
        var data = new TheoryData<int, MinNoteDistanceMode, double, int>();
        foreach (var row in MeasureContentIndexTests.TempoMaps())
        {
            var (seed, mode, ms, divisor) = row.Data;
            var maxBpm = Beats(9, seed).Max(b => b.Bpm);
            if (mode == MinNoteDistanceMode.Milliseconds && ms * maxBpm / 60_000 >= 0.25) continue;
            data.Add(seed, mode, ms, divisor);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(TempoMaps))]
    public void Every_link_survives_repeated_edit_save_load_rounds(int seed, MinNoteDistanceMode mode, double ms, int divisor)
    {
        var config = Config(mode, ms, divisor);
        var h = LinkHarness.WithRhythm(seed, config);
        h.Link((h.Lead, 1), (h.Lead, 4), (h.Lead, 7), (h.Rhythm, 1), (h.Rhythm, 4), (h.Rhythm, 7));
        var random = new Random(seed * 7919 + (int)mode);
        var dir = Path.Combine(Path.GetTempPath(), $"dc_links_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            for (var round = 0; round < Rounds; round++)
            {
                var edit = RandomEdit(h, random);
                AssertOneGroupOfSix(h, $"round {round} after '{edit}'");

                var path = Path.Combine(dir, $"r{round}.rscp");
                var snapshot = ChartProjectCloner.Clone(h.Project);
                ChartSpacingEnforcer.EnforceProject(snapshot, config); // the save path
                new RscpChartSerializer().Save(path, snapshot);
                var loaded = new RscpChartSerializer().Load(path);
                ChartSpacingEnforcer.EnforceProject(loaded, config); // the load path

                h = new LinkHarness(loaded, config);
                AssertOneGroupOfSix(h, $"round {round} after save/load of '{edit}'");
            }
        }
        finally
        {
            BestEffortDelete.Directory(dir);
        }
    }

    /// <summary>
    ///     The clean-up passes, run again and again over linked copies (between propagated edits),
    ///     never change what the copies compare as: the group's content hash before equals the hash
    ///     after, so the load check of a file saved at any point keeps every link.
    /// </summary>
    [Theory]
    [MemberData(nameof(TempoMaps))]
    public void The_clean_up_passes_never_break_a_link(int seed, MinNoteDistanceMode mode, double ms, int divisor)
    {
        var config = Config(mode, ms, divisor);
        var h = LinkHarness.WithRhythm(seed, config);
        var group = h.Link((h.Lead, 1), (h.Lead, 4), (h.Lead, 7), (h.Rhythm, 4));
        var random = new Random(seed);

        for (var pass = 0; pass < 4; pass++)
        {
            RandomEdit(h, random);
            var hash = h.Keys(group)[0]!.Hash;

            ChartSpacingEnforcer.EnforceProject(h.Project, config);
            ChartSpacingEnforcer.EnforceProject(h.Project, config);

            Assert.All(h.Keys(group), k => Assert.Equal(hash, k!.Hash));
            var notices = LinkGroupVerifier.Verify(h.Project, new Dictionary<LinkGroup, string> { [group] = hash });
            Assert.Empty(notices);
            LinkHarness.AssertLinkedAndEqual(h, group, 4);
        }
    }

    /// <summary>
    ///     The limit of "content in beats": with an 80 ms minimum note distance, a sustain that ends a
    ///     quarter beat before the next note is left alone at 120 BPM (125 ms of room) and must be
    ///     SHORTENED at 240 BPM (62.5 ms). The copies then sound different, and the fast copy is
    ///     unlinked — with the edit applied, never left linked and different.
    /// </summary>
    [Fact]
    public void A_clamp_only_one_copys_tempo_forces_unlinks_that_copy_and_keeps_the_edit()
    {
        var config = Config(MinNoteDistanceMode.Milliseconds, 80);
        var project = LinkFixture.Project(null);
        var beats = project.Beats;
        for (var i = 1; i < beats.Count; i++)
        {
            var beatMs = i - 1 is >= 28 and < 32 ? 250.0 : 500.0; // bar 7 at 240 BPM
            beats[i].Time = beats[i - 1].Time + beatMs;
            beats[i].Bpm = 60_000 / (i is >= 28 and < 32 ? 250.0 : 500.0);
        }

        ChartSpacingEnforcer.EnforceProject(project, config);
        var h = new LinkHarness(project, config);
        var group = h.Link((h.Lead, 1), (h.Lead, 4), (h.Lead, 7));
        var level = LinkHarness.Hardest(h.Lead);

        // Bar 4: a note a quarter beat after the bent note's half-beat sustain ends.
        Assert.True(h.History.Do(new AddNotesAction(level.Notes, [Note(At(4, 1.75), 5, 3, 0)],
            () => ChartSpacingEnforcer.EnforceLevel(level, h.Project.Beats, config))));

        LinkHarness.AssertLinkedAndEqual(h, group, 2);
        Assert.DoesNotContain(group.Placements, p => p.Position == At(7, 0));
        Assert.Contains(level.Notes, n => n.Position == At(7, 1.75) && n.StringIndex == 5); // the edit reached it
        Assert.True(LinkHarness.NoteAt(h.Lead, At(7, 1), 1).Sustain < Beats(0.5)); // …and the clamp shortened it there
    }

    private static void AssertOneGroupOfSix(LinkHarness h, string when)
    {
        var group = Assert.Single(h.Project.LinkGroups);
        Assert.True(group.Placements.Count == 6, $"{when}: {group.Placements.Count} placements left");
        var keys = h.Keys(group);
        Assert.True(keys.All(k => k != null) && keys.Select(k => k!.Canonical).Distinct().Count() == 1,
            $"{when}: the copies differ");
    }

    /// <summary>One random editing gesture inside a random placement, through the history.</summary>
    internal static string RandomEdit(LinkHarness h, Random random)
    {
        var group = h.Project.LinkGroups[0];
        var placement = group.Placements[random.Next(group.Placements.Count)];
        var arrangement = placement.Arrangement!;
        var level = LinkHarness.Hardest(arrangement);
        var notes = level.Notes.Where(n => n.Position >= placement.Position && n.Position < placement.EndPosition).ToList();
        void Enforce() => ChartSpacingEnforcer.EnforceLevel(level, h.Project.Beats, h.Config);

        switch (random.Next(6))
        {
            case 0 when notes.Count > 0:
            {
                var fret = random.Next(1, 16);
                h.Edit(arrangement, notes[random.Next(notes.Count)], n => n.Fret = fret);
                return $"fret {fret}";
            }
            case 1 when notes.Count > 0:
            {
                var sustain = new[] { 0, 0.25, 0.5, 1, 2, 4 }[random.Next(6)];
                h.Edit(arrangement, notes[random.Next(notes.Count)], n => n.Sustain = Beats(sustain));
                return $"sustain {sustain}";
            }
            case 2:
            {
                var at = placement.Position + Beats(random.Next(16) / 4.0);
                var stringIndex = random.Next(6);
                if (level.Notes.Any(n => n.Position == at && n.StringIndex == stringIndex) || level.Chords.Any(c => c.Position == at))
                    return "nothing (occupied)";
                h.History.Do(new AddNotesAction(level.Notes, [Note(at, stringIndex, random.Next(0, 13), random.Next(4) / 2.0)], Enforce));
                return $"add at {at}";
            }
            case 3 when notes.Count > 1:
                h.History.Do(new RemoveNotesAction(level.Notes, [notes[random.Next(notes.Count)]], Enforce));
                return "remove";
            case 4 when notes.Count > 0:
                h.Edit(arrangement, notes[random.Next(notes.Count)], n => n.Vibrato = !n.Vibrato);
                return "vibrato";
            default:
            {
                var anchors = level.Anchors.Where(a => a.Position >= placement.Position && a.Position < placement.EndPosition).ToList();
                if (anchors.Count == 0) return "nothing (no anchor)";
                var anchor = anchors[random.Next(anchors.Count)];
                var moved = anchor.Clone();
                moved.Fret = random.Next(1, 13);
                h.History.Do(new MutateAnchorsAction(level.Anchors, [anchor], [anchor.Clone()], [moved], Enforce));
                return $"anchor {moved.Fret}";
            }
        }
    }
}
