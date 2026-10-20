using DotCharter.Rendering.Data;

namespace DotCharter.Tests.Rendering;

/// <summary>
/// The HUD active-tone resolution the snapshot performs each frame: the chart's
/// own current tone (base tone before the first change, then the latest change),
/// the rig override when in-game tones drive, the enable toggle, and the flash
/// tick that bumps on a switch. Shares the "GameplayToneHud" collection with the
/// coordinator tests so the process-wide channel is never touched concurrently.
/// </summary>
[Collection("GameplayToneHud")]
public sealed class GameplayToneHudTests
{
    private static Arrangement CleanToHeavyArrangement()
    {
        var arr = new Arrangement { ToneBase = "clean" };
        arr.ToneChanges.Add(new ToneChange { Position = new FractionalPosition(4), ToneName = "heavy" });
        return arr;
    }

    private static SnapshotUpdateContext Ctx(Arrangement arr)
    {
        var beats = new List<BeatMarker>();
        for (var i = 0; i < 16; i++)
            beats.Add(new BeatMarker { Time = i * 1000, IsMeasure = i % 4 == 0 });
        return new SnapshotUpdateContext
        {
            ActiveBeats = beats,
            Arrangement = arr,
            Tones = arr.ToneChanges
        };
    }

    private static FrameSnapshot Build(SnapshotUpdateContext ctx, double timeMs)
    {
        var snap = new FrameSnapshot();
        FrameSnapshot.BuildSnapshot(timeMs - 1000, timeMs + 3000, timeMs, snap, ctx, []);
        return snap;
    }

    [Fact]
    public void BeforeFirstChange_ShowsBaseTone()
    {
        GameplayToneHud.Enabled = true;
        GameplayToneHud.ClearRig();

        var snap = Build(Ctx(CleanToHeavyArrangement()), 500); // before the beat-4 change (4000 ms)

        Assert.Equal("clean", snap.ActiveToneName);
        Assert.False(snap.ActiveToneFromRig);
    }

    [Fact]
    public void AfterAChange_ShowsTheChangeTone()
    {
        GameplayToneHud.Enabled = true;
        GameplayToneHud.ClearRig();

        var snap = Build(Ctx(CleanToHeavyArrangement()), 5000); // past the change

        Assert.Equal("heavy", snap.ActiveToneName);
    }

    [Fact]
    public void RigOverride_WinsOverChartTone_WhenDriving()
    {
        GameplayToneHud.Enabled = true;
        GameplayToneHud.SetRig("Rebuilt Lead", approximated: true);
        try
        {
            var snap = Build(Ctx(CleanToHeavyArrangement()), 500);

            Assert.Equal("Rebuilt Lead", snap.ActiveToneName);
            Assert.True(snap.ActiveToneFromRig);
            Assert.True(snap.ActiveToneApproximated);
        }
        finally
        {
            GameplayToneHud.ClearRig();
        }
    }

    [Fact]
    public void Disabled_ShowsNothing()
    {
        GameplayToneHud.ClearRig();
        GameplayToneHud.Enabled = false;
        try
        {
            var snap = Build(Ctx(CleanToHeavyArrangement()), 5000);
            Assert.Equal("", snap.ActiveToneName);
        }
        finally
        {
            GameplayToneHud.Enabled = true;
        }
    }

    [Fact]
    public void ChangedTick_BumpsOnAChange_ButNotOnASteadyTone()
    {
        GameplayToneHud.Enabled = true;
        GameplayToneHud.ClearRig();

        var ctx = Ctx(CleanToHeavyArrangement());
        var snap = new FrameSnapshot();

        FrameSnapshot.BuildSnapshot(-500, 3500, 500, snap, ctx, []);
        Assert.Equal("clean", snap.ActiveToneName);
        var cleanTick = snap.ActiveToneChangedTick;

        // Same tone next frame — the change tick must NOT move.
        FrameSnapshot.BuildSnapshot(500, 4500, 1500, snap, ctx, []);
        Assert.Equal("clean", snap.ActiveToneName);
        Assert.Equal(cleanTick, snap.ActiveToneChangedTick);

        // Let the coarse TickCount64 clock (~15 ms) advance strictly past the
        // clean tick so the switch frame is guaranteed a newer value, then
        // cross the change. (Real tone changes are seconds apart.)
        while (Environment.TickCount64 <= cleanTick) System.Threading.Thread.Sleep(1);
        FrameSnapshot.BuildSnapshot(4000, 8000, 5000, snap, ctx, []);
        Assert.Equal("heavy", snap.ActiveToneName);
        Assert.True(snap.ActiveToneChangedTick > cleanTick, "a tone switch must bump the flash tick");
    }

    [Fact]
    public void NoTones_ShowsNothing()
    {
        GameplayToneHud.Enabled = true;
        GameplayToneHud.ClearRig();

        var snap = Build(Ctx(new Arrangement()), 5000); // no base tone, no changes
        Assert.Equal("", snap.ActiveToneName);
    }
}
