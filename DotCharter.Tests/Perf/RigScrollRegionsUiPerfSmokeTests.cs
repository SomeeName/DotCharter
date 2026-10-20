using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using DotCharter.Tone.Audio.Dsp;
using DotCharter.Tone.UI.ViewModels.PlayMode;
using DotCharter.Tone.UI.Views.PlayMode;
using DotCharter.Localization;

namespace DotCharter.Tests.Perf;

/// <summary>
///     W1 (2026-09-16) — NESTED SCROLL REGIONS ARE A DEFECT IN THIS SCREEN.
///     <para>
///         A scroller inside a scroller means the wheel lands on whichever one
///         the pointer happens to be over and the outer extent lies about how
///         much content there is. Nothing about that throws, warns or fails a
///         bounds assertion: both containers arrange perfectly. So this walks
///         the laid-out <c>AmpFxRigView</c> subtree — including the hosted
///         <c>ToneDesignPanel</c>, <c>ToneSectionsPanel</c>,
///         <c>RigModuleEditorView</c> and the <c>RigEditors/*</c> family views —
///         and reports every pair.
///     </para>
///     <para>
///         A SCROLL REGION is a <see cref="ScrollViewer" /> with at least one
///         axis that can grow a BAR (<c>Visible</c> or <c>Auto</c>). That is the
///         distinction that makes the rule implementable: a <c>ListBox</c>
///         brings a <c>ScrollViewer</c> in its template whether you want one or
///         not, so the way to stop it being a region is to disable both axes and
///         let the list grow into the scroller that owns the surface. A
///         <c>TextBox</c>'s template scroller is <c>Hidden</c>, which is neither
///         — a text field is not a scroll region of this screen.
///     </para>
/// </summary>
[Collection("HeadlessUI")]
public sealed class RigScrollRegionsUiPerfSmokeTests(ITestOutputHelper output)
{
    private static void WithRig(Action<AmpFxRigView, AmpFxRigViewModel, PedalboardViewModel, Window> body,
        IEnumerable<string>? gear = null, double width = 1920, double height = 1080)
    {
        var metadata = new JuceDspChainService();
        metadata.LoadCatalog();
        var catalogue = metadata.Catalog;
        Assert.SkipWhen(catalogue.Count == 0, "native DSP ABI (juce_sharp.dll) not loadable here");

        var chain = new FakeDspChainService(catalogue);
        using var rig = TestRigBuilders.Pedalboard(chain, new FakeDspTonePresetService(), LocalizationService.Instance,
            toneDesignPanel: ToneDesignPanels.Real());
        foreach (var id in gear ?? ["overdrive", "namamp", "amp", "cab", "graphiceq", "wah"])
            rig.AddGear(catalogue.Single(m => m.Id == id));

        var model = new AmpFxRigViewModel(rig);
        var view = new AmpFxRigView { DataContext = model };
        var window = new Window { Width = width, Height = height, Content = view };
        window.Show();
        try
        {
            for (var pass = 0; pass < 4; pass++) window.UpdateLayout();
            body(view, model, rig, window);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>An axis that can actually GROW A BAR. <c>Hidden</c> is excluded
    /// deliberately: it is what every <c>TextBox</c> template carries for its own
    /// text, and a text field is not a scroll region of this screen.</summary>
    private static bool Scrolls(ScrollBarVisibility v) =>
        v is ScrollBarVisibility.Visible or ScrollBarVisibility.Auto;

    /// <summary>
    ///     A scroll region of THIS SCREEN — a scroller over a panel of content,
    ///     not a control scrolling its own interior.
    ///     <para>
    ///         The exclusion is a <see cref="TextBox" /> ancestor, and it is not
    ///         cosmetic: `NumericUpDown.FireNumeric` in the editor rack's control
    ///         assignment form brings a `PART_TextBox` whose template scroller is
    ///         `Auto`, so without this the rack reports a nested region for every
    ///         numeric field anyone puts in it. A text field scrolling its own
    ///         text is not the defect this class is about.
    ///     </para>
    /// </summary>
    private static bool IsScrollRegion(ScrollViewer s) =>
        (Scrolls(s.HorizontalScrollBarVisibility) || Scrolls(s.VerticalScrollBarVisibility))
        && !s.GetVisualAncestors().OfType<TextBox>().Any();

    /// <summary>The closest ancestor that is a named view, for a label a reader
    /// can act on — an anonymous <c>ScrollViewer</c> five templates deep is not
    /// a location.</summary>
    private static string Where(Visual v)
    {
        var parts = v.GetSelfAndVisualAncestors()
            .Where(a => a is Control { Name.Length: > 0 } or UserControl)
            .Select(a => a is Control { Name: { Length: > 0 } n } ? n : a.GetType().Name)
            .Take(4);
        return string.Join(" < ", parts);
    }

    private static string OwningView(Visual v) =>
        v.GetSelfAndVisualAncestors().OfType<UserControl>().FirstOrDefault()?.GetType().Name ?? "?";

    /// <summary>
    ///     THE INVENTORY AND THE GUARD IN ONE. Walks every state of the screen
    ///     that realizes a different subtree — the drawer open, the designer
    ///     expanded, and each module on the board selected in turn — and fails on
    ///     a scroll region inside another one.
    ///     <para>
    ///         Proven non-vacuous by restoring <c>MaxHeight="200"</c> and the
    ///         default (Auto) bar visibility on <c>ToneMatchList</c>: it then
    ///         reported "ToneMatchList inside RigToneDesignScroll" and failed.
    ///     </para>
    ///     <para>
    ///         <b>There is no allow-list, and there must not become one.</b> The
    ///         two source-level nested scrollers left in the family views —
    ///         <c>RigAmpFaceView</c>'s capture library and
    ///         <c>RigPedalEditorView</c>'s file library, both W4's — do not
    ///         REALIZE here: each is gated on a populated library the headless
    ///         fixture has none of. If W4 makes either visible, this case turns
    ///         red, which is the correct outcome.
    ///     </para>
    /// </summary>
    [Fact]
    public async Task NoScrollRegionInTheRig_SitsInsideAnother()
    {
        await HeadlessSessionFixture.Dispatch(() => WithRig((view, model, rig, window) =>
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var offenders = new List<string>();

            void Sweep(string state)
            {
                for (var pass = 0; pass < 4; pass++) window.UpdateLayout();

                foreach (var s in view.GetVisualDescendants().OfType<ScrollViewer>())
                {
                    if (!s.IsEffectivelyVisible || !IsScrollRegion(s)) continue;

                    var label = $"{Where(s)}  [{OwningView(s)}]";
                    if (seen.Add(label)) output.WriteLine($"  {state,-22} {label}");

                    var outer = s.GetVisualAncestors().OfType<ScrollViewer>()
                        .FirstOrDefault(a => a.IsEffectivelyVisible && IsScrollRegion(a));
                    if (outer is null) continue;

                    var fault = $"{Where(s)} inside {Where(outer)}";
                    if (!offenders.Contains(fault)) offenders.Add(fault);
                }
            }

            output.WriteLine("scroll regions in the rig (state, path, owning view):");

            rig.SelectNodeAt(-1);
            Sweep("board only");

            rig.IsBrowserOpen = true;
            Sweep("drawer");

            ToneDesignPanels.Of(rig.Browser).IsExpanded = true;
            Sweep("drawer + designer");

            rig.IsBrowserOpen = false;
            for (var i = 0; i < rig.Nodes.Count; i++)
            {
                rig.SelectNodeAt(i);
                Sweep($"editor:{rig.Nodes[i].ModuleTypeId}");
            }

            output.WriteLine($"{seen.Count} scroll regions, {offenders.Count} nested");
            foreach (var o in offenders) output.WriteLine($"  NESTED {o}");

            Assert.True(seen.Count > 3, "this case realized almost nothing — it is not exercising the screen");
            Assert.True(offenders.Count == 0,
                $"{offenders.Count} nested scroll region(s) in the rig: {string.Join("; ", offenders)}");
        }), CancellationToken.None);
    }
}
