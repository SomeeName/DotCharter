namespace DotCharter.Tests.UI;

/// <summary>
///     The tone rig has ONE view model and TWO hosts — the chart editor's overlay
///     and Play Mode — and each host owns its own Esc walk-up:
///     <c>MainWindowViewModel.HandleEscapeKey</c> and
///     <c>PlayModeViewModel.TryCloseOverlay</c>. Since v0.43 they close over the
///     same <c>PedalboardViewModel</c> singleton, so a layer the rig can be IN is
///     a layer both walks have to know about.
///     <para>
///         One did not. Play Mode walked save dialog → tone-browser drawer → board;
///         the editor walked save dialog → board, with the drawer rung simply
///         missing. So browsing tones in the chart editor and pressing Esc threw
///         away the whole rig overlay, while the identical gesture in Play Mode
///         closed only the drawer — the same surface, the same key, two answers.
///     </para>
///     <para>
///         A source lint, deliberately: what is being asserted is that two
///         hand-written walk-ups AGREE, and the missing rung compiled perfectly.
///     </para>
/// </summary>
public sealed class RigEscapeParityTests
{
    /// <summary>The rig's dismissible layers, outermost LAST — the order Esc must
    /// unwind them in, because that is the order they stack on screen.</summary>
    private static readonly string[] RigLayersInCloseOrder =
    [
        "Browser.CancelDelete()",
        "Browser.IsSaveDialogOpen = false",
        "IsBrowserOpen = false",
        "IsPedalboardOpen = false"
    ];

    [Theory]
    [InlineData("HandleEscapeKey", @"DotCharter.UI\ViewModels\Shell\MainWindowViewModel.cs",
        "public void HandleEscapeKey()")]
    [InlineData("TryCloseOverlay", @"DotCharter.UI\ViewModels\PlayMode\PlayModeViewModel.Overlays.cs",
        "public bool TryCloseOverlay()")]
    public void EveryRigHost_UnwindsTheSameLayersInTheSameOrder(
        string label, string relativePath, string signature)
    {
        var body = MethodBody(
            File.ReadAllText(Path.Combine(TestPaths.RepoRoot(), relativePath.Replace('\\', Path.DirectorySeparatorChar))),
            signature);

        var previous = -1;
        foreach (var layer in RigLayersInCloseOrder)
        {
            var at = body.IndexOf(layer, StringComparison.Ordinal);
            Assert.True(at >= 0,
                $"{label} never closes the rig layer `{layer}`. Both hosts drive ONE PedalboardViewModel, "
                + "so a layer one walk-up handles and the other does not is the same key giving two answers.");
            Assert.True(at > previous,
                $"{label} closes `{layer}` out of order — Esc must unwind the rig's layers "
                + "outermost-last, the order they stack on screen.");
            previous = at;
        }
    }

    // ---- helpers --------------------------------------------------------------

    /// <summary>The braced body following <paramref name="signature" />. Fails
    /// loudly on a signature that no longer exists, so a rename cannot leave the
    /// assertions searching an empty string.</summary>
    private static string MethodBody(string source, string signature)
    {
        var at = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(at >= 0, $"`{signature}` no longer exists — repoint this test.");

        var open = source.IndexOf('{', at + signature.Length);
        Assert.True(open >= 0, $"No body found for `{signature}`.");

        var depth = 0;
        for (var i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return source[(open + 1)..i];
        }

        throw new InvalidOperationException($"Unbalanced braces after `{signature}`.");
    }
}
