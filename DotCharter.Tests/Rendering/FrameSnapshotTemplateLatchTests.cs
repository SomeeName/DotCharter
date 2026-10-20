using DotCharter.Rendering.Data;

namespace DotCharter.Tests.Rendering;

/// <summary>
///     The chord-template projection latch (<c>FrameSnapshot.UpdateTemplates</c>).
///     The table used to be rebuilt every frame on both renderers — per template
///     a struct build plus 8x <see cref="ChordTemplate.GetFret" /> + 8x
///     <see cref="ChordTemplate.GetFinger" /> dictionary lookups — although
///     templates change only on a chart edit.
///     <para>
///         Identity + count is not sound on its own here: the editing verbs
///         mutate templates IN PLACE (FingeringGenerationService writes
///         <c>template.Frets</c>/<c>.Fingers</c>, ArrangementAutoFixService
///         clamps a fret to the capo, ChartSpacingEnforcer and
///         DoubleStopChordNames assign <c>template.Name</c>). Hence the two
///         modes pinned below: an exact latch on
///         <see cref="SnapshotUpdateContext.TemplatesVersion" />, and a
///         250 ms-bounded identity+count latch when unversioned.
///     </para>
///     No wall-clock assertion anywhere — the staleness bound is driven through
///     the snapshot's internal <c>TemplateLatchClock</c> seam.
/// </summary>
public sealed class FrameSnapshotTemplateLatchTests
{
    private static ChordTemplate Template(int id, int fret)
    {
        var t = new ChordTemplate { Id = id, Name = "T" + id };
        t.Frets[0] = fret;
        t.Fingers[0] = 1;
        return t;
    }

    private static SnapshotUpdateContext Context(IList<ChordTemplate> templates)
    {
        return new SnapshotUpdateContext
        {
            ActiveTemplates = templates,
            ViewStartMs = 0,
            ViewEndMs = 1000,
            VisibleRangeBufferMs = 0,
            NoteEnd = -1,
            ChordEnd = -1,
            AnchorEnd = -1,
            HandShapeEnd = -1,
            BeatEnd = -1
        };
    }

    /// <summary>Sentinel written straight into the projected table; a rebuild erases it.</summary>
    private static void Stamp(FrameSnapshot s) => s.Templates[0].Id = -999;

    private static bool SentinelSurvived(FrameSnapshot s) => s.Templates[0].Id == -999;

    [Fact]
    public void Versioned_context_skips_the_reprojection_until_the_version_moves()
    {
        var templates = new List<ChordTemplate> { Template(0, 3), Template(1, 5) };
        var ctx = Context(templates);
        ctx.TemplatesVersion = 7;
        var snapshot = new FrameSnapshot();

        snapshot.Update(ctx);
        Assert.Equal(2, snapshot.TemplateCount);
        Assert.Equal(0, snapshot.Templates[0].Id);

        // Same instance, same count, same version: the latch holds and the
        // sentinel is still there after a second Update.
        Stamp(snapshot);
        snapshot.Update(ctx);
        Assert.True(SentinelSurvived(snapshot));

        // Version moves (what an in-place edit must be accompanied by): rebuilt.
        ctx.TemplatesVersion = 8;
        snapshot.Update(ctx);
        Assert.False(SentinelSurvived(snapshot));
        Assert.Equal(0, snapshot.Templates[0].Id);
    }

    [Fact]
    public void A_count_change_rebuilds_even_at_the_same_version()
    {
        var templates = new List<ChordTemplate> { Template(0, 3) };
        var ctx = Context(templates);
        ctx.TemplatesVersion = 1;
        var snapshot = new FrameSnapshot();

        snapshot.Update(ctx);
        Stamp(snapshot);

        templates.Add(Template(1, 5));
        snapshot.Update(ctx);
        Assert.False(SentinelSurvived(snapshot));
        Assert.Equal(2, snapshot.TemplateCount);
    }

    [Fact]
    public void A_new_list_instance_rebuilds_even_at_the_same_version_and_count()
    {
        var ctx = Context([Template(0, 3)]);
        ctx.TemplatesVersion = 1;
        var snapshot = new FrameSnapshot();

        snapshot.Update(ctx);
        Stamp(snapshot);

        ctx.ActiveTemplates = [Template(0, 3)];
        snapshot.Update(ctx);
        Assert.False(SentinelSurvived(snapshot));
    }

    [Fact]
    public void Unversioned_context_latches_inside_the_staleness_bound_and_rebuilds_past_it()
    {
        var templates = new List<ChordTemplate> { Template(0, 3) };
        var ctx = Context(templates); // TemplatesVersion stays -1
        var clock = 1_000L;
        var snapshot = new FrameSnapshot { TemplateLatchClock = () => clock };

        snapshot.Update(ctx);
        Assert.Equal(1, snapshot.TemplateCount);

        // Inside the bound — the frame-rate case: the table is not re-projected.
        Stamp(snapshot);
        clock += 100;
        snapshot.Update(ctx);
        Assert.True(SentinelSurvived(snapshot));

        // Past the bound: the latch self-heals, which is what makes an
        // in-place template edit visible without a version.
        clock += 200;
        snapshot.Update(ctx);
        Assert.False(SentinelSurvived(snapshot));
        Assert.Equal(0, snapshot.Templates[0].Id);
    }

    [Fact]
    public void An_in_place_edit_is_picked_up_once_the_unversioned_bound_expires()
    {
        var edited = Template(0, 3);
        var ctx = Context([edited]);
        var clock = 0L;
        var snapshot = new FrameSnapshot { TemplateLatchClock = () => clock };

        snapshot.Update(ctx);
        Assert.Equal(3, snapshot.Templates[0].GetFret(0));

        // Exactly what ArrangementAutoFixService/FingeringGenerationService do:
        // mutate the template in place, list instance and count unchanged.
        edited.Frets[0] = 9;
        clock += 10;
        snapshot.Update(ctx);
        Assert.Equal(3, snapshot.Templates[0].GetFret(0)); // still latched

        clock += 300;
        snapshot.Update(ctx);
        Assert.Equal(9, snapshot.Templates[0].GetFret(0));
    }
}
