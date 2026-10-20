using DotCharter.Localization;

namespace DotCharter.Tests.UI.Onboarding;

/// <summary>
///     The Welcome page's language commit, which used to change the app's
///     language on a page the user never touched.
///
///     <para>
///         <c>LanguageIndexOf</c> matches <c>LanguageOption.ConfigValue</c>
///         EXACTLY and falls back to index 0 — "System". So a config naming a
///         community pack that has since been removed from the languages folder
///         (or any hand-written value, or a bare <c>"de"</c>) seeded the picker
///         at System, and pressing Next then wrote <c>"system"</c> back to
///         <c>AppConfig.LanguageCulture</c> AND called
///         <c>SetConfiguredCulture</c> with it. Committing an untouched page must
///         never differ from the config that seeded it.
///     </para>
/// </summary>
/// <remarks>The rig hands the wizard the process-wide LocalizationService, and a
/// Next off the Welcome page reaches SetConfiguredCulture — a culture write
/// through product code races exactly as a direct one does. See
/// <c>CultureCollectionLintTests</c>.</remarks>
[Collection("LocalizationSingleton")]
public sealed class OnboardingWelcomeLanguageTests
{
    /// <summary>The bug. Next on a Welcome page nobody touched leaves the
    /// configured culture exactly as it found it — even one this build's picker
    /// cannot show.</summary>
    [Fact]
    public void ACultureThePickerCannotShow_SurvivesAnUntouchedNext()
    {
        var rig = new OnboardingWizardRig();
        rig.Config.AppConfig.LanguageCulture = "xx-Pirate";

        rig.Vm.Open();
        // The picker has nothing better to show than System for it...
        Assert.Equal(0, rig.Vm.SelectedLanguageIndex);
        rig.Vm.Next();

        // ...but "nothing better to show" is not a decision the user made.
        Assert.Equal("xx-Pirate", rig.Config.AppConfig.LanguageCulture);
    }

    /// <summary>A culture the picker DOES list survives just as it always did —
    /// the seeded index round-trips.</summary>
    [Fact]
    public void AListedCulture_SurvivesAnUntouchedNext()
    {
        var rig = new OnboardingWizardRig();
        rig.Config.AppConfig.LanguageCulture = "en-US";

        rig.Vm.Open();
        rig.Vm.Next();

        Assert.Equal("en-US", rig.Config.AppConfig.LanguageCulture);
    }

    /// <summary>
    ///     …and a language the user actually PICKS is still committed. Without
    ///     this the two above would pass against a <c>CommitWelcome</c> that
    ///     wrote no language at all.
    /// </summary>
    [Fact]
    public void ALanguageTheUserPicks_IsStillWritten()
    {
        var rig = new OnboardingWizardRig();
        rig.Config.AppConfig.LanguageCulture = "xx-Pirate";
        var english = IndexOf("en-US");

        rig.Vm.Open();
        rig.Vm.SelectedLanguageIndex = english;
        rig.Vm.Next();

        Assert.Equal("en-US", rig.Config.AppConfig.LanguageCulture);
    }

    /// <summary>The rest of the Welcome page is unaffected — the role and the
    /// update opt-in still commit.</summary>
    [Fact]
    public void TheOtherWelcomeFields_StillCommit()
    {
        var rig = new OnboardingWizardRig();
        rig.Vm.Open();
        rig.Vm.UpdateCheckOnStartup = false;
        rig.Vm.IsCharter = true;
        rig.Vm.Next();

        Assert.False(rig.Config.AppConfig.UpdateCheckOnStartup);
    }

    /// <summary>The process's own language is en-US and every case above either
    /// keeps it or picks it, so no test here can flip the culture under the rest
    /// of the run.</summary>
    private static int IndexOf(string configValue)
    {
        var options = LocalizationService.Instance.LanguageOptions;
        for (var i = 0; i < options.Count; i++)
        {
            if (string.Equals(options[i].ConfigValue, configValue, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        throw new InvalidOperationException($"'{configValue}' is not a listed language option.");
    }
}
