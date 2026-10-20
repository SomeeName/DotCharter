using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace DotCharter.Tests.UI;

/// <summary>
///     Where a REFUSED run puts the user, and whether that screen can say why.
///     <para>
///         Live Play can decline to start — no input device, no chart. When it
///         does, <c>PlayModeViewModel.StartRun</c> unwinds and picks a screen. It
///         used to pick <c>SongSelect</c>: the user had sat through Get Ready and
///         a three-second count-in, and landed two screens back with the reason as
///         a small red string in the browse bar at the top-left, while everything
///         they had set up (path, scroll speed, stem mutes, drop tuner) went off
///         screen — and away from the input chip and the live signal probe, which
///         are the two things on Get Ready that DIAGNOSE the refusal.
///     </para>
///     <para>
///         It lands on the pre-play screen the run came from now (Get Ready, or
///         Co-op Setup for an armed roster). <b>The durable rule is not the screen
///         name</b> — it is that whichever screen a refusal lands on must render
///         <c>FlowError</c>, or the run vanishes with no explanation anywhere.
///         Both halves are pinned below, because routing the refusal somewhere new
///         is exactly the change that would silently break the second one.
///     </para>
/// </summary>
public sealed class RefusedRunLandingTests
{
    /// <summary>The screens named inside the <c>if (!_livePlay.IsActive)</c>
    /// refusal block — the whole input to both assertions.</summary>
    private static IReadOnlyList<string> RefusalLandingScreens()
    {
        var source = UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(),
            "DotCharter.UI", "ViewModels", "PlayMode", "PlayModeViewModel.Gameplay.cs")));

        const string guard = "if (!_livePlay.IsActive)";
        var at = source.IndexOf(guard, StringComparison.Ordinal);
        Assert.True(at >= 0,
            "PlayModeViewModel.Gameplay no longer guards on `!_livePlay.IsActive` — repoint this test.");

        var screens = Regex.Matches(Block(source, at + guard.Length), @"PlayModeScreen\.(\w+)")
            .Select(m => m.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.True(screens.Count > 0, "The refusal block assigns no screen at all.");
        return screens;
    }

    /// <summary>
    ///     A refusal must return the user to the PRE-PLAY screen — where their run
    ///     setup still is and where the input chip and signal probe live — never
    ///     back out to the library.
    /// </summary>
    [Fact]
    public void ARefusedRun_ReturnsToThePreplayScreen_NotTheLibrary()
    {
        var screens = RefusalLandingScreens();

        Assert.DoesNotContain("SongSelect", screens);
        Assert.Contains("TuningCheck", screens);
    }

    /// <summary>
    ///     …and every screen it can land on must actually show the reason. This is
    ///     the half that catches a future reroute: `FlowError` is rendered by two
    ///     screen roots out of sixteen, so almost any other destination would drop
    ///     the explanation entirely and read as a run that just did not start.
    /// </summary>
    [Fact]
    public void EveryScreenARefusedRunLandsOn_RendersTheReason()
    {
        var view = PlayModeMarkup.Document();
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        foreach (var screen in RefusalLandingScreens())
        {
            var rootName = screen + "ScreenRoot";
            var root = view.Descendants()
                .FirstOrDefault(e => (string?)e.Attribute(x + "Name") == rootName);
            Assert.True(root != null,
                $"A refused run lands on PlayModeScreen.{screen}, but the Play Mode markup has no `{rootName}`.");

            var bindsFlowError = root.DescendantsAndSelf()
                .SelectMany(e => e.Attributes())
                .Any(a => a.Value.Contains("FlowError", StringComparison.Ordinal));

            Assert.True(bindsFlowError,
                $"`{rootName}` binds nothing to FlowError, so a run refused onto PlayModeScreen.{screen} "
                + "would leave the user there with no reason on screen.");
        }
    }

    // ---- helpers --------------------------------------------------------------

    /// <summary>The braced block starting at the first `{` at or after
    /// <paramref name="from" />.</summary>
    private static string Block(string source, int from)
    {
        var open = source.IndexOf('{', from);
        Assert.True(open >= 0, "No block after the refusal guard.");

        var depth = 0;
        for (var i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return source[(open + 1)..i];
        }

        throw new InvalidOperationException("Unbalanced braces after the refusal guard.");
    }
}
