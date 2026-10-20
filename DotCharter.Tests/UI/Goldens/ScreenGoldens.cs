using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DotCharter.Tests.Rendering.Headless;
using SkiaSharp;

namespace DotCharter.Tests.UI.Goldens;

/// <summary>
///     The pure half of the golden-screenshot harness (target-architecture.md § 5):
///     where the approved PNGs live, how two frames are compared, the diff image,
///     and the sidecar every approved PNG carries. The headless half is
///     <c>Perf/ScreenGoldenUiPerfSmokeTests</c>; the owner's tool is
///     <c>tools/agent/Update-Goldens.ps1</c>.
/// </summary>
public static class ScreenGoldens
{
    public const int Width = 1600;
    public const int Height = 900;

    /// <summary>A pixel counts as different when any channel moved by MORE than this (of 255).</summary>
    public const int ChannelTolerance = 8;

    /// <summary>A case fails when more than this share of its pixels differ — ≈ 720 of 1.44 M.</summary>
    public const double MaxDifferingShare = 0.0005;

    /// <summary>When set, every case also writes its render, a diff and a status file under this folder.</summary>
    public const string StageDirEnvVar = "DOTCHARTER_GOLDEN_STAGE_DIR";

    public const string NoGoldenMessage =
        "no golden — run Update-Goldens (tools/agent/Update-Goldens.ps1 -Stage, then the owner runs -Accept)";

    /// <summary><c>DotCharter.Tests/Goldens/ui/</c> in the source tree — read in place, never copied to the output.</summary>
    public static string GoldensRoot => Path.Combine(TestPaths.RepoRoot(), "DotCharter.Tests", "Goldens", "ui");

    /// <summary>Where a failing case writes <c>.actual.png</c>, <c>.diff.png</c> and the <c>index.html</c> over them.</summary>
    public static string FailureDirectory => Path.Combine(AppContext.BaseDirectory, "TestResults", "ui-goldens");

    public sealed record Frame(int Width, int Height, byte[] Bgra);

    public sealed record Sidecar(string Commit, string AcceptedOn, string Sha256);

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static Frame Decode(byte[] png)
    {
        using var decoded = SKBitmap.Decode(png)
                            ?? throw new InvalidDataException("not a decodable PNG");
        using var bgra = decoded.Copy(SKColorType.Bgra8888)
                         ?? throw new InvalidDataException("the PNG cannot be converted to BGRA");
        return new Frame(bgra.Width, bgra.Height, bgra.Bytes);
    }

    /// <summary>Share of pixels differing by more than <see cref="ChannelTolerance" />; 1 when the sizes differ.</summary>
    public static double DifferenceRatio(Frame expected, Frame actual) =>
        expected.Width != actual.Width || expected.Height != actual.Height
            ? 1.0
            : GoldenFrame.DifferenceRatio(expected.Bgra, actual.Bgra, ChannelTolerance);

    public static bool Matches(double ratio) => ratio <= MaxDifferingShare;

    /// <summary>The actual frame dimmed to a third, every differing pixel painted magenta. A size change paints it all.</summary>
    public static byte[] DiffPng(Frame expected, Frame actual)
    {
        var sameSize = expected.Width == actual.Width && expected.Height == actual.Height;
        var pixels = new byte[actual.Bgra.Length];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var differs = !sameSize
                          || Math.Abs(expected.Bgra[i] - actual.Bgra[i]) > ChannelTolerance
                          || Math.Abs(expected.Bgra[i + 1] - actual.Bgra[i + 1]) > ChannelTolerance
                          || Math.Abs(expected.Bgra[i + 2] - actual.Bgra[i + 2]) > ChannelTolerance;
            pixels[i] = differs ? (byte)255 : (byte)(actual.Bgra[i] / 3);
            pixels[i + 1] = differs ? (byte)0 : (byte)(actual.Bgra[i + 1] / 3);
            pixels[i + 2] = differs ? (byte)255 : (byte)(actual.Bgra[i + 2] / 3);
            pixels[i + 3] = 255;
        }

        return EncodePng(actual.Width, actual.Height, pixels);
    }

    public static byte[] EncodePng(int width, int height, byte[] bgra)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        System.Runtime.InteropServices.Marshal.Copy(bgra, 0, bitmap.GetPixels(), bgra.Length);
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    public static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    public static string SidecarPathOf(string png) => Path.ChangeExtension(png, ".json");

    public static Sidecar NewSidecar(byte[] png, string commit, DateTimeOffset acceptedOn) =>
        new(commit, acceptedOn.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), Sha256(png));

    public static string Serialize(Sidecar sidecar) => JsonSerializer.Serialize(sidecar, Json) + "\n";

    /// <summary>
    ///     Every approved PNG under <paramref name="root" /> has a sidecar whose
    ///     <c>sha256</c> is the PNG's own, and every sidecar has its PNG. One
    ///     message per offence; empty when the store is sound.
    /// </summary>
    public static List<string> SidecarViolations(string root)
    {
        var violations = new List<string>();
        if (!Directory.Exists(root)) return violations;

        foreach (var png in Directory.EnumerateFiles(root, "*.png", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var name = Path.GetRelativePath(root, png).Replace('\\', '/');
            var sidecar = SidecarPathOf(png);
            if (!File.Exists(sidecar))
            {
                violations.Add($"{name}: no sidecar {Path.GetFileName(sidecar)} — a golden is accepted with Update-Goldens.ps1 -Accept, never copied in");
                continue;
            }

            Sidecar? parsed;
            try
            {
                parsed = JsonSerializer.Deserialize<Sidecar>(File.ReadAllText(sidecar), Json);
            }
            catch (JsonException ex)
            {
                violations.Add($"{name}: sidecar does not parse ({ex.Message})");
                continue;
            }

            if (parsed is null || string.IsNullOrWhiteSpace(parsed.Commit) || string.IsNullOrWhiteSpace(parsed.AcceptedOn))
                violations.Add($"{name}: sidecar lacks commit or acceptedOn");
            else if (!string.Equals(parsed.Sha256, Sha256(File.ReadAllBytes(png)), StringComparison.OrdinalIgnoreCase))
                violations.Add($"{name}: the PNG's sha256 is not the one its sidecar approved — re-accept it with Update-Goldens.ps1");
        }

        foreach (var json in Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            if (!File.Exists(Path.ChangeExtension(json, ".png")))
                violations.Add($"{Path.GetRelativePath(root, json).Replace('\\', '/')}: sidecar with no PNG");
        }

        return violations;
    }

    /// <summary>Appends one failing case to <c>index.html</c> in <paramref name="directory" />, creating the page on first use.</summary>
    public static void AppendToIndex(string directory, string relative, string status, string? expectedPath)
    {
        Directory.CreateDirectory(directory);
        var index = Path.Combine(directory, "index.html");
        if (!File.Exists(index))
        {
            File.WriteAllText(index,
                "<!doctype html><meta charset=\"utf-8\"><title>UI golden failures</title>" +
                "<style>body{background:#111;color:#eee;font:13px system-ui;margin:20px}" +
                "figure{display:inline-block;margin:0 12px 24px 0;vertical-align:top}img{width:480px;border:1px solid #444}" +
                "h2{font:600 14px monospace;margin:24px 0 8px}</style><h1>UI golden failures</h1>\n");
        }

        var stem = relative[..^".png".Length];
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"<h2>{relative} — {status}</h2>");
        if (expectedPath is not null)
            sb.Append(CultureInfo.InvariantCulture, $"<figure><img src=\"{new Uri(expectedPath).AbsoluteUri}\"><figcaption>approved</figcaption></figure>");
        sb.Append(CultureInfo.InvariantCulture, $"<figure><img src=\"{stem}.actual.png\"><figcaption>actual</figcaption></figure>");
        if (expectedPath is not null)
            sb.Append(CultureInfo.InvariantCulture, $"<figure><img src=\"{stem}.diff.png\"><figcaption>diff</figcaption></figure>");
        File.AppendAllText(index, sb.Append('\n').ToString());
    }
}
