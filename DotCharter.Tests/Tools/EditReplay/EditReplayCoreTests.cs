using DotCharter.Tools.EditReplay;

namespace DotCharter.Tests.Tools.EditReplay;

/// <summary>
///     The pure half of tools/edit-replay (#944) on a tiny synthetic pair: a "fresh import" and the
///     "charter's result" with known differences, so every per-category count, the link-aware count
///     and the log parser are pinned. Four beats to the bar; bars 2 and 3 are one link group.
/// </summary>
public sealed class EditReplayCoreTests
{
    private static readonly BarMap FourFour = new([0, 4, 8, 12, 16, 20]);

    private static readonly IReadOnlyList<PlacementSpan>[] Groups =
        [[new PlacementSpan("Lead", 4, 8), new PlacementSpan("Lead", 8, 12)]];

    [Fact]
    public void Counts_every_category_over_the_edited_bars()
    {
        var edits = Diff();

        var counts = ReplayDiff.CountByCategory(edits);

        Assert.Equal(3, counts[EditCategory.Anchor]);
        Assert.Equal(2, counts[EditCategory.Shape]);
        Assert.Equal(2, counts[EditCategory.Grip]);
        Assert.Equal(0, counts[EditCategory.Fingering]);
        Assert.Equal(1, counts[EditCategory.Sustain]);
        Assert.Equal(1, counts[EditCategory.Note]);
        Assert.Equal(9, counts.Values.Sum());
    }

    [Fact]
    public void Lists_fingering_a_grip_save_covers_without_counting_it()
    {
        var fingering = Diff().Where(e => e.Category == EditCategory.Fingering).ToList();

        Assert.Equal(2, fingering.Count);
        Assert.All(fingering, e => Assert.False(e.Counted));
    }

    [Fact]
    public void Leaves_bars_outside_the_range_alone()
    {
        Assert.DoesNotContain(Diff(), e => e.Bar == 5);
        Assert.Contains(ReplayDiff.Diff("Lead", ChartFacts.Of(Fresh()), ChartFacts.Of(Charter()), FourFour, 1, 5), e => e.Bar == 5);
    }

    [Fact]
    public void An_edit_repeated_identically_in_a_linked_placement_is_free()
    {
        var counts = ReplayDiff.CountWithLinks(Diff(), Groups);

        Assert.Equal(2, counts[EditCategory.Anchor]);
        Assert.Equal(1, counts[EditCategory.Shape]);
        Assert.Equal(1, counts[EditCategory.Grip]);
        Assert.Equal(1, counts[EditCategory.Sustain]);
        Assert.Equal(1, counts[EditCategory.Note]);
        Assert.Equal(6, counts.Values.Sum());
    }

    [Fact]
    public void A_linked_placement_whose_result_differs_still_costs_its_edit()
    {
        var charter = Charter();
        charter.Levels[0].Anchors.Single(a => a.Position == new FractionalPosition(8)).Fret = 7;

        var counts = ReplayDiff.CountWithLinks(
            ReplayDiff.Diff("Lead", ChartFacts.Of(Fresh()), ChartFacts.Of(charter), FourFour, 1, 4), Groups);

        Assert.Equal(3, counts[EditCategory.Anchor]);
    }

    [Fact]
    public void Parses_chart_edits_undos_and_edited_beats_from_the_log()
    {
        string[] log =
        [
            "2026-01-01 10:00:00.000 +01:00 [INFO] [History] [T2] Executed ProjectSnapshotAction (target=Lead, node=3). Nodes=1.",
            "2026-01-01 10:00:01.000 +01:00 [INFO] [History] [T2] Executed key:History_LinkSuggestions (target=Lead, node=4). Nodes=2.",
            "2026-01-01 10:00:02.000 +01:00 [INFO] [Editing] [T2] Selected FHP anchor for drag. SelectedAnchors=1, anchor fret=3, width=4, position=12 2/3 (13016,589 ms).",
            "2026-01-01 10:00:03.000 +01:00 [INFO] [History] [T2] Executed key:History_Delete (target=Lead, node=5). Nodes=3.",
            "2026-01-01 10:00:04.000 +01:00 [INFO] [History] [T2] Executed verb:sustain (target=Lead, node=6). Nodes=4.",
            "2026-01-01 10:00:05.000 +01:00 [INFO] [History] [T2] Undo CompositeAction (target=Lead). Node 6 -> 5.",
            "2026-01-01 10:00:06.000 +01:00 [INFO] [Editing] [T2] Double-clicked FHP lane at 40 0/1 (40000,000 ms). ExistingAnchor=True."
        ];

        var session = LoggedSession.Parse(log);

        Assert.Equal(2, session.Edits["Lead"]);
        Assert.Equal(1, session.Undos);
        Assert.Equal([12 + 2.0 / 3, 40], session.EditedBeats);
    }

    private static List<ReplayEdit> Diff() =>
        ReplayDiff.Diff("Lead", ChartFacts.Of(Fresh()), ChartFacts.Of(Charter()), FourFour, 1, 4);

    // Bar 1: a note's sustain and a note's fret differ. Bars 2 and 3 (linked): the anchor's fret,
    // the shape's end and its grip (and so the chord under it) change identically. Bar 4: an anchor
    // moves one beat. Bar 5, outside the range: an anchor the charter deleted.
    private static Arrangement Fresh() => Build(
        [(0, 1), (4, 3), (8, 3), (12, 5), (16, 7)],
        shapeEnd: 6, finger: 1, sustain: new Fraction(1, 2), secondFret: 3);

    private static Arrangement Charter() => Build(
        [(0, 1), (4, 5), (8, 5), (13, 5)],
        shapeEnd: 7, finger: 3, sustain: Fraction.One, secondFret: 5);

    private static Arrangement Build((int Beat, int Fret)[] anchors, int shapeEnd, int finger, Fraction sustain, int secondFret)
    {
        var arrangement = new Arrangement { Name = "Lead" };
        arrangement.ChordTemplates.Add(new ChordTemplate
        {
            Id = 0,
            Frets = new Dictionary<int, int> { [0] = 3, [1] = 3 },
            Fingers = new Dictionary<int, int> { [0] = 1, [1] = finger }
        });
        var level = new ChartLevel();
        foreach (var (beat, fret) in anchors) level.Anchors.Add(new FretAnchor { Position = new FractionalPosition(beat), Fret = fret, Width = 4 });
        level.Notes.Add(new GuitarNote { Position = new FractionalPosition(1), StringIndex = 0, Fret = 2, Sustain = new FractionalPosition(sustain) });
        level.Notes.Add(new GuitarNote { Position = new FractionalPosition(2), StringIndex = 0, Fret = secondFret });
        foreach (var start in new[] { 4, 8 })
        {
            level.HandShapes.Add(new HandShape
            {
                Position = new FractionalPosition(start),
                EndPosition = new FractionalPosition(start + shapeEnd - 4),
                TemplateId = 0
            });
            level.Chords.Add(new Chord
            {
                Position = new FractionalPosition(start),
                TemplateId = 0,
                ChordNotes = [new ChordNote { StringIndex = 0, Fret = 3 }, new ChordNote { StringIndex = 1, Fret = 3 }]
            });
        }

        arrangement.Levels.Add(level);
        return arrangement;
    }

    [Fact]
    public void Bars_come_from_the_beat_map_measure_starts()
    {
        var beats = Enumerable.Range(0, 7).Select(i => new BeatMarker { IsMeasure = i is 0 or 3 }).ToList();

        var bars = BarMap.Of(beats);

        Assert.Equal(1, bars.BarOf(2.5));
        Assert.Equal(2, bars.BarOf(3));
        Assert.Equal(2, bars.BarOf(6));
    }
}
