using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using System.Runtime.CompilerServices;
using Avalonia.Styling;
using DotCharter.Tone.UI.Controls.Fire;

namespace DotCharter.Tests.UI.Controls;

/// <summary>
///     <see cref="FireDrawnControl" />, the base every custom-drawn Fire control
///     shares, with NO Avalonia platform: the counted repaint path, the paint
///     cache dropped on attach / detach / theme change, and the theme
///     subscription taken on attach and released on detach. The attach and
///     detach overrides are driven directly through a probe subclass; the real
///     tree is <c>Perf/FireControlsUiPerfSmokeTests</c>.
/// </summary>
public sealed class FireDrawnControlTests
{
    [Fact]
    public void EveryRepaint_IsCounted()
    {
        var probe = new Probe(followsThemeVariant: false);

        probe.RequestRepaint();
        probe.RequestRepaint();

        Assert.Equal(2, probe.RepaintRequests);
    }

    [Fact]
    public void AThemeFollowingControl_SubscribesOnAttach_RepaintsOnAChange_AndReleasesOnDetach()
    {
        var probe = new Probe(followsThemeVariant: true);

        ChangeTheme(probe, ThemeVariant.Light);
        Assert.Equal(0, probe.RepaintRequests);
        Assert.False(probe.IsFollowingThemeVariant);

        probe.Attach();
        Assert.True(probe.IsFollowingThemeVariant);

        probe.FillCache();
        ChangeTheme(probe, ThemeVariant.Dark);
        Assert.Equal(1, probe.RepaintRequests);
        Assert.Equal(0, probe.CachedColors);

        probe.Detach();
        Assert.False(probe.IsFollowingThemeVariant);

        ChangeTheme(probe, ThemeVariant.Light);
        Assert.Equal(1, probe.RepaintRequests);
    }

    [Fact]
    public void ADefaultControl_TakesNoThemeSubscription()
    {
        var probe = new Probe(followsThemeVariant: false);

        probe.Attach();
        Assert.False(probe.IsFollowingThemeVariant);

        probe.FillCache();
        ChangeTheme(probe, ThemeVariant.Dark);
        Assert.Equal(0, probe.RepaintRequests);
        Assert.Equal(1, probe.CachedColors);

        probe.Detach();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ThePaintCache_IsDroppedOnAttachAndOnDetach(bool followsThemeVariant)
    {
        var probe = new Probe(followsThemeVariant);

        probe.FillCache();
        probe.Attach();
        Assert.Equal(0, probe.CachedColors);

        probe.FillCache();
        probe.Detach();
        Assert.Equal(0, probe.CachedColors);
    }

    [Fact]
    public void TheFireControls_DeriveFromTheBase_AndOnlyTheThreeLiveOnesFollowTheTheme()
    {
        FireDrawnControl[] following = [new FireKnob(), new FireLevelMeter(), new FireTunerMeter()];
        FireDrawnControl[] still =
        [
            new FireProgress(), new FireChipStrip(), new RigFan(), new RigWire(), new RigMicGlyph(),
            new RigFilterResponsePlot()
        ];

        Assert.All(following, c => Assert.True(Probe.FollowsThemeVariant(c), c.GetType().Name));
        Assert.All(still, c => Assert.False(Probe.FollowsThemeVariant(c), c.GetType().Name));
        // These two build a platform cursor in their constructor, so only the type is checked here.
        Assert.True(typeof(FireDrawnControl).IsAssignableFrom(typeof(RigMicStage)));
        Assert.True(typeof(FireDrawnControl).IsAssignableFrom(typeof(RigMicSideView)));
    }

    private static void ChangeTheme(Control control, ThemeVariant variant) => control.SetValue(ThemeVariantScope.ActualThemeVariantProperty, variant);

    private sealed class Probe : FireDrawnControl
    {
        public Probe(bool followsThemeVariant)
            : base(followsThemeVariant)
        {
        }

        public int CachedColors => Paint.CachedColorCount;

        public static bool FollowsThemeVariant(FireDrawnControl control)
        {
            var field = typeof(FireDrawnControl).GetField("_followsThemeVariant",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.NotNull(field);
            return (bool)field.GetValue(control)!;
        }

        public void RequestRepaint() => Repaint();

        public void FillCache()
        {
            Paint.Color(this, "FireAccent");
            Paint.Brush(Colors.Red);
        }

        private static VisualTreeAttachmentEventArgs AttachmentArgs() => (VisualTreeAttachmentEventArgs)RuntimeHelpers.GetUninitializedObject(typeof(VisualTreeAttachmentEventArgs));

        public void Attach() => OnAttachedToVisualTree(AttachmentArgs());

        public void Detach() => OnDetachedFromVisualTree(AttachmentArgs());
    }
}
