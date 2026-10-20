using DotCharter.UI.ViewModels.Shell;

namespace DotCharter.Tests.UI;

/// <summary>
///     Where a user can ask "is there an update".
///     <para>
///         Until 0.61 the answer was ONE place: Settings ▸ Updates ▸ Show pending
///         update — nav to Settings, the 8th rail entry, then two buttons. Nothing
///         in the Help menu, nothing in the command palette. The startup check did
///         find updates, but it PRIMES the dialog rather than showing it and
///         announces the find through <c>ProjectState.SetMessage</c>, which writes
///         the single shared <c>MainDisplayMessage</c> slot — so an autosave, a
///         project open or "General settings applied." erased the only notice,
///         permanently, and the primed dialog sat there unreachable.
///     </para>
///     <para>
///         <c>CheckOrShowUpdateCommand</c> is the one door for both entry
///         conditions (re-open what was found, else check now), and this pins that
///         both surfaces name it. It is a SOURCE lint for the same reason
///         <see cref="RelayCommandReachabilityTests" /> is: the command property is
///         source-generated, so reflection would find it whether or not anything on
///         earth can press it. What matters is that a human-authored file names it.
///     </para>
/// </summary>
public sealed class UpdateEntryPointTests
{
    private const string Command = "CheckOrShowUpdateCommand";

    /// <summary>The lints below match on the command's NAME, so a rename would
    /// leave them looking for a string nothing has — passing while both entry
    /// points point at nothing. Reflection is the half that cannot go stale.
    /// The command lives on the Help menu's view model, over <c>UpdateCheckFlow</c>.</summary>
    [Fact]
    public void TheUpdateEntryPointCommand_ExistsOnTheHelpMenuViewModel() => Assert.NotNull(typeof(HelpMenuViewModel).GetProperty(Command));

    /// <summary>Help is where a user looks for "check for updates", and it had
    /// Keyboard shortcuts / About / License / Usage statistics / Session Health
    /// and no update entry at all.</summary>
    [Fact]
    public void TheHelpMenu_OffersAnUpdateCheck()
    {
        var menu = MainMenuSource.ReadAllText();

        Assert.Contains(Command, menu, StringComparison.Ordinal);
        Assert.Contains("Menu_CheckForUpdates", menu, StringComparison.Ordinal);
    }

    /// <summary>…and so is the palette, which is the keyboard user's whole menu
    /// bar. `CommandCatalog` carried no update row of any kind.</summary>
    [Fact]
    public void TheCommandPalette_OffersAnUpdateCheck()
    {
        var catalog = UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(
            TestPaths.RepoRoot(), "DotCharter.UI", "ViewModels", "CommandPalette", "CommandCatalog.cs")));

        Assert.Contains("help.checkForUpdates", catalog, StringComparison.Ordinal);
        Assert.Contains(Command, catalog, StringComparison.Ordinal);
    }
}
