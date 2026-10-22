using System.Text.RegularExpressions;
using DotCharter.Tests.Perf;

namespace DotCharter.Tests.UI.Goldens;

/// <summary>
///     The default-gate half of the golden screenshots (target-architecture.md § 5):
///     the approved store is sound (every PNG has a sidecar whose hash is its own),
///     the comparison bites where § 5 says it does, and the golden class runs
///     ONLY in its own invocation — excluded by its exact name everywhere the
///     headless suite runs, so the exclusion can swallow no other class.
/// </summary>
public sealed class ScreenGoldenStoreTests
{
    [Fact]
    public void Every_approved_golden_has_a_sidecar_whose_hash_is_its_own()
    {
        var violations = ScreenGoldens.SidecarViolations(ScreenGoldens.GoldensRoot);
        Assert.True(violations.Count == 0, "DotCharter.Tests/Goldens/ui:\n  " + string.Join("\n  ", violations));
    }

    [Fact]
    public void Probe_a_missing_sidecar_a_wrong_hash_and_an_orphaned_sidecar_are_each_named()
    {
        var root = Directory.CreateTempSubdirectory("dotcharter-goldens-probe-").FullName;
        try
        {
            var png = ScreenGoldens.EncodePng(2, 2, new byte[16]);
            var other = ScreenGoldens.EncodePng(2, 2, Enumerable.Repeat((byte)200, 16).ToArray());
            Directory.CreateDirectory(Path.Combine(root, "area"));

            File.WriteAllBytes(Path.Combine(root, "area", "good--default.png"), png);
            File.WriteAllText(Path.Combine(root, "area", "good--default.json"),
                ScreenGoldens.Serialize(ScreenGoldens.NewSidecar(png, "abc123", DateTimeOffset.UnixEpoch)));
            File.WriteAllBytes(Path.Combine(root, "area", "unsigned--default.png"), png);
            File.WriteAllBytes(Path.Combine(root, "area", "edited--default.png"), other);
            File.WriteAllText(Path.Combine(root, "area", "edited--default.json"),
                ScreenGoldens.Serialize(ScreenGoldens.NewSidecar(png, "abc123", DateTimeOffset.UnixEpoch)));
            File.WriteAllText(Path.Combine(root, "area", "orphan--default.json"),
                ScreenGoldens.Serialize(ScreenGoldens.NewSidecar(png, "abc123", DateTimeOffset.UnixEpoch)));

            var violations = ScreenGoldens.SidecarViolations(root);

            Assert.Equal(3, violations.Count);
            Assert.Contains(violations, v => v.StartsWith("area/unsigned--default.png: no sidecar", StringComparison.Ordinal));
            Assert.Contains(violations, v => v.StartsWith("area/edited--default.png: the PNG's sha256", StringComparison.Ordinal));
            Assert.Contains(violations, v => v.StartsWith("area/orphan--default.json: sidecar with no PNG", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Probe_the_comparison_absorbs_jitter_and_fails_on_a_moved_label()
    {
        var baseline = Frame((_, _) => (40, 40, 40));

        Assert.Equal(0, ScreenGoldens.DifferenceRatio(baseline, Frame((_, _) => (48, 40, 32))));

        // 700 stray pixels (under the ≈ 720 of § 5) pass; a 40 × 40 block (1,600) fails.
        var jitter = Frame((x, y) => y == 0 && x < 700 ? Bgr(200, 40, 40) : Bgr(40, 40, 40));
        Assert.True(ScreenGoldens.Matches(ScreenGoldens.DifferenceRatio(baseline, jitter)));
        var moved = Frame((x, y) => x is >= 100 and < 140 && y is >= 100 and < 140 ? Bgr(230, 230, 230) : Bgr(40, 40, 40));
        Assert.False(ScreenGoldens.Matches(ScreenGoldens.DifferenceRatio(baseline, moved)));

        Assert.Equal(1.0, ScreenGoldens.DifferenceRatio(baseline, new ScreenGoldens.Frame(10, 10, new byte[400])));
    }

    [Fact]
    public void Probe_the_diff_image_marks_exactly_the_differing_pixels()
    {
        var expected = new ScreenGoldens.Frame(2, 1, [10, 10, 10, 255, 10, 10, 10, 255]);
        var actual = new ScreenGoldens.Frame(2, 1, [10, 10, 10, 255, 90, 10, 10, 255]);

        var diff = ScreenGoldens.Decode(ScreenGoldens.DiffPng(expected, actual));

        Assert.Equal((2, 1), (diff.Width, diff.Height));
        Assert.Equal(new byte[] { 3, 3, 3 }, diff.Bgra[..3]);
        Assert.Equal(new byte[] { 255, 0, 255 }, diff.Bgra[4..7]);
    }

    // ── the invocation: its own run, and excluded by its EXACT name ────────

    [Fact]
    public void The_golden_class_is_excluded_by_its_exact_name_wherever_the_headless_suite_runs()
    {
        Assert.Equal(ScreenGoldenUiPerfSmokeTests.FullName, typeof(ScreenGoldenUiPerfSmokeTests).FullName);
        Assert.DoesNotContain("*", ScreenGoldenUiPerfSmokeTests.FullName, StringComparison.Ordinal);
        var exclusion = $"'--filter-not-class', '{ScreenGoldenUiPerfSmokeTests.FullName}'";

        var gate = Script("tools", "agent", "Invoke-Gate.ps1");
        Assert.Contains("$HeadlessFilter = @('--filter-class', '*UiPerfSmokeTests', '--filter-not-class', $GoldenClass)", gate, StringComparison.Ordinal);
        // Both ways the headless suite starts (alone, or alongside the gate) use that filter.
        Assert.Matches(new Regex(@"if \(\$Headless\)[^\n]*Invoke-HeadlessShardedRun 'headless' \$HeadlessFilter"), gate);
        Assert.Contains("Start-HeadlessShardedRun 'headless' $HeadlessFilter", gate, StringComparison.Ordinal);
        Assert.Contains($"$GoldenClass = '{ScreenGoldenUiPerfSmokeTests.FullName}'", gate, StringComparison.Ordinal);
        Assert.Matches(new Regex(@"if \(\$Goldens\) \{.*?Invoke-TestRun 'goldens' @\('--filter-class', \$GoldenClass\)", RegexOptions.Singleline), gate);
        Assert.Matches(new Regex(@"if \(\$Goldens\) \{.*?\$env:DOTCHARTER_UI_SWEEP_DUMP = '1'.*?Invoke-TestRun 'goldens'", RegexOptions.Singleline), gate);

        Assert.Contains($"[string[]]$Filter = @('--filter-class', '*UiPerfSmokeTests', {exclusion})",
            Script("tools", "ci", "Invoke-HeadlessUiCi.ps1"), StringComparison.Ordinal);
        Assert.Contains($"$HeadlessUiFilter = @('--filter-class', '*UiPerfSmokeTests', {exclusion})",
            Script("tools", "release", "New-Release.ps1"), StringComparison.Ordinal);
    }

    [Fact]
    public void Only_the_owner_can_accept_a_golden()
    {
        var update = Script("tools", "agent", "Update-Goldens.ps1");
        var refuse = update.IndexOf("if ([Console]::IsInputRedirected", StringComparison.Ordinal);
        var ask = update.IndexOf("= Read-Host ", StringComparison.Ordinal);
        var firstCopy = update.IndexOf("Copy-Item -LiteralPath", StringComparison.Ordinal);
        Assert.True(refuse > 0 && ask > refuse && firstCopy > ask,
            "Update-Goldens.ps1 -Accept must refuse a non-interactive console, then ask, and only then copy");
        Assert.Single(Regex.Matches(update, "Copy-Item"));
    }

    private static (byte B, byte G, byte R) Bgr(byte b, byte g, byte r) => (b, g, r);

    private static ScreenGoldens.Frame Frame(Func<int, int, (byte B, byte G, byte R)> pixel)
    {
        const int w = ScreenGoldens.Width, h = ScreenGoldens.Height;
        var bgra = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var (b, g, r) = pixel(x, y);
                var i = (y * w + x) * 4;
                (bgra[i], bgra[i + 1], bgra[i + 2], bgra[i + 3]) = (b, g, r, 255);
            }
        }

        return new ScreenGoldens.Frame(w, h, bgra);
    }

    private static string Script(params string[] parts) =>
        File.ReadAllText(Path.Combine([TestPaths.RepoRoot(), .. parts])).Replace("\r\n", "\n", StringComparison.Ordinal);
}
