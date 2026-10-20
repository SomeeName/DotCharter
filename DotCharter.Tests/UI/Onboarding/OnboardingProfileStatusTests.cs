
namespace DotCharter.Tests.UI.Onboarding;

/// <summary>
///     The profile page's status strip, which could tell the user two
///     contradictory things at once.
///
///     <para>
///         <c>OnNewProfileNameChanged</c> and <c>OnProfileNameChanged</c> wrote
///         the <c>Onboarding_Profile_NameTaken</c> refusal and NOTHING ever
///         withdrew it. Type "Default", see it refused, type "Ana": the Create
///         button enables (<c>CanCreateProfile</c> is recomputed on every
///         keystroke) while "Default is already taken" stays on screen next to
///         it. The user is told the name they are looking at is refused when it
///         is not.
///     </para>
///     <para>
///         And the strip was hard-coded <c>Classes="DlgStrip Ok"</c> in the view,
///         so the refusal was painted in the SUCCESS colour — the pattern the
///         Audio page's own strip beside it already gets right with
///         <c>Classes.Error="{Binding AudioTestFailed}"</c>.
///     </para>
/// </summary>
/// <remarks>The rig hands the wizard the process-wide LocalizationService, and a
/// Next off the Welcome page reaches SetConfiguredCulture — a culture write
/// through product code races exactly as a direct one does. See
/// <c>CultureCollectionLintTests</c>.</remarks>
[Collection("LocalizationSingleton")]
public sealed class OnboardingProfileStatusTests
{
    /// <summary>The rig seeds one profile called "Default".</summary>
    private static OnboardingWizardRig OnTheProfilePage()
    {
        var rig = new OnboardingWizardRig();
        rig.Vm.Open();
        rig.Advance(6); // Welcome -> Library -> Audio -> Input -> Tone -> Latency -> Profile
        Assert.True(rig.Vm.IsProfile);
        return rig;
    }

    /// <summary>The bug, on the Create box.</summary>
    [Fact]
    public void ARefusedNewName_StopsBeingRefusedOnceItIsFree()
    {
        var rig = OnTheProfilePage();

        rig.Vm.NewProfileName = "Default";
        Assert.True(rig.Vm.ProfileStatusIsRefusal);
        Assert.True(rig.Vm.HasProfileStatus);
        Assert.False(rig.Vm.CanCreateProfile);

        rig.Vm.NewProfileName = "Ana";

        Assert.True(rig.Vm.CanCreateProfile);
        Assert.False(rig.Vm.ProfileStatusIsRefusal);
        Assert.False(rig.Vm.HasProfileStatus);
    }

    /// <summary>…and on the rename box, whose refusal has the same two writers.
    /// A rename onto ANOTHER player's name is refused; typing something free
    /// withdraws it.</summary>
    [Fact]
    public void ARefusedRename_StopsBeingRefusedOnceItIsFree()
    {
        var rig = OnTheProfilePage();
        rig.Vm.NewProfileName = "Ana";
        rig.Vm.CreateProfileCommand.Execute(null);
        // Now editing "Ana", with "Default" beside it.

        rig.Vm.ProfileName = "Default";
        Assert.True(rig.Vm.ProfileStatusIsRefusal);
        Assert.False(rig.Vm.CanRenameProfile);

        rig.Vm.ProfileName = "Anastasia";

        Assert.True(rig.Vm.CanRenameProfile);
        Assert.False(rig.Vm.ProfileStatusIsRefusal);
    }

    /// <summary>A refusal is a refusal, not a result — the view paints the strip
    /// off this flag, and it was hard-coded to the success class.</summary>
    [Fact]
    public void ARefusal_IsFlaggedAsOne_AndAResultIsNot()
    {
        var rig = OnTheProfilePage();

        rig.Vm.NewProfileName = "Default";
        Assert.True(rig.Vm.ProfileStatusIsRefusal);

        rig.Vm.NewProfileName = "Ana";
        rig.Vm.CreateProfileCommand.Execute(null);

        Assert.True(rig.Vm.HasProfileStatus);
        Assert.False(rig.Vm.ProfileStatusIsRefusal);
    }

    /// <summary>
    ///     Withdrawing a refusal must not wipe a RESULT. Create a profile, then
    ///     start typing another name: the "created" line the user is reading has
    ///     to survive the keystroke that clears nothing.
    /// </summary>
    [Fact]
    public void ClearingARefusal_DoesNotEatTheLastResult()
    {
        var rig = OnTheProfilePage();
        rig.Vm.NewProfileName = "Ana";
        rig.Vm.CreateProfileCommand.Execute(null);
        var created = rig.Vm.ProfileStatus;
        Assert.NotEqual(string.Empty, created);

        rig.Vm.NewProfileName = "Bo";

        Assert.Equal(created, rig.Vm.ProfileStatus);
    }
}
