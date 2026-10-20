using DotCharter.Services.Ai.Chart;

namespace DotCharter.Tests.Ai.Chart;

/// <summary>
///     <see cref="AiStructureApplier" /> on its own: what <c>Build</c> writes, what it counts as
///     applied or stale, what <c>Preview</c> marks, and that undo puts the ribbon back. Every
///     change kind and every guard of the composition is pinned here, so it can be restructured
///     with its behaviour held still.
///     <para>
///         The chart: event points (beat: section / phrase) 0 intro/intro, 8 verse/–, 16 –/riff,
///         24 chorus/– carrying an event, 32 "outro " (trailing space)/–; one level with anchors on
///         beats 0 (fret 5) and 8 (fret 3).
///     </para>
/// </summary>
public sealed class AiStructureApplierTests
{
    private readonly Arrangement _arr = BuildArrangement();

    private static Arrangement BuildArrangement()
    {
        var arr = new Arrangement();
        arr.EventPoints.Add(new EventPoint { Position = new FractionalPosition(0), Section = "intro", Phrase = "intro" });
        arr.EventPoints.Add(new EventPoint { Position = new FractionalPosition(8), Section = "verse" });
        arr.EventPoints.Add(new EventPoint { Position = new FractionalPosition(16), Phrase = "riff" });
        arr.EventPoints.Add(new EventPoint { Position = new FractionalPosition(24), Section = "chorus", Events = "e0" });
        arr.EventPoints.Add(new EventPoint { Position = new FractionalPosition(32), Section = "outro " });
        var level = new ChartLevel();
        level.Anchors.Add(new FretAnchor(new FractionalPosition(0), 5));
        level.Anchors.Add(new FretAnchor(new FractionalPosition(8), 3));
        arr.Levels.Add(level);
        return arr;
    }

    private const string Initial = "0:intro/intro 8:verse/ 16:/riff 24:chorus/+e0 32:outro /";
    private const string InitialAnchors = "0:5/4 8:3/4";

    private static FractionalPosition At(int beat) => new(beat);

    private static AiStructureChange Add(AiStructureKind kind, int to, string name, params AiStructureAnchor[] anchors)
        => new(AiStructureOp.Add, kind, null, null, At(to), name) { Anchors = anchors };

    private static AiStructureChange Rename(AiStructureKind kind, int at, string oldName, string name)
        => new(AiStructureOp.Rename, kind, At(at), oldName, null, name);

    private static AiStructureChange Move(AiStructureKind kind, int from, string oldName, int to,
        params AiStructureAnchor[] anchors)
        => new(AiStructureOp.Move, kind, At(from), oldName, At(to), null) { Anchors = anchors };

    private static AiStructureChange Remove(AiStructureKind kind, int at, string oldName)
        => new(AiStructureOp.Remove, kind, At(at), oldName, null, null);

    private const AiStructureKind S = AiStructureKind.Section;
    private const AiStructureKind P = AiStructureKind.Phrase;

    private static string Ribbon(Arrangement arr) => string.Join(" ", arr.EventPoints
        .OrderBy(e => e.Position).ThenBy(e => e.Section, StringComparer.Ordinal)
        .Select(e => $"{e.Position.BeatId}:{e.Section}/{e.Phrase}" + (e.Events.Length > 0 ? "+" + e.Events : "")));

    private static string Anchors(Arrangement arr) => string.Join(" ", arr.Levels[0].Anchors
        .OrderBy(a => a.Position).Select(a => $"{a.Position.BeatId}:{a.Fret}/{a.Width}"));

    private static string Marks(AiStructureApplyTargets t)
        => $"S[{string.Join(",", t.Sections.Select(e => e.Position.BeatId))}] " +
           $"P[{string.Join(",", t.Phrases.Select(e => e.Position.BeatId))}] " +
           $"N[{string.Join(",", t.Proposed.Select(e => $"{e.Position.BeatId}:{e.Section}/{e.Phrase}"))}] " +
           $"A[{string.Join(",", t.Anchors.Select(a => $"{a.Anchor.Position.BeatId}:{a.Anchor.Fret}"))}]";

    /// <summary>Builds, previews, executes and undoes <paramref name="changes" />, asserting each step.</summary>
    private void Check(AiStructureChange[] changes, string ribbon, string anchors, int applied, int stale,
        string marks)
    {
        var targets = AiStructureApplier.Preview(_arr, changes, out var previewApplied);
        // Preview reads the same composition and leaves the chart untouched.
        Assert.Equal(marks, Marks(targets));
        Assert.Equal(applied, previewApplied);
        Assert.Equal(Initial, Ribbon(_arr));

        var action = AiStructureApplier.Build(_arr, changes, out var builtApplied, out var builtStale);
        Assert.Equal((applied, stale), (builtApplied, builtStale));
        Assert.Equal(Initial, Ribbon(_arr));
        Assert.Equal(InitialAnchors, Anchors(_arr));

        if (ribbon == Initial && anchors == InitialAnchors)
        {
            Assert.Null(action);
            return;
        }

        Assert.NotNull(action);
        action.Execute();
        Assert.Equal(ribbon, Ribbon(_arr));
        Assert.Equal(anchors, Anchors(_arr));

        action.Undo();
        Assert.Equal(Initial, Ribbon(_arr));
        Assert.Equal(InitialAnchors, Anchors(_arr));
    }

    [Fact]
    public void A_rename_writes_the_new_name_in_place()
        => Check([Rename(S, 8, "verse", "bridge")],
            "0:intro/intro 8:bridge/ 16:/riff 24:chorus/+e0 32:outro /", InitialAnchors, 1, 0,
            "S[8] P[] N[8:bridge/] A[]");

    [Fact]
    public void A_rename_matches_the_live_name_trimmed()
        => Check([Rename(S, 32, "outro", "ending")],
            "0:intro/intro 8:verse/ 16:/riff 24:chorus/+e0 32:ending/", InitialAnchors, 1, 0,
            "S[32] P[] N[32:ending/] A[]");

    [Fact]
    public void A_rename_to_the_same_name_is_applied_but_builds_nothing()
        => Check([Rename(S, 8, "verse", "verse")], Initial, InitialAnchors, 1, 0, "S[8] P[] N[8:verse/] A[]");

    [Fact]
    public void A_rename_of_a_marker_renamed_since_is_stale()
        => Check([Rename(S, 8, "chorus", "bridge")], Initial, InitialAnchors, 0, 1, "S[] P[] N[] A[]");

    [Fact]
    public void A_rename_of_a_phrase_does_not_touch_the_section()
        => Check([Rename(P, 0, "intro", "count-in")],
            "0:intro/count-in 8:verse/ 16:/riff 24:chorus/+e0 32:outro /", InitialAnchors, 1, 0,
            "S[] P[0] N[0:/count-in] A[]");

    [Fact]
    public void Two_changes_on_one_marker_claim_it_once()
        => Check([Rename(S, 8, "verse", "a"), Rename(S, 8, "verse", "b")],
            "0:intro/intro 8:a/ 16:/riff 24:chorus/+e0 32:outro /", InitialAnchors, 1, 1,
            "S[8] P[] N[8:a/] A[]");

    [Fact]
    public void Removing_the_only_field_removes_the_event_point()
        => Check([Remove(S, 8, "verse")],
            "0:intro/intro 16:/riff 24:chorus/+e0 32:outro /", InitialAnchors, 1, 0, "S[8] P[] N[] A[]");

    [Fact]
    public void Removing_one_field_keeps_the_other()
        => Check([Remove(S, 0, "intro")],
            "0:/intro 8:verse/ 16:/riff 24:chorus/+e0 32:outro /", InitialAnchors, 1, 0, "S[0] P[] N[] A[]");

    [Fact]
    public void Removing_both_fields_removes_the_event_point()
        => Check([Remove(S, 0, "intro"), Remove(P, 0, "intro")],
            "8:verse/ 16:/riff 24:chorus/+e0 32:outro /", InitialAnchors, 2, 0, "S[0] P[0] N[] A[]");

    [Fact]
    public void An_event_point_that_carries_events_is_kept_empty()
        => Check([Remove(S, 24, "chorus")],
            "0:intro/intro 8:verse/ 16:/riff 24:/+e0 32:outro /", InitialAnchors, 1, 0, "S[24] P[] N[] A[]");

    [Fact]
    public void A_move_to_a_free_spot_clears_the_source_and_creates_the_destination()
        => Check([Move(S, 8, "verse", 12)],
            "0:intro/intro 12:verse/ 16:/riff 24:chorus/+e0 32:outro /", InitialAnchors, 1, 0,
            "S[8] P[] N[12:verse/] A[]");

    [Fact]
    public void A_move_onto_a_point_without_that_field_fills_it()
        => Check([Move(P, 16, "riff", 8)],
            "0:intro/intro 8:verse/riff 24:chorus/+e0 32:outro /", InitialAnchors, 1, 0,
            "S[] P[16] N[8:/riff] A[]");

    [Fact]
    public void A_move_onto_a_taken_spot_is_stale_and_leaves_its_marker()
        => Check([Move(S, 8, "verse", 24)], Initial, InitialAnchors, 0, 1, "S[] P[] N[] A[]");

    [Fact]
    public void A_move_of_a_marker_that_is_gone_is_stale()
        => Check([Move(S, 12, "verse", 20)], Initial, InitialAnchors, 0, 1, "S[] P[] N[] A[]");

    [Fact]
    public void An_add_fills_an_existing_point_that_lacks_the_field()
        => Check([Add(S, 16, "solo")],
            "0:intro/intro 8:verse/ 16:solo/riff 24:chorus/+e0 32:outro /", InitialAnchors, 1, 0,
            "S[] P[] N[16:solo/] A[]");

    [Fact]
    public void An_add_onto_a_taken_spot_is_stale()
        => Check([Add(S, 0, "solo")], Initial, InitialAnchors, 0, 1, "S[] P[] N[] A[]");

    [Fact]
    public void Two_adds_at_one_new_spot_share_one_event_point()
        => Check([Add(S, 40, "solo"), Add(P, 40, "lead")],
            "0:intro/intro 8:verse/ 16:/riff 24:chorus/+e0 32:outro / 40:solo/lead", InitialAnchors, 2, 0,
            "S[] P[] N[40:solo/,40:/lead] A[]");

    [Fact]
    public void Two_adds_of_one_kind_at_one_spot_apply_once()
        => Check([Add(S, 40, "solo"), Add(S, 40, "lead")],
            "0:intro/intro 8:verse/ 16:/riff 24:chorus/+e0 32:outro / 40:solo/", InitialAnchors, 1, 1,
            "S[] P[] N[40:solo/] A[]");

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Clears_run_before_sets_so_a_move_and_a_replacement_compose_in_either_order(bool moveFirst)
    {
        var move = Move(S, 8, "verse", 12);
        var add = Add(S, 8, "bridge");
        Check(moveFirst ? [move, add] : [add, move],
            "0:intro/intro 8:bridge/ 12:verse/ 16:/riff 24:chorus/+e0 32:outro /", InitialAnchors, 2, 0,
            moveFirst ? "S[8] P[] N[12:verse/,8:bridge/] A[]" : "S[8] P[] N[8:bridge/,12:verse/] A[]");
    }

    [Fact]
    public void A_phrase_brings_only_the_anchors_its_level_lacks()
        => Check([
                Add(P, 4, "lick",
                    new AiStructureAnchor(0, At(4), 7, 4),
                    new AiStructureAnchor(0, At(8), 2, 4),
                    new AiStructureAnchor(5, At(4), 1, 4),
                    new AiStructureAnchor(-1, At(4), 1, 4),
                    new AiStructureAnchor(0, At(4), 9, 4))
            ],
            "0:intro/intro 4:/lick 8:verse/ 16:/riff 24:chorus/+e0 32:outro /", "0:5/4 4:7/4 8:3/4", 1, 0,
            "S[] P[] N[4:/lick] A[4:7]");

    [Fact]
    public void A_moved_phrase_brings_its_anchor_too()
        => Check([Move(P, 16, "riff", 20, new AiStructureAnchor(0, At(20), 2, 5))],
            "0:intro/intro 8:verse/ 20:/riff 24:chorus/+e0 32:outro /", "0:5/4 8:3/4 20:2/5", 1, 0,
            "S[] P[16] N[20:/riff] A[20:2]");

    [Fact]
    public void A_stale_change_brings_no_anchor()
        => Check([Add(P, 16, "lick", new AiStructureAnchor(0, At(16), 7, 4))], Initial, InitialAnchors, 0, 1,
            "S[] P[] N[] A[]");

    [Fact]
    public void Missing_arguments_throw()
    {
        Assert.Throws<ArgumentNullException>(() => AiStructureApplier.Build(null!, [], out _, out _));
        Assert.Throws<ArgumentNullException>(() => AiStructureApplier.Build(_arr, null!, out _, out _));
        Assert.Throws<ArgumentNullException>(() => AiStructureApplier.Preview(null!, [], out _));
    }

    [Fact]
    public void Nothing_to_do_builds_nothing()
        => Check([], Initial, InitialAnchors, 0, 0, "S[] P[] N[] A[]");
}
