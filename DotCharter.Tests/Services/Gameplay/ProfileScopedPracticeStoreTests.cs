using DotCharter.Services.Audio.LivePlay;
using DotCharter.Services.Gameplay.Profiles;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Gameplay;

/// <summary>
///     PracticeProfileService in player-profile mode: per-profile stores,
///     store reload on profile switch, and one-time migration of the
///     pre-profile (0.10) practice-profiles.json into the default profile.
/// </summary>
public sealed class ProfileScopedPracticeStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "DotCharterTests", Guid.NewGuid().ToString("N"));

    private string LegacyPath => Path.Combine(_dir, "practice-profiles.json");

    private PlayerProfileService NewPlayers()
        => new(FakeLocalization.English(), logger: null, filePath: Path.Combine(_dir, "player-profiles.json"));

    private PracticeProfileService NewScoped(IPlayerProfileService players)
        => new(players, logger: null, legacyFilePath: LegacyPath);

    private static LivePlaySessionResult Result(long score = 1000)
        => new(score, 0.8, 8, 2, 20, 5, 60, 100, 0.5);

    private void WriteLegacyStore(long score)
        => new PracticeProfileService(logger: null, filePath: LegacyPath).Record("song", "lead", Result(score));

    [Fact]
    public void LegacyStore_IsMovedIntoDefaultProfile()
    {
        WriteLegacyStore(score: 4321);
        var players = NewPlayers();

        var profile = NewScoped(players).GetProfile("song", "lead");

        Assert.Equal(4321, profile.BestScore);
        Assert.False(File.Exists(LegacyPath));
        Assert.True(File.Exists(Path.Combine(
            players.GetDataDirectory(players.ActiveProfile.Id), "practice-profiles.json")));
    }

    [Fact]
    public void LegacyStore_IsNotMigratedIntoNonDefaultProfile()
    {
        WriteLegacyStore(score: 4321);
        var players = NewPlayers();
        var second = players.Create("Zwei");
        players.SwitchTo(second.Id);

        var profile = NewScoped(players).GetProfile("song", "lead");

        Assert.Equal(0, profile.BestScore);
        Assert.True(File.Exists(LegacyPath));
    }

    [Fact]
    public void ProfileSwitch_IsolatesScoresPerProfile()
    {
        var players = NewPlayers();
        var scoped = NewScoped(players);
        var second = players.Create("Zwei");

        scoped.Record("song", "lead", Result(score: 111));
        players.SwitchTo(second.Id);

        Assert.Equal(0, scoped.GetProfile("song", "lead").BestScore);
        scoped.Record("song", "lead", Result(score: 222));
        Assert.Equal(222, scoped.GetProfile("song", "lead").BestScore);

        players.SwitchTo(players.Profiles.First(p => p.IsDefault).Id);
        Assert.Equal(111, scoped.GetProfile("song", "lead").BestScore);
    }

    [Fact]
    public void MigratedStore_AcceptsNewSessions()
    {
        WriteLegacyStore(score: 100);
        var players = NewPlayers();
        var scoped = NewScoped(players);

        var profile = scoped.Record("song", "lead", Result(score: 500));

        Assert.Equal(500, profile.BestScore);
        Assert.Equal(2, profile.Sessions.Count);
        Assert.False(File.Exists(LegacyPath));
    }

    public void Dispose() => BestEffortDelete.Directory(_dir);
}
