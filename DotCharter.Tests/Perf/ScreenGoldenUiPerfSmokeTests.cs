using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using DotCharter.Presentation.Fakes;
using DotCharter.Tests.UI.Goldens;
using DotCharter.UI.Gallery.Pages.Screens;
using DotCharter.UI.Views.Shell;

namespace DotCharter.Tests.Perf;

/// <summary>
///     <b>Every screen looks as the owner approved it</b> (target-architecture.md § 5).
///     One case per <see cref="ScreenCatalog" /> entry × fake variant, rendered
///     exactly as the gallery renders it (<see cref="ScreenPages.Build" />) at
///     1600×900 with Skia, and compared per pixel against
///     <c>DotCharter.Tests/Goldens/ui/&lt;area&gt;/&lt;screen&gt;--&lt;variant&gt;.png</c>.
/// </summary>
/// <remarks>
///     <para>
///         <b>Its own <c>dotnet test</c> invocation</b> — <c>Invoke-Gate.ps1
///         -Goldens</c>, which sets <c>DOTCHARTER_UI_SWEEP_DUMP=1</c>: the session's
///         drawing mode is fixed when it starts, and only the Skia mode can return a
///         frame. The <c>-Headless</c> run, CI's headless job and the release
///         preflight all exclude this class by its full name
///         (<see cref="FullName" />); <c>UI/Goldens/ScreenGoldenStoreTests</c>
///         pins that exclusion.
///     </para>
///     <para>
///         A case with no approved PNG FAILS with <see cref="ScreenGoldens.NoGoldenMessage" /> —
///         it never passes by having nothing to compare. A mismatch writes
///         <c>.actual.png</c>, <c>.diff.png</c> and an <c>index.html</c> under
///         <see cref="ScreenGoldens.FailureDirectory" />. With
///         <see cref="ScreenGoldens.StageDirEnvVar" /> set, every case also stages its
///         render for <c>tools/agent/Update-Goldens.ps1</c>. First-layout time is
///         logged per screen, never asserted.
///     </para>
/// </remarks>
[Collection("HeadlessUI")]
public sealed class ScreenGoldenUiPerfSmokeTests
{
    public const string FullName = "DotCharter.Tests.Perf.ScreenGoldenUiPerfSmokeTests";

    private static readonly Lazy<bool> FailuresCleared = new(() =>
    {
        if (Directory.Exists(ScreenGoldens.FailureDirectory)) Directory.Delete(ScreenGoldens.FailureDirectory, recursive: true);
        return true;
    });

    public static TheoryData<string, string> Cases
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (var entry in ScreenCatalog.Default.Entries)
            {
                foreach (var variant in FakeScreens.VariantsOf(entry.Contract))
                    data.Add(entry.Contract.FullName!, variant.Name);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task EveryScreen_MatchesItsApprovedGolden(string contractName, string variantName)
    {
        Assert.True(UiSweepRunner.CaptureEnabled,
            "the goldens render with Skia — run them as their own invocation, `tools/agent/Invoke-Gate.ps1 -Goldens` " +
            "(it sets DOTCHARTER_UI_SWEEP_DUMP=1); they are excluded from -Headless on purpose");
        Assert.True(HeadlessSessionFixture.SweptCulture == "en-US",
            $"goldens are en-US only (target-architecture.md § 5); this session runs {HeadlessSessionFixture.SweptCulture}");
        _ = FailuresCleared.Value;

        var entry = ScreenCatalog.Default.Entries.Single(e => e.Contract.FullName == contractName);
        var variant = FakeScreens.VariantsOf(entry.Contract).Single(v => v.Name == variantName);
        var relative = entry.GoldenPath(variant.Name);

        var (png, layoutMs) = await HeadlessSessionFixture.Dispatch(() => Render(entry, variant),
            TestContext.Current.CancellationToken);
        TestContext.Current.TestOutputHelper?.WriteLine(
            string.Create(CultureInfo.InvariantCulture, $"{relative}: first layout {layoutMs:0.0} ms"));

        var goldenPath = Path.Combine(ScreenGoldens.GoldensRoot, relative);
        var golden = File.Exists(goldenPath) ? File.ReadAllBytes(goldenPath) : null;
        var actual = ScreenGoldens.Decode(png);
        var expected = golden is null ? null : ScreenGoldens.Decode(golden);
        var ratio = expected is null ? 1.0 : ScreenGoldens.DifferenceRatio(expected, actual);

        Stage(relative, png, expected, actual, ratio, golden is null);

        if (expected is null)
        {
            WriteFailure(relative, png, null, null, "no golden");
            Assert.Fail($"{relative}: {ScreenGoldens.NoGoldenMessage}");
        }

        if (!ScreenGoldens.Matches(ratio))
        {
            WriteFailure(relative, png, ScreenGoldens.DiffPng(expected, actual), goldenPath,
                string.Create(CultureInfo.InvariantCulture, $"{ratio:P3} of pixels differ"));
            Assert.Fail(FormattableString.Invariant(
                $"{relative}: {ratio:P3} of pixels differ (limit {ScreenGoldens.MaxDifferingShare:P2}); actual and diff in {ScreenGoldens.FailureDirectory}. If the change is intended, re-stage with Update-Goldens.ps1 -Stage and ask the owner to accept."));
        }
    }

    private static IBrush AppBackground()
    {
        var app = Application.Current ?? throw new InvalidOperationException("no Application — the headless session did not boot");
        Assert.True(app.TryFindResource("AppBackground", app.ActualThemeVariant, out var brush) && brush is IBrush,
            "AppBackground did not resolve — the golden window would capture on the default white");
        return (IBrush)brush!;
    }

    /// <summary>A registry-driven Theory passes by having nothing to run; this is the floor under it.</summary>
    [Fact]
    public void TheCases_CoverTheWholeCatalog()
    {
        Assert.True(Cases.Count >= 30, $"only {Cases.Count} golden cases");
        foreach (var entry in ScreenCatalog.Default.Entries)
            Assert.True(FakeScreens.VariantsOf(entry.Contract).Count > 0, $"{entry.Contract.Name} has no fake variants");
    }

    private static (byte[] Png, double LayoutMs) Render(ScreenEntry entry, FakeScreenVariant variant)
    {
        var control = ScreenPages.Build(entry, variant);
        var window = new Window
        {
            Width = ScreenGoldens.Width,
            Height = ScreenGoldens.Height,
            // The gallery window paints AppBackground under every page; a
            // screen that is HOSTED in the app (a settings tab under the
            // shell's content column) paints none of its own, and on the
            // default white window its light-on-dark text is unreadable.
            Background = AppBackground(),
            Content = control
        };
        var clock = Stopwatch.StartNew();
        window.Show();
        try
        {
            window.UpdateLayout();
            var layoutMs = clock.Elapsed.TotalMilliseconds;
            Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
            for (var pass = 0; pass < 3; pass++) window.UpdateLayout();

            using var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            using var stream = new MemoryStream();
            frame.Save(stream, new PngBitmapEncoderOptions());
            return (stream.ToArray(), layoutMs);
        }
        finally
        {
            window.Close();
        }
    }

    private static void WriteFailure(string relative, byte[] png, byte[]? diff, string? goldenPath, string status)
    {
        var stem = Path.Combine(ScreenGoldens.FailureDirectory, relative[..^".png".Length]);
        Directory.CreateDirectory(Path.GetDirectoryName(stem)!);
        File.WriteAllBytes(stem + ".actual.png", png);
        if (diff is not null) File.WriteAllBytes(stem + ".diff.png", diff);
        ScreenGoldens.AppendToIndex(ScreenGoldens.FailureDirectory, relative, status, goldenPath);
    }

    private static void Stage(string relative, byte[] png, ScreenGoldens.Frame? expected, ScreenGoldens.Frame actual,
        double ratio, bool isNew)
    {
        var stageRoot = Environment.GetEnvironmentVariable(ScreenGoldens.StageDirEnvVar);
        if (string.IsNullOrWhiteSpace(stageRoot)) return;

        var target = Path.Combine(stageRoot, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllBytes(target, png);

        var status = isNew ? "new" : ScreenGoldens.Matches(ratio) ? "same" : "changed";
        if (status == "changed") File.WriteAllBytes(Path.ChangeExtension(target, ".diff.png"), ScreenGoldens.DiffPng(expected!, actual));
        File.WriteAllText(Path.ChangeExtension(target, ".stage.json"), JsonSerializer.Serialize(new
        {
            relative,
            status,
            differingShare = ratio
        }));
    }
}
