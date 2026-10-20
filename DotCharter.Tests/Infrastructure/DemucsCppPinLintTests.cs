using System.Text.RegularExpressions;

namespace DotCharter.Tests.Infrastructure;

/// <summary>
///     <c>DEMUCS_CPP_TAG</c> in the native CMake build is the git ref
///     <c>FetchContent</c> clones sevagh/demucs.cpp at. It defaulted to
///     <c>"main"</c> for every release up to v0.48, so two builds of the same
///     commit could compile two different upstreams — and the only signal was a
///     <c>message(WARNING)</c> nobody reads in a CI log. The default must be a
///     40-hex commit SHA (or a <c>v*</c> tag, should upstream ever cut one);
///     a branch name is refused here, in the default gate, where a red build is
///     not ignorable.
/// </summary>
public sealed class DemucsCppPinLintTests
{
    private static readonly Regex DefaultTag = new(
        @"^\s*set\s*\(\s*DEMUCS_CPP_TAG\s+""([^""]*)""\s+CACHE\s+STRING",
        RegexOptions.Multiline | RegexOptions.IgnoreCase);

    [Fact]
    public void The_demucs_cpp_default_ref_is_a_commit_sha_or_a_tag_never_a_branch()
    {
        var path = Path.Combine(TestPaths.RepoRoot(), "JuceSharp", "src", "JuceSharp.Native", "CMakeLists.txt");
        Assert.True(File.Exists(path), $"submodule not checked out: {path}");

        var match = DefaultTag.Match(File.ReadAllText(path));
        Assert.True(match.Success, "no `set(DEMUCS_CPP_TAG \"...\" CACHE STRING ...)` line — the pin moved or was deleted");

        var value = match.Groups[1].Value;
        Assert.True(IsPinned(value),
            $"DEMUCS_CPP_TAG defaults to '{value}', which is a mutable ref. Pin it to a 40-hex commit SHA " +
            "(git ls-remote https://github.com/sevagh/demucs.cpp.git HEAD) or a v* tag.");
    }

    [Theory]
    [InlineData("main", false)]
    [InlineData("master", false)]
    [InlineData("HEAD", false)]
    [InlineData("develop", false)]
    [InlineData("", false)]
    [InlineData("f1206e9", false)] // abbreviated: not unique forever
    [InlineData("f1206e9adeea103aef4a636b9e62297cf1f8e34e", true)]
    [InlineData("F1206E9ADEEA103AEF4A636B9E62297CF1F8E34E", true)]
    [InlineData("v1.0.0", true)]
    [InlineData("v0.2", true)]
    public void The_pin_predicate_accepts_only_shas_and_version_tags(string value, bool expected) =>
        Assert.Equal(expected, IsPinned(value));

    private static bool IsPinned(string value) =>
        Regex.IsMatch(value, "^[0-9a-fA-F]{40}$") || Regex.IsMatch(value, @"^v\d+(\.\d+)*$");
}
