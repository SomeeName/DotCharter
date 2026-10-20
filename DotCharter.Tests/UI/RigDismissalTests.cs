using System.Text.RegularExpressions;

namespace DotCharter.Tests.UI;

/// <summary>
///     <b>The Amp &amp; FX rig always has a way out, and which way depends on
///     the host.</b>
///     <para>
///         The rig is one control shown by two surfaces. The chart editor shows
///         it over a shell with an ACTIVITY RAIL whose rig row is a two-way
///         toggle, so that row is the door in both directions and the header
///         draws no ✕ — a second door onto one boolean is the shape
///         <c>RelayCommandReachabilityTests</c> exists to flag. Play Mode has no
///         rail at all (a run is a game and hides every piece of chrome), so
///         there the ✕ is the only pointer door and it stays.
///     </para>
///     <para>
///         That asymmetry is exactly the kind of thing that rots into "the rig
///         cannot be closed from Play Mode" after someone tidies the header. So
///         both halves are pinned, plus the keyboard door that covers them both.
///     </para>
/// </summary>
public sealed class RigDismissalTests
{
    [Fact]
    public void TheHeaderCloseButton_IsGatedOnTheHostRatherThanDeleted()
    {
        var rig = Source("DotCharter.Tone.UI", "Views", "PlayMode", "AmpFxRigView.axaml");

        var at = rig.IndexOf("x:Name=\"RigCloseButton\"", StringComparison.Ordinal);
        Assert.True(at > 0,
            "the rig header has no close button at all — Play Mode has no activity rail, so deleting " +
            "it outright leaves that host with no POINTER way out of the rig");

        var button = rig[at..rig.IndexOf("/>", at, StringComparison.Ordinal)];
        Assert.Contains("IsVisible=\"{Binding ShowCloseButton}\"", button, StringComparison.Ordinal);
        Assert.Contains("CloseCommand", button, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The shell's overlay turns it off exactly where the rail is, and Play
    ///     Mode never does. Over Home or a player's full-bleed Library there is
    ///     no rail, so the shell's ✕ comes back — it follows the chrome flag, at
    ///     build and whenever the surface or the context changes.
    ///     `ShowCloseButton` defaults to TRUE, so a host that forgets keeps its
    ///     ✕ — the safe direction for a dismissal affordance.
    /// </summary>
    [Fact]
    public void TheShellHost_SuppressesIt_OnlyWhereTheRailIs()
    {
        var editor = Source("DotCharter.UI", "ViewModels", "Shell", "ToneRigViewModel.cs");
        var playMode = Source("DotCharter.UI", "ViewModels", "PlayMode", "PlayModeToneRigViewModel.cs");
        var screen = Source("DotCharter.Tone.UI", "ViewModels", "PlayMode", "AmpFxRigViewModel.cs");

        Assert.Contains("ShowCloseButton = !IsShellChromeVisible", editor, StringComparison.Ordinal);
        Assert.Contains("rig.ShowCloseButton = !IsShellChromeVisible", editor, StringComparison.Ordinal);
        Assert.DoesNotContain("ShowCloseButton", playMode, StringComparison.Ordinal);
        Assert.Contains("private bool _showCloseButton = true;", screen, StringComparison.Ordinal);
    }

    /// <summary>
    ///     …and the editor's replacement really is reachable: the rail's rig row
    ///     is a focusable two-way toggle over the SAME flag the ✕ used to clear,
    ///     and the rail is visible wherever the editor is.
    /// </summary>
    [Fact]
    public void TheEditorsReplacementDoor_IsTheRailsTwoWayRigToggle()
    {
        var rail = Source("DotCharter.UI", "Views", "Shell", "ActivityRailView.axaml");

        var at = rail.IndexOf("x:Name=\"RailRig\"", StringComparison.Ordinal);
        Assert.True(at > 0, "the activity rail has no rig row — the editor now has no way out of the rig");

        var row = rail[at..rail.IndexOf("</ToggleButton>", at, StringComparison.Ordinal)];
        Assert.Contains("IsPedalboardOpen, Mode=TwoWay", row, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name", row, StringComparison.Ordinal);

        // A RadioButton here would be a one-way trip: it cannot uncheck itself,
        // so the rig would open and never close from the rail.
        Assert.StartsWith("<ToggleButton", rail[rail.LastIndexOf('<', at)..], StringComparison.Ordinal);
    }

    /// <summary>The keyboard door, which covers both hosts and neither button.</summary>
    [Fact]
    public void EscapeClosesTheRig_InBothHosts()
    {
        foreach (var (project, parts) in new (string, string[])[]
                 {
                     ("editor", ["DotCharter.UI", "ViewModels", "Shell", "MainWindowViewModel.cs"]),
                     ("Play Mode", ["DotCharter.UI", "ViewModels", "PlayMode", "PlayModeViewModel.Overlays.cs"])
                 })
        {
            var source = Source(parts);
            Assert.True(Regex.IsMatch(source, @"IsPedalboardOpen\)\s*\{?\s*(?:.|\n)*?IsPedalboardOpen = false"),
                $"the {project} host's Escape chain no longer closes the rig — with the editor's ✕ gone " +
                "that is one of only two doors");
        }
    }

    private static string Source(params string[] parts) =>
        UiSourceTree.ReadAllText(Path.Combine(new[] { TestPaths.RepoRoot() }.Concat(parts).ToArray()));
}
