using Avalonia.Controls;
using DotCharter.Tone.UI.Controls;

namespace DotCharter.Tests.Perf;

/// <summary>
///     Behavioural half of <see cref="EffectiveVisibilityWatcher" /> — the
///     shared fix for six surfaces whose
///     <c>change.Property.Name == nameof(IsEffectivelyVisible)</c> branches
///     never fired in Avalonia 12 (the property is computed, not styled; found
///     2026-08-15 via the rig's never-starting meter clock). Needs a real
///     window (attachment + ancestor chains), hence <c>*UiPerfSmokeTests</c>;
///     the wiring lints live in the default gate
///     (<c>UI/SongCoverImageTests.BothSurfaces_RewarmRealizedRowsOnBecomingVisible</c>).
/// </summary>
[Collection("HeadlessUI")]
public sealed class EffectiveVisibilityWatcherUiPerfSmokeTests
{
    [Fact]
    public async Task AnAncestorsVisibilityFlip_IsReported_AndDetachReadsAsHidden()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var inner = new Border();
            var host = new Panel { Children = { inner } };
            var reports = new List<bool>();
            _ = new EffectiveVisibilityWatcher(inner, reports.Add);

            var window = new Window { Width = 300, Height = 200, Content = host };
            window.Show();
            try
            {
                window.UpdateLayout();
                Assert.Equal([true], reports); // the attach itself is the first flip

                // The ANCESTOR hides — exactly the case the dead pattern never
                // saw (the control's own IsVisible is untouched).
                host.IsVisible = false;
                Assert.Equal([true, false], reports);

                host.IsVisible = true;
                Assert.Equal([true, false, true], reports);

                // No phantom reports for unrelated changes.
                inner.Width = 40;
                Assert.Equal(3, reports.Count);

                // Detach reads as hidden.
                window.Content = null;
                Assert.Equal([true, false, true, false], reports);
            }
            finally
            {
                window.Content = null;
                window.Close();
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ReparentingRebuildsTheAncestorChain()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var inner = new Border();
            var firstHost = new Panel { Children = { inner } };
            var secondHost = new Panel { IsVisible = false };
            var root = new Panel { Children = { firstHost, secondHost } };
            var reports = new List<bool>();
            _ = new EffectiveVisibilityWatcher(inner, reports.Add);

            var window = new Window { Width = 300, Height = 200, Content = root };
            window.Show();
            try
            {
                window.UpdateLayout();
                Assert.Equal([true], reports);

                // Move under the HIDDEN host: the new chain must govern.
                firstHost.Children.Remove(inner);
                secondHost.Children.Add(inner);
                Assert.False(reports[^1]);

                // …and the OLD parent's visibility no longer matters.
                var count = reports.Count;
                firstHost.IsVisible = false;
                Assert.Equal(count, reports.Count);

                secondHost.IsVisible = true;
                Assert.True(reports[^1]);
            }
            finally
            {
                window.Content = null;
                window.Close();
            }
        }, CancellationToken.None);
    }
}
