using System.Text.RegularExpressions;

namespace DotCharter.Tests.UI;

/// <summary>
///     A binding's <c>FallbackValue</c> / <c>TargetNullValue</c> written in markup is a
///     STRING that Avalonia converts to the target type with the CURRENT culture at runtime.
///     <c>FallbackValue=0.3</c> therefore parses in en-US and fails in de-DE ("Could not
///     convert FallbackValue '0.3' to 'System.Double'"): a binding error on every build of
///     the view and the property's value lost (#135, the rig's cabinet mic). A decimal
///     fallback is written as a typed value instead — <c>{x:Static …}</c> of a const beside
///     the control. Integers parse in every culture and stay allowed.
/// </summary>
public sealed class XamlCultureLiteralLintTests
{
    // A decimal literal as the whole fallback: "0.3", "-1.5", ".25".
    private static readonly Regex DecimalFallback =
        new(@"\b(?<kind>FallbackValue|TargetNullValue)\s*=\s*'?(?<value>-?\d*\.\d+)'?\s*[,}]");

    [Fact]
    public void NoBindingFallback_IsACultureSensitiveDecimalLiteral()
    {
        var root = TestPaths.RepoRoot();
        var scanned = 0;
        var offenders = new List<string>();
        foreach (var project in Directory.GetDirectories(root, "DotCharter.*"))
        {
            foreach (var file in UiSourceTree.EnumerateFiles(UiSourceTree.Resolve(project), "*.axaml", SearchOption.AllDirectories))
            {
                var inBuildOutput = file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") ||
                                    file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}");
                if (inBuildOutput) continue;
                scanned++;
                var lines = File.ReadAllLines(file);
                for (var i = 0; i < lines.Length; i++)
                {
                    foreach (Match m in DecimalFallback.Matches(lines[i]))
                    {
                        offenders.Add($"{Path.GetRelativePath(root, file)}:{i + 1} " +
                                      $"{m.Groups["kind"].Value}={m.Groups["value"].Value}");
                    }
                }
            }
        }

        Assert.True(scanned > 100, $"only {scanned} .axaml files scanned — the walk no longer finds the views");
        Assert.True(offenders.Count == 0,
            "decimal binding fallbacks parse with the current culture and fail in de-DE; use {x:Static} of a typed const:\n  " +
            string.Join("\n  ", offenders));
    }

    [Theory]
    [InlineData("Position=\"{Binding CabMic.Position, Mode=TwoWay, FallbackValue=0.3}\"", true)]
    [InlineData("Opacity=\"{Binding Fade, TargetNullValue='.25'}\"", true)]
    [InlineData("DistanceCm=\"{Binding CabMic.DistanceCm, FallbackValue=5}\"", false)]
    [InlineData("Position=\"{Binding CabMic.Position, FallbackValue={x:Static fire:RigMicStage.DefaultPosition}}\"", false)]
    public void ThePattern_FlagsDecimalsOnly(string markup, bool flagged) =>
        Assert.Equal(flagged, DecimalFallback.IsMatch(markup));
}
