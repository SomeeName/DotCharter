using DotCharter.Localization;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Localization;

// These tests mutate the process-wide LocalizationService.Instance (active
// culture + community packs). Serialize every test that does so into one
// collection so they can't race each other's culture switches.
[CollectionDefinition("LocalizationSingleton", DisableParallelization = true)]
public sealed class LocalizationSingletonCollection;

/// <summary>
///     Covers runtime community language packs (0.13.0): flat
///     <c>&lt;culture&gt;.json</c> key/value files loaded from a folder, robust to
///     malformed/partial input, with English fallback for missing keys.
///
///     The test packs deliberately define only invented keys (never a real resx
///     key), so switching <see cref="LocalizationService.Instance" /> to a test
///     culture cannot change any string another test might read. State is
///     restored in <c>finally</c>.
/// </summary>
[Collection("LocalizationSingleton")]
public sealed class CommunityLanguageTests
{
    private const string CommunityOnlyKey = "Test_Community_OnlyKey";

    [Fact]
    public void MalformedAndPartialPacks_DoNotCrash_AndOnlyValidOnesAppear()
    {
        var dir = NewTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "fr-FR.json"),
                $$"""{ "Language_DisplayName": "Français (test)", "{{CommunityOnlyKey}}": "salut" }""");
            File.WriteAllText(Path.Combine(dir, "broken.json"), "{ this is not valid json ");
            File.WriteAllText(Path.Combine(dir, "empty.json"), "{}");
            File.WriteAllText(Path.Combine(dir, "array.json"), "[1, 2, 3]");

            // Must not throw on any of the bad files.
            LocalizationService.Instance.LoadCommunityLanguages(dir);

            var options = LocalizationService.Instance.LanguageOptions;
            var fr = options.FirstOrDefault(o => o.ConfigValue == "fr-FR");
            Assert.NotNull(fr);
            Assert.Equal("Français (test)", fr!.DisplayName);

            // The four built-ins plus exactly one valid pack.
            Assert.Equal(5, options.Count);
        }
        finally
        {
            RestoreDefaults(dir);
        }
    }

    [Fact]
    public void ActiveCommunityCulture_UsesPackValue_AndFallsBackToEnglish()
    {
        var dir = NewTempDir();
        try
        {
            // Partial pack: one provided key, everything else must fall back.
            File.WriteAllText(Path.Combine(dir, "fr-FR.json"),
                $$"""{ "Language_DisplayName": "Français", "{{CommunityOnlyKey}}": "salut" }""");

            LocalizationService.Instance.LoadCommunityLanguages(dir);
            LocalizationService.Instance.SetConfiguredCulture("fr-FR");

            Assert.Equal("fr-FR", LocalizationService.Instance.Culture.Name);
            // Provided key resolves to the community value…
            Assert.Equal("salut", LocalizationService.Instance[CommunityOnlyKey]);
            // …a key the pack omits falls back to the English resx.
            Assert.Equal("Save", LocalizationService.Instance["Action_Save"]);
        }
        finally
        {
            RestoreDefaults(dir);
        }
    }

    [Fact]
    public void NonexistentFolder_LeavesOnlyBuiltInLanguages()
    {
        try
        {
            LocalizationService.Instance.LoadCommunityLanguages(
                Path.Combine(Path.GetTempPath(), "dotcharter-no-such-languages-dir-" + Guid.NewGuid().ToString("N")));

            var codes = LocalizationService.Instance.LanguageOptions.Select(o => o.ConfigValue).ToList();
            Assert.Equal(new[] { LocalizationService.SystemLanguage, "en-US", "de-DE", "ja-JP" }, codes);
        }
        finally
        {
            RestoreDefaults(null);
        }
    }

    /// <summary>
    ///     A pack may not shadow a built-in. <c>de-DE</c> and <c>ja-JP</c> are
    ///     fully translated in the resx and are what German / Japanese users
    ///     get; a dropped-in <c>de-DE.json</c> replacing one would be a silent,
    ///     unreviewable override of every string in the app.
    /// </summary>
    [Fact]
    public void APackNamedForABuiltInCulture_IsIgnoredEntirely()
    {
        var dir = NewTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "de-DE.json"),
                """{ "Language_DisplayName": "HIJACKED", "Action_Save": "HIJACKED" }""");
            File.WriteAllText(Path.Combine(dir, "en-US.json"),
                """{ "Action_Save": "HIJACKED" }""");
            File.WriteAllText(Path.Combine(dir, "ja-JP.json"),
                """{ "Language_DisplayName": "HIJACKED", "Action_Save": "HIJACKED" }""");

            LocalizationService.Instance.LoadCommunityLanguages(dir);

            var codes = LocalizationService.Instance.LanguageOptions.Select(o => o.ConfigValue).ToList();
            Assert.Equal(new[] { LocalizationService.SystemLanguage, "en-US", "de-DE", "ja-JP" }, codes);

            LocalizationService.Instance.SetConfiguredCulture("de-DE");
            Assert.Equal("Speichern", LocalizationService.Instance["Action_Save"]);

            LocalizationService.Instance.SetConfiguredCulture("ja-JP");
            Assert.Equal("保存", LocalizationService.Instance["Action_Save"]);
        }
        finally
        {
            RestoreDefaults(dir);
        }
    }

    /// <summary>
    ///     Every non-object JSON root, plus values that are not strings. A
    ///     translator's editor writes numbers and nested objects readily; none
    ///     of it may throw out of startup, and a file with no usable string at
    ///     all is not a language.
    /// </summary>
    [Fact]
    public void NonObjectRootsAndNonStringValues_AreSkippedWithoutThrowing()
    {
        var dir = NewTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "aa-AA.json"), "null");
            File.WriteAllText(Path.Combine(dir, "bb-BB.json"), "\"just a string\"");
            File.WriteAllText(Path.Combine(dir, "cc-CC.json"), "1234");
            File.WriteAllText(Path.Combine(dir, "dd-DD.json"), "true");
            File.WriteAllText(Path.Combine(dir, "ee-EE.json"), "");
            // An object, but not one usable string in it.
            File.WriteAllText(Path.Combine(dir, "ff-FF.json"),
                """{ "A": 1, "B": { "x": "y" }, "C": [1, 2], "D": null, "E": false }""");
            // Mixed: the string entries survive, the rest are dropped.
            File.WriteAllText(Path.Combine(dir, "gg-GG.json"),
                $$"""{ "{{CommunityOnlyKey}}": "kept", "Dropped": 7, "AlsoDropped": { "n": 1 } }""");

            LocalizationService.Instance.LoadCommunityLanguages(dir);

            var codes = LocalizationService.Instance.LanguageOptions.Select(o => o.ConfigValue).ToList();
            Assert.Equal(
                new[] { LocalizationService.SystemLanguage, "en-US", "de-DE", "ja-JP", "gg-GG" }, codes);

            LocalizationService.Instance.SetConfiguredCulture("gg-GG");
            Assert.Equal("kept", LocalizationService.Instance[CommunityOnlyKey]);
            // A key the pack "defines" as a number never entered the map, so it
            // falls through to the resx miss (the key itself) rather than
            // rendering "7" or throwing.
            Assert.Equal("Dropped", LocalizationService.Instance["Dropped"]);
        }
        finally
        {
            RestoreDefaults(dir);
        }
    }

    /// <summary>
    ///     A filename that is not a culture code at all. It must not throw, must
    ///     not disturb the built-ins, and must still be SELECTABLE without
    ///     throwing — <c>ResolveCulture</c> and <c>IsRightToLeft</c> both run on
    ///     whatever <c>CultureInfo</c> the pack ended up with.
    /// </summary>
    [Theory]
    [InlineData("not-a-culture")]
    [InlineData("readme")]
    [InlineData("123")]
    public void AFilenameThatIsNotACultureCode_NeverThrows(string code)
    {
        var dir = NewTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, code + ".json"),
                $$"""{ "{{CommunityOnlyKey}}": "value" }""");

            LocalizationService.Instance.LoadCommunityLanguages(dir);

            // The built-ins are always first and always intact.
            var codes = LocalizationService.Instance.LanguageOptions.Select(o => o.ConfigValue).ToList();
            Assert.Equal(
                new[] { LocalizationService.SystemLanguage, "en-US", "de-DE", "ja-JP" }, codes.Take(4));

            // Selecting it (from a stale config value, say) must not throw
            // either, and the app must still resolve strings afterwards.
            LocalizationService.Instance.SetConfiguredCulture(code);
            _ = LocalizationService.Instance.IsRightToLeft;
            Assert.False(string.IsNullOrEmpty(LocalizationService.Instance["Action_Save"]));
        }
        finally
        {
            RestoreDefaults(dir);
        }
    }

    /// <summary>An empty, whitespace or plainly bogus folder argument. Called on
    /// the startup path, so it may not throw for any of them.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("::not a path::")]
    public void ABogusFolderArgument_IsIgnoredWithoutThrowing(string folder)
    {
        try
        {
            LocalizationService.Instance.LoadCommunityLanguages(folder);

            var codes = LocalizationService.Instance.LanguageOptions.Select(o => o.ConfigValue).ToList();
            Assert.Equal(new[] { LocalizationService.SystemLanguage, "en-US", "de-DE", "ja-JP" }, codes);
        }
        finally
        {
            RestoreDefaults(null);
        }
    }

    /// <summary>
    ///     <c>IsRightToLeft</c> is what the shell binds <c>FlowDirection</c> to,
    ///     and for a community pack it can only come from the pack's own culture
    ///     — no resx entry says it.
    /// </summary>
    [Fact]
    public void IsRightToLeft_FollowsAnRtlPack_AndComesBack()
    {
        var dir = NewTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "he-IL.json"),
                $$"""{ "{{CommunityOnlyKey}}": "שלום" }""");

            LocalizationService.Instance.LoadCommunityLanguages(dir);
            LocalizationService.Instance.SetConfiguredCulture("he-IL");
            Assert.True(LocalizationService.Instance.IsRightToLeft);

            LocalizationService.Instance.SetConfiguredCulture("en-US");
            Assert.False(LocalizationService.Instance.IsRightToLeft);
        }
        finally
        {
            RestoreDefaults(dir);
        }
    }

    /// <summary>
    ///     Reloading is a REPLACE, not a merge — including for the pack that is
    ///     currently active. Until 0.58 <c>_activeCommunity</c> was left pointing
    ///     at the old <c>CommunityLanguage</c>, so a pack deleted from the folder
    ///     went on supplying every string it defined until the app restarted.
    /// </summary>
    [Fact]
    public void ReloadingWithoutTheActivePack_StopsServingItsStrings()
    {
        var dir = NewTempDir();
        try
        {
            var pack = Path.Combine(dir, "fr-FR.json");
            File.WriteAllText(pack, $$"""{ "{{CommunityOnlyKey}}": "salut" }""");

            LocalizationService.Instance.LoadCommunityLanguages(dir);
            LocalizationService.Instance.SetConfiguredCulture("fr-FR");
            Assert.Equal("salut", LocalizationService.Instance[CommunityOnlyKey]);

            // The user deletes the pack and the folder is rescanned.
            File.Delete(pack);
            LocalizationService.Instance.LoadCommunityLanguages(dir);

            Assert.Equal(CommunityOnlyKey, LocalizationService.Instance[CommunityOnlyKey]);
            Assert.DoesNotContain(LocalizationService.Instance.LanguageOptions,
                o => o.ConfigValue == "fr-FR");
        }
        finally
        {
            RestoreDefaults(dir);
        }
    }

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dotcharter-lang-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void RestoreDefaults(string? tempDir)
    {
        LocalizationService.Instance.LoadCommunityLanguages(
            Path.Combine(Path.GetTempPath(), "dotcharter-cleanup-" + Guid.NewGuid().ToString("N")));
        LocalizationService.Instance.SetConfiguredCulture("en-US");

        if (tempDir != null)
            BestEffortDelete.Directory(tempDir);
    }
}
