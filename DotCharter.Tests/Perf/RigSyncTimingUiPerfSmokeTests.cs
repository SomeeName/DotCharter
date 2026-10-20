// Rig chain-mutation timing probe: how much UI-thread time an add / remove /
// tone switch costs with the REAL AmpFxRigView attached, and how many of the
// board's unit-card containers survive each mutation.

using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;
using DotCharter.Tone.UI.ViewModels.PlayMode;
using DotCharter.Tone.UI.Views.PlayMode;
using DotCharter.Localization;

namespace DotCharter.Tests.Perf;

/// <summary>
///     Times the rig's chain mutations against the real attached
///     <see cref="AmpFxRigView" /> and reports container survival across each —
///     the structural half of the owner's "adding and removing pedals freezes
///     the app" report. Timings are LOGGED, never asserted (repo law: no
///     wall-clock assertion anywhere near a gate); the assertions here are
///     STRUCTURE: a mutation that touches one node may not rebuild every unit
///     card on the board.
/// </summary>
[Collection("HeadlessUI")]
public sealed class RigSyncTimingUiPerfSmokeTests
{
    private readonly ITestOutputHelper _output;

    public RigSyncTimingUiPerfSmokeTests(ITestOutputHelper output) => _output = output;

    private static DcRigPreset PresetA() => new(new List<DcRigModule>
    {
        new("overdrive", new Dictionary<string, float> { ["drive"] = 0.6f }, false),
        new("delay", new Dictionary<string, float> { ["time"] = 420f }, false),
        new("reverb", new Dictionary<string, float> { ["mix"] = 0.2f }, false),
        new("amp", new Dictionary<string, float> { ["gain"] = 0.4f, ["voicing"] = 1f }, false),
        new("cab", new Dictionary<string, float> { ["model"] = 2f }, false),
        new("octaver", new Dictionary<string, float> { ["sub"] = 0.3f }, false),
        new("overdrive", new Dictionary<string, float> { ["drive"] = 0.2f }, true),
    });

    /// <summary>Shares five module types with A (different params), drops the
    /// octaver + second overdrive, adds a second delay — the shape of switching
    /// between two related tones.</summary>
    private static DcRigPreset PresetB() => new(new List<DcRigModule>
    {
        new("overdrive", new Dictionary<string, float> { ["drive"] = 0.9f }, false),
        new("delay", new Dictionary<string, float> { ["time"] = 250f }, false),
        new("reverb", new Dictionary<string, float> { ["mix"] = 0.45f }, false),
        new("amp", new Dictionary<string, float> { ["gain"] = 0.7f, ["voicing"] = 2f }, false),
        new("cab", new Dictionary<string, float> { ["model"] = 3f }, false),
        new("delay", new Dictionary<string, float> { ["time"] = 900f }, false),
        new("reverb", new Dictionary<string, float> { ["mix"] = 0.1f }, false),
    });

    private static List<Border> Cards(AmpFxRigView view) =>
        view.GetVisualDescendants().OfType<Border>()
            .Where(b => b.Classes.Contains("FireUnitCard")).ToList();

    [Fact]
    public async Task RigChainMutations_TimedWithTheRealViewAttached()
    {
        var lines = new List<string>();
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var chain = new Infrastructure.FakeDspChainService();
            var rig = TestRigBuilders.Pedalboard(chain, new Infrastructure.FakeDspTonePresetService(), LocalizationService.Instance);
            var rigVm = new AmpFxRigViewModel(rig);
            // The second host: the chart editor's overlay holds its OWN
            // AmpFxRigViewModel over the same singleton, so every chain event
            // is processed twice in the shipped app. Model that.
            var secondHost = new AmpFxRigViewModel(rig);
            _ = secondHost;

            var view = new AmpFxRigView { DataContext = rigVm };
            var window = new Window { Width = 1600, Height = 1000, Content = view };
            window.Show();
            try
            {
                view.Measure(new Size(1600, 1000));
                view.Arrange(new Rect(0, 0, 1600, 1000));

                var applier = new DcRigApplier(chain);

                for (var round = 1; round <= 2; round++)
                {
                    // ── seed: preset A onto the (possibly non-empty) chain ──
                    applier.Apply(PresetA());
                    var sw = Stopwatch.StartNew();
                    rig.Sync();
                    var syncMs = sw.Elapsed.TotalMilliseconds;
                    sw.Restart();
                    view.UpdateLayout();
                    lines.Add($"r{round} seed(A,7 modules): sync={syncMs:F1}ms layout={sw.Elapsed.TotalMilliseconds:F1}ms");

                    // ── add one pedal ──
                    var before = Cards(view);
                    sw.Restart();
                    rig.AddGear(rig.AvailableModules.First(m => m.Id == "reverb"));
                    var vmMs = sw.Elapsed.TotalMilliseconds;
                    sw.Restart();
                    view.UpdateLayout();
                    var after = Cards(view);
                    var survived = after.Count(c => before.Contains(c));
                    lines.Add($"r{round} add1: vm={vmMs:F1}ms layout={sw.Elapsed.TotalMilliseconds:F1}ms " +
                              $"cards {before.Count}->{after.Count} survived={survived}");
                    // STRUCTURE, not wall clock: adding one pedal may inflate
                    // exactly ONE new card — the old full teardown rebuilt all.
                    Assert.Equal(before.Count, survived);
                    Assert.Equal(before.Count + 1, after.Count);

                    // ── remove it again ──
                    before = after;
                    sw.Restart();
                    rig.RemoveCommand.Execute(rig.Nodes[^1]);
                    vmMs = sw.Elapsed.TotalMilliseconds;
                    sw.Restart();
                    view.UpdateLayout();
                    after = Cards(view);
                    survived = after.Count(c => before.Contains(c));
                    lines.Add($"r{round} remove1: vm={vmMs:F1}ms layout={sw.Elapsed.TotalMilliseconds:F1}ms " +
                              $"cards {before.Count}->{after.Count} survived={survived}");
                    // Removing one pedal keeps every other card's container.
                    Assert.Equal(after.Count, survived);

                    // ── tone switch A→B (5 shared module types) ──
                    var vmsBefore = rig.Nodes.ToList();
                    before = after;
                    sw.Restart();
                    applier.Apply(PresetB());
                    var nativeMs = sw.Elapsed.TotalMilliseconds;
                    sw.Restart();
                    rig.Sync();
                    var syncMs2 = sw.Elapsed.TotalMilliseconds;
                    sw.Restart();
                    view.UpdateLayout();
                    after = Cards(view);
                    survived = after.Count(c => before.Contains(c));
                    var vmSurvived = rig.Nodes.Count(n => vmsBefore.Contains(n));
                    lines.Add($"r{round} switch(A->B): apply={nativeMs:F1}ms sync={syncMs2:F1}ms " +
                              $"layout={sw.Elapsed.TotalMilliseconds:F1}ms cards {before.Count}->{after.Count} " +
                              $"survived={survived} vmSurvived={vmSurvived}/{rig.Nodes.Count}");
                    // A→B share five module types (od/delay/reverb/amp/cab), and
                    // DcRigApplier reuses same-type nodes — so five view models
                    // AND their five realized cards must survive the switch.
                    Assert.Equal(5, vmSurvived);
                    Assert.Equal(5, survived);
                }
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);

        foreach (var line in lines) _output.WriteLine(line);
        File.WriteAllLines(
            Path.Combine(Path.GetTempPath(), "DotCharterTests", "rig-sync-timing.txt"), lines);
    }
}
