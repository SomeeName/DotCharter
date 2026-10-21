using System.Text.RegularExpressions;

namespace DotCharter.Tests.Infrastructure;

/// <summary>
///     A PowerShell <c>if</c> expression unwraps a one-element array into its element, so
///     <c>$Args = if (x) { @() } else { @('-p:A=b') }</c> holds the STRING <c>-p:A=b</c>, and
///     <c>@Args</c> then splats it one character per argument. MSBuild reads that as
///     <c>- p : A = b</c> and fails the publish. An argument list that is splatted must be
///     assigned through <c>@( … )</c>.
/// </summary>
public sealed partial class ScriptSplatArrayLintTests
{
    [GeneratedRegex(@"^\s*\$(?<name>\w+)\s*=\s*(?:if|switch)\b", RegexOptions.Multiline)]
    private static partial Regex ConditionalAssignment();

    public static IEnumerable<string> Offenders(string text)
    {
        foreach (Match m in ConditionalAssignment().Matches(text))
        {
            var name = m.Groups["name"].Value;
            if (Regex.IsMatch(text, $@"(?<![\w$])@{Regex.Escape(name)}\b")) yield return name;
        }
    }

    [Fact]
    public void A_splatted_argument_list_is_never_assigned_from_a_bare_if()
    {
        var root = Path.Combine(TestPaths.RepoRoot(), "tools");
        var found = Directory.EnumerateFiles(root, "*.ps*1", SearchOption.AllDirectories)
            .SelectMany(f => Offenders(File.ReadAllText(f)).Select(n => $"{Path.GetRelativePath(TestPaths.RepoRoot(), f)}: ${n}"))
            .ToList();

        Assert.True(found.Count == 0, "assign these through @( ... ) before splatting them:\n" + string.Join("\n", found));
    }

    [Fact]
    public void The_lint_catches_the_unwrapping_form_and_passes_the_wrapped_one()
    {
        Assert.Equal(["LockedRestoreArgs"],
            Offenders("$LockedRestoreArgs = if ($x) { @() } else { @('-p:A=b') }\n& dotnet publish @LockedRestoreArgs\n"));
        Assert.Empty(Offenders("$LockedRestoreArgs = @(if (-not $x) { '-p:A=b' })\n& dotnet publish @LockedRestoreArgs\n"));
    }
}
