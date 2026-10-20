using DotCharter.Services.Infrastructure.Psarc;

namespace DotCharter.Tests.Services.Configuration;

/// <summary>The tests of <see cref="AppConfigRocksmithKeysTests" /> that need no package reader; its fixtures are in <c>AppConfigRocksmithKeysTests.Fixtures.cs</c>.</summary>
public sealed partial class AppConfigRocksmithKeysTests
{
    [Fact]
    public void A_fresh_config_is_written_with_the_three_EMPTY_key_fields()
    {
        var service = NewService();
        service.SaveAppConfig(service.LoadAppConfig());
        service.Flush();

        var keys = SavedKeys();
        Assert.Equal(["PsarcToc", "SngPc", "SngMac"], keys.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.All(keys.EnumerateObject(), p => Assert.Equal(string.Empty, p.Value.GetString()));
    }

    [Fact]
    public void A_config_from_before_774_or_with_a_null_section_gains_the_empty_fields_on_its_next_save()
    {
        WriteConfig("""{ "LanguageCulture": "de-DE", "RocksmithKeys": null }""");
        var service = NewService();
        Assert.NotNull(service.LoadAppConfig().RocksmithKeys);
        service.Update(_ => { });
        service.Flush();

        Assert.Equal(string.Empty, SavedKeys().GetProperty("SngMac").GetString());
        Assert.Equal("de-DE", service.LoadAppConfig().LanguageCulture);
    }

    [Fact]
    public void The_app_builds_its_one_configuration_service_as_the_key_source()
    {
        // The app's field is built by ForApplication, and ForApplication is what wires the
        // keys (UseConfigurationForTesting above exercises the same SourceFor path).
        var app = File.ReadAllText(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "App.axaml.cs"));
        Assert.Contains("_configService = JsonConfigurationService.ForApplication();", app, StringComparison.Ordinal);
        var service = File.ReadAllText(Path.Combine(TestPaths.RepoRoot(), "DotCharter.Services", "Infrastructure",
            "Configuration", "JsonConfigurationService.cs"));
        Assert.Contains("RocksmithKeys.UseConfiguration(service);", service, StringComparison.Ordinal);

        // …and in a test process it cannot repoint the pinned keys at a config on disk.
        // (This temp config has no keys, so a repointed source would read none.)
        var before = RocksmithKeys.Current!.SngPcKey;
        _services.Add(JsonConfigurationService.ForApplication());
        Assert.NotNull(before);
        Assert.Equal(before, RocksmithKeys.Current?.SngPcKey);
    }

    [Fact]
    public void A_malformed_value_is_ignored_and_logged_by_name_at_info_without_any_key_text()
    {
        const string malformed = "ZZ" + "0123456789abcdef";
        WriteConfig(KeysJson(malformed, PcHex, string.Empty));
        var service = NewService();
        using var _ = RocksmithKeys.UseConfigurationForTesting(service);
        service.LoadAppConfig();
        var logger = new FakeAppLogger();
        service.Logger = logger; // the load ran first: the note is replayed

        Assert.Null(RocksmithKeys.Current!.TocKey);
        Assert.Equal(TestRocksmithKeys.SyntheticSngPc, RocksmithKeys.Current.SngPcKey);

        var note = Assert.Single(logger.Entries, e => e.Message.Contains("RocksmithKeys", StringComparison.Ordinal));
        Assert.Equal("INFO", note.Level); // a warning would reach the status bar; the keys have no UI
        Assert.Contains("RocksmithKeys.PsarcToc", note.Message, StringComparison.Ordinal);
        Assert.All(logger.Entries, e =>
        {
            Assert.DoesNotContain("0123456789", e.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(PcHex, e.Message, StringComparison.OrdinalIgnoreCase);
        });
        // The value stays in the file as the user typed it — ignored, never rewritten.
        Assert.Equal(malformed, service.LoadAppConfig().RocksmithKeys.PsarcToc);
    }

    [Fact]
    public void The_old_key_file_is_moved_into_appconfig_once_and_then_deleted()
    {
        File.WriteAllText(LegacyPath, TestRocksmithKeys.LegacyKeyFileText(
            TestRocksmithKeys.SyntheticToc, TestRocksmithKeys.SyntheticSngPc, TestRocksmithKeys.SyntheticSngMac));

        var service = NewService();
        var keys = service.LoadAppConfig().RocksmithKeys;

        Assert.Equal((TocHex, PcHex, MacHex), (keys.PsarcToc, keys.SngPc, keys.SngMac));
        Assert.False(File.Exists(LegacyPath)); // deleted only after the write below landed
        var saved = SavedKeys();
        Assert.Equal(TocHex, saved.GetProperty("PsarcToc").GetString());
        Assert.Equal(MacHex, saved.GetProperty("SngMac").GetString());

        using var _ = RocksmithKeys.UseConfigurationForTesting(service);
        Assert.Equal(TestRocksmithKeys.SyntheticSngPc, RocksmithKeys.Current!.SngPcKey);
    }

    [Fact]
    public void A_key_already_in_appconfig_wins_over_the_old_file_and_only_empty_fields_are_filled()
    {
        var otherToc = Convert.ToHexString(TestRocksmithKeys.SyntheticToc.Select(b => (byte)~b).ToArray());
        WriteConfig(KeysJson(otherToc, string.Empty, string.Empty));
        File.WriteAllText(LegacyPath, TestRocksmithKeys.LegacyKeyFileText(
            TestRocksmithKeys.SyntheticToc, TestRocksmithKeys.SyntheticSngPc, null));

        var keys = NewService().LoadAppConfig().RocksmithKeys;

        Assert.Equal((otherToc, PcHex, string.Empty), (keys.PsarcToc, keys.SngPc, keys.SngMac));
        Assert.False(File.Exists(LegacyPath));
    }

    [Fact]
    public void A_malformed_old_file_is_left_in_place_and_named_without_its_content()
    {
        File.WriteAllText(LegacyPath, "psarc-toc = " + TocHex + "\nsng-pc = 00112233\n");
        var service = NewService();
        var keys = service.LoadAppConfig().RocksmithKeys;
        var logger = new FakeAppLogger();
        service.Logger = logger;

        Assert.False(keys.HasAnyValue()); // all or nothing: a half-read file migrates nothing
        Assert.True(File.Exists(LegacyPath));
        var note = Assert.Single(logger.Entries, e => e.Message.Contains("rocksmith-keys.txt", StringComparison.Ordinal));
        Assert.Equal("INFO", note.Level);
        Assert.Contains("line 2", note.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(TocHex, note.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("00112233", note.Message, StringComparison.Ordinal);
    }
}
