using System.Globalization;
using System.Xml.Linq;

namespace DotCharter.Tests.Infrastructure;

/// <summary>
///     The localization fake answers like the real service under en-US, or with the key
///     itself, and never moves the process culture. One case calls
///     <c>SetConfiguredCulture</c> on the FAKE to prove that the call leaves the ambient
///     culture alone; <c>CultureCollectionLintTests</c> lists the file as an
///     instance-scoped caller, so the class needs no collection.
/// </summary>
public sealed class FakeLocalizationTests
{
    [Fact]
    public void English_AnswersWithTheInvariantResxValue()
    {
        var fake = FakeLocalization.English();

        Assert.Equal(ResxValue("Project_Untitled"), fake["Project_Untitled"]);
        Assert.Equal(ResxValue("Stem_Drums"), fake["Stem_Drums"]);
    }

    [Fact]
    public void English_ResolvesAMissingKeyToItsName_AndABlankKeyToEmpty()
    {
        var fake = FakeLocalization.English();

        Assert.Equal("No_Such_Key_Anywhere", fake["No_Such_Key_Anywhere"]);
        Assert.Equal(string.Empty, fake["  "]);
    }

    [Fact]
    public void Plural_ChoosesTheFormByCount_AndPassesTheCountAsTheFirstArgument()
    {
        var fake = FakeLocalization.English();

        Assert.Equal("1 note", fake.Plural("SongBrowser_NoteCountOne", "SongBrowser_NoteCount", 1));
        Assert.Equal("4 notes", fake.Plural("SongBrowser_NoteCountOne", "SongBrowser_NoteCount", 4));
        Assert.Equal("Ready to load — 3 · Lead",
            fake.Plural("SongBrowser_Load_Ready", "SongBrowser_Load_Ready", 3, "Lead"));
    }

    [Fact]
    public void PluralWith_LetsTheCountOnlyChoose()
    {
        var fake = FakeLocalization.English();

        Assert.Equal("½ note", fake.PluralWith("SongBrowser_NoteCountOne", "SongBrowser_NoteCount", 1, "½"));
        Assert.Equal("2½ notes", fake.PluralWith("SongBrowser_NoteCountOne", "SongBrowser_NoteCount", 2, "2½"));
    }

    [Fact]
    public void Format_UsesTheFixedFormatCulture_NotTheAmbientOne()
    {
        Assert.Equal("1.5 notes", FakeLocalization.English().Format("SongBrowser_NoteCount", 1.5));
        Assert.Equal("1,5 notes",
            FakeLocalization.English(CultureInfo.GetCultureInfo("de-DE")).Format("SongBrowser_NoteCount", 1.5));
    }

    [Fact]
    public void KeysOnly_AnswersWithTheKey_AndAppendsFormattedArguments()
    {
        var fake = FakeLocalization.KeysOnly();

        Assert.Equal("Project_Untitled", fake["Project_Untitled"]);
        Assert.Equal("Project_Untitled", fake.Format("Project_Untitled"));
        Assert.Equal("SongBrowser_Load_Ready(1.5, Lead)", fake.Format("SongBrowser_Load_Ready", 1.5, "Lead"));
        Assert.Equal("SongBrowser_NoteCountOne(1)", fake.Plural("SongBrowser_NoteCountOne", "SongBrowser_NoteCount", 1));
        Assert.Equal("SongBrowser_NoteCount(4, x)",
            fake.Plural("SongBrowser_NoteCountOne", "SongBrowser_NoteCount", 4, "x"));
        Assert.Equal("SongBrowser_NoteCount(2½)",
            fake.PluralWith("SongBrowser_NoteCountOne", "SongBrowser_NoteCount", 2, "2½"));
    }

    [Fact]
    public void SetConfiguredCulture_IsRecorded_ButMovesNeitherTheStringsNorTheProcessCulture()
    {
        var fake = FakeLocalization.English();
        var defaultCulture = CultureInfo.DefaultThreadCurrentCulture;
        var defaultUiCulture = CultureInfo.DefaultThreadCurrentUICulture;
        var current = CultureInfo.CurrentCulture;
        var currentUi = CultureInfo.CurrentUICulture;
        var raised = new List<string?>();
        fake.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        fake.SetConfiguredCulture("de-DE");

        Assert.Equal("de-DE", fake.ConfiguredCulture);
        Assert.Equal("ConfiguredCulture", Assert.Single(raised));
        Assert.Equal(ResxValue("Project_Untitled"), fake["Project_Untitled"]);
        Assert.Equal("en-US", fake.Culture.Name);
        Assert.False(fake.IsRightToLeft);
        Assert.Same(defaultCulture, CultureInfo.DefaultThreadCurrentCulture);
        Assert.Same(defaultUiCulture, CultureInfo.DefaultThreadCurrentUICulture);
        Assert.Same(current, CultureInfo.CurrentCulture);
        Assert.Same(currentUi, CultureInfo.CurrentUICulture);
    }

    [Fact]
    public void ABlankConfiguredCulture_IsTheSystemLanguage_AndCommunityPacksAreIgnored()
    {
        var fake = FakeLocalization.English();

        fake.SetConfiguredCulture(null);
        fake.LoadCommunityLanguages(Path.Combine(Path.GetTempPath(), "no-such-language-folder"));

        Assert.Equal(DotCharter.Localization.LocalizationService.SystemLanguage, fake.ConfiguredCulture);
        Assert.Empty(fake.LanguageOptions);
    }

    private static string ResxValue(string key)
    {
        var resx = XDocument.Load(Path.Combine(TestPaths.RepoRoot(), "DotCharter.Localization", "Resources.resx"));
        return resx.Root!.Elements("data")
            .Single(d => (string?)d.Attribute("name") == key)
            .Element("value")!.Value;
    }
}
