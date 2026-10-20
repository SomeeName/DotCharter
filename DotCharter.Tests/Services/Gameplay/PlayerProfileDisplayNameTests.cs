using DotCharter.Services.Gameplay.Profiles;

namespace DotCharter.Tests.Services.Gameplay;

/// <summary>
///     The default profile has no custom name and renders the localized "Player 1" at display
///     time (owner, 2026-09-22): a German first run stored "Spieler 1" and showed it in an
///     English UI ever after.
/// </summary>
public sealed class PlayerProfileDisplayNameTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dotcharter-profile-names-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string RegistryPath => Path.Combine(_dir, "player-profiles.json");

    [Fact]
    public void A_fresh_default_profile_has_no_custom_name_and_reads_localized()
    {
        var service = new PlayerProfileService(FakeLocalization.KeysOnly(), filePath: RegistryPath);

        var profile = Assert.Single(service.Profiles);
        Assert.False(profile.HasCustomName);
        Assert.Equal("Profile_DefaultName", profile.DisplayName);
    }

    [Fact]
    public void In_english_a_nameless_profile_reads_player_1()
    {
        var profile = Assert.Single(new PlayerProfileService(FakeLocalization.English(), filePath: RegistryPath).Profiles);

        Assert.False(profile.HasCustomName);
        Assert.Equal("Player 1", profile.DisplayName);
    }

    [Theory]
    [InlineData("Spieler 1")]
    [InlineData("Player 1")]
    [InlineData("プレイヤー1")]
    public void A_stored_default_name_in_any_language_becomes_no_custom_name(string stored)
    {
        var id = Guid.NewGuid();
        File.WriteAllText(RegistryPath,
            $$"""{"ActiveProfileId":"{{id}}","Profiles":[{"Id":"{{id}}","Name":"{{stored}}","IsDefault":true}]}""");

        var profile = Assert.Single(new PlayerProfileService(FakeLocalization.KeysOnly(), filePath: RegistryPath).Profiles);

        Assert.False(profile.HasCustomName);
        Assert.Equal("Profile_DefaultName", profile.DisplayName);
    }

    [Fact]
    public void A_name_the_player_chose_is_kept_and_a_second_player_1_is_numbered()
    {
        var service = new PlayerProfileService(FakeLocalization.KeysOnly(), filePath: RegistryPath);
        var ana = service.Create("Ana");

        Assert.True(ana.HasCustomName);
        Assert.Equal("Ana", ana.DisplayName);
        // The default profile READS "Player 1", so the import rule numbers a clash against it.
        Assert.Contains(service.Profiles, p => !p.HasCustomName);
    }
}
