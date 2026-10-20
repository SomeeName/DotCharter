using DotCharter.Services.Gameplay;
using DotCharter.UI.ViewModels.PlayMode;

namespace DotCharter.Tests.UI.ViewModels.PlayMode;

/// <summary>
///     0.21 dynamic backing mix wiring: the run-scoped stem gain ducks on a miss,
///     swells back on a streak, and — the load-bearing invariant — is restored to
///     the stem's base volume on Stop(), which the owner calls on every exit path.
///     Driven manually (driveWithTimer: false) so no Avalonia dispatcher is needed.
/// </summary>
public sealed class StemDuckFollowerTests
{
    private const string Stem = "guitar";

    private static StemDuckFollower NewFollower(FakeAudioPlaybackService audio)
        => new(audio, driveWithTimer: false);

    [Fact]
    public void Start_AtZeroStreak_DucksToFloorOfBaseVolume()
    {
        var audio = new FakeAudioPlaybackService();
        var follower = NewFollower(audio);

        follower.Start(Stem, baseVolume: () => 1.0, streak: () => 0);

        Assert.True(follower.IsActive);
        // Seeded at the streak target immediately — no first-frame jump from unity.
        Assert.Equal(StemDuckMath.DuckFloor, audio.StemVolumes[Stem], 6);
    }

    [Fact]
    public void Ticking_WithAStreak_SwellsBackTowardBaseVolume()
    {
        var audio = new FakeAudioPlaybackService();
        var follower = NewFollower(audio);
        follower.Start(Stem, () => 1.0, () => StemDuckMath.SwellStreak); // full-swell streak

        for (var i = 0; i < 200; i++) follower.Tick(20);

        Assert.True(audio.StemVolumes[Stem] > 0.99, $"did not swell: {audio.StemVolumes[Stem]}");
    }

    [Fact]
    public void Ticking_AfterAMiss_DucksBackDown()
    {
        var audio = new FakeAudioPlaybackService();
        var streak = StemDuckMath.SwellStreak;
        var follower = NewFollower(audio);
        follower.Start(Stem, () => 1.0, () => streak);
        for (var i = 0; i < 200; i++) follower.Tick(20); // swelled to ~1.0

        streak = 0; // the player misses — streak resets
        for (var i = 0; i < 200; i++) follower.Tick(20);

        Assert.Equal(StemDuckMath.DuckFloor, audio.StemVolumes[Stem], 3);
    }

    [Fact]
    public void Gain_RidesOnTopOfTheLiveBaseVolume()
    {
        var audio = new FakeAudioPlaybackService();
        var baseVol = 0.5; // a mixer level the player set
        var follower = NewFollower(audio);
        follower.Start(Stem, () => baseVol, () => 0);

        Assert.Equal(0.5 * StemDuckMath.DuckFloor, audio.StemVolumes[Stem], 6);
    }

    [Fact]
    public void Stop_RestoresTheBaseVolume_AndGoesInactive()
    {
        var audio = new FakeAudioPlaybackService();
        var follower = NewFollower(audio);
        follower.Start(Stem, () => 0.8, () => 0); // ducked to 0.8 * floor
        Assert.NotEqual(0.8, audio.StemVolumes[Stem], 6);

        follower.Stop();

        Assert.False(follower.IsActive);
        Assert.Equal(0.8, audio.StemVolumes[Stem], 6); // back to base, run-scoped gain gone
    }

    [Fact]
    public void Stop_IsIdempotent()
    {
        var audio = new FakeAudioPlaybackService();
        var follower = NewFollower(audio);
        follower.Start(Stem, () => 1.0, () => 0);

        follower.Stop();
        follower.Stop(); // must not throw or re-touch anything

        Assert.False(follower.IsActive);
        Assert.Equal(1.0, audio.StemVolumes[Stem], 6);
    }

    [Fact]
    public void TickAfterStop_DoesNothing()
    {
        var audio = new FakeAudioPlaybackService();
        var follower = NewFollower(audio);
        follower.Start(Stem, () => 1.0, () => StemDuckMath.SwellStreak);
        follower.Stop();
        var restored = audio.StemVolumes[Stem];

        follower.Tick(20); // inactive — no-op

        Assert.Equal(restored, audio.StemVolumes[Stem], 12);
    }

    [Fact]
    public void Restart_RestoresThePreviousStemFirst()
    {
        var audio = new FakeAudioPlaybackService();
        var follower = NewFollower(audio);
        follower.Start("guitar", () => 1.0, () => 0);
        follower.Start("bass", () => 1.0, () => 0); // switch target

        // The first stem was restored (Start calls Stop internally), so guitar is
        // back at base while bass is now the ducked one.
        Assert.Equal(1.0, audio.StemVolumes["guitar"], 6);
        Assert.Equal(StemDuckMath.DuckFloor, audio.StemVolumes["bass"], 6);
    }

    [Fact]
    public void SeveralStems_DuckTogether_EachOverItsOwnBase_AndAllAreRestored()
    {
        // Guitar roles (#260): the guitar is two stems, Lead and Rhythm, and the
        // player's misses must duck BOTH — ducking one would leave half the guitar.
        var audio = new FakeAudioPlaybackService();
        var follower = NewFollower(audio);

        follower.Start([("guitar-lead", () => 1.0), ("guitar-rhythm", () => 0.5)], () => 0);

        Assert.Equal(StemDuckMath.DuckFloor, audio.StemVolumes["guitar-lead"], 6);
        Assert.Equal(0.5 * StemDuckMath.DuckFloor, audio.StemVolumes["guitar-rhythm"], 6);

        follower.Stop();
        Assert.Equal(1.0, audio.StemVolumes["guitar-lead"], 6);
        Assert.Equal(0.5, audio.StemVolumes["guitar-rhythm"], 6);
    }

    [Fact]
    public void NullAudio_IsInert()
    {
        var follower = new StemDuckFollower(audio: null, driveWithTimer: false);
        follower.Start(Stem, () => 1.0, () => 0);
        follower.Tick(20);
        follower.Stop();

        Assert.False(follower.IsActive); // never armed without an engine
    }
}
