using DotCharter.Services.Export;

namespace DotCharter.Tests.Services.Export;

/// <summary>#747: the Rocksmith DLC key is ASCII <c>[A-Za-z0-9]</c>, stable and unique.</summary>
public sealed class RocksmithDlcKeyTests
{
    private static bool IsAsciiKey(string key) => key.Length >= RocksmithDlcKey.MinimumLength && key.All(char.IsAsciiLetterOrDigit);

    [Fact]
    public void Japanese_title_gives_an_ascii_key_that_is_stable_and_unique()
    {
        var first = RocksmithDlcKey.Create("Ave Mujica", "ザホールブルーワールド");
        var again = RocksmithDlcKey.Create("Ave Mujica", "ザホールブルーワールド");
        var other = RocksmithDlcKey.Create("Ave Mujica", "黒のバースデイ");

        Assert.True(IsAsciiKey(first), first);
        Assert.True(IsAsciiKey(other), other);
        Assert.StartsWith("DCAveMujica", first, StringComparison.Ordinal);
        Assert.Equal(first, again);
        Assert.NotEqual(first, other);
    }

    [Fact]
    public void All_japanese_artist_and_title_still_give_a_valid_stable_key()
    {
        var key = RocksmithDlcKey.Create("米津玄師", "レモン");

        Assert.True(IsAsciiKey(key), key);
        Assert.Equal(key, RocksmithDlcKey.Create("米津玄師", "レモン"));
        Assert.NotEqual(key, RocksmithDlcKey.Create("米津玄師", "感電"));
    }

    [Fact]
    public void Latin_diacritics_fold_to_their_ascii_letters()
    {
        Assert.Equal("DCBeyonceHalo", RocksmithDlcKey.Create("Beyoncé", "Halo"));
        Assert.Equal("DCMotorheadAceofSpades", RocksmithDlcKey.Create("Motörhead", "Ace of Spades"));
        Assert.Equal("DCSigurRosHoppipolla", RocksmithDlcKey.Create("Sigur Rós", "Hoppípolla"));
        Assert.Equal("DCDieArzteSchrei", RocksmithDlcKey.Create("Die Ärzte", "Schrei"));
    }

    [Fact]
    public void Plain_ascii_keys_are_unchanged()
    {
        // The pre-#747 derivation, spelled out: DC + 10 artist + 12 title alphanumerics.
        Assert.Equal("DCDotCharterGoldenTestSo", RocksmithDlcKey.Create("DotCharter", "Golden Test Song"));
        Assert.Equal("DCACDCBackInBlack", RocksmithDlcKey.Create("AC/DC", "Back In Black"));
    }

    [Fact]
    public void A_too_short_ascii_key_is_padded_reproducibly()
    {
        var key = RocksmithDlcKey.Create("", "A");

        Assert.True(IsAsciiKey(key), key);
        Assert.Equal(key, RocksmithDlcKey.Create("", "A"));
    }

    [Theory]
    [InlineData("MyKey01", false)]
    [InlineData("My-Key_01", false)]
    [InlineData("abc", true)]
    [InlineData("DCAveMujicaザホール", true)]
    [InlineData("DCBeyoncé1", true)]
    [InlineData("", false)]
    public void A_typed_key_is_invalid_when_short_or_not_ascii(string key, bool invalid) =>
        Assert.Equal(invalid, RocksmithDlcKey.IsInvalidTypedKey(key));

    [Theory]
    [InlineData("Golden Test Song", false)]
    [InlineData("Beyoncé – Déjà Vu", false)]
    [InlineData("Motörhead’s “Ace”", false)]
    [InlineData("ザホールブルーワールド", true)]
    [InlineData("Ave Mujica 黒", true)]
    [InlineData("Кино", true)]
    [InlineData("", false)]
    public void Display_text_outside_the_game_font_is_reported(string value, bool lacks) =>
        Assert.Equal(lacks, RocksmithDlcKey.LacksGameGlyphs(value));

    [Fact]
    public void Entry_name_guard_refuses_non_ascii_and_names_not_from_the_key()
    {
        RocksmithPackageBuilder.EnsureEntryNamesFromKey(
            ["songs/bin/generic/dcabcdef_lead.sng", "audio/windows/12345.wem", "appid.appid"], "dcabcdef");

        Assert.Throws<InvalidOperationException>(() =>
            RocksmithPackageBuilder.EnsureEntryNamesFromKey(["songs/bin/generic/dcabcdefザ_lead.sng"], "dcabcdefザ"));
        Assert.Throws<InvalidOperationException>(() =>
            RocksmithPackageBuilder.EnsureEntryNamesFromKey(["songs/bin/generic/other_lead.sng"], "dcabcdef"));
    }
}
