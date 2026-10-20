using System.Text.RegularExpressions;

namespace DotCharter.Tests.UI;

/// <summary>
///     Static perf audit 2026-08-29 P1 #16c: the Play Mode settings modal (now
///     <c>PlayModeSettingsViewModel</c>, #658) searched the gameplay-settings
///     catalogue and refilled the bound results per keystroke, while Song Select
///     (<c>SongSelectViewModel</c>) already
///     coalesced typing through <c>CoalescedFilterPass</c>. Source lint — the
///     view model takes the whole Play Mode graph; the behaviour is exercised
///     by <c>PlayModeScreensUiPerfSmokeTests.SettingsModal_…</c>, which drains
///     the Background post before reading the results.
/// </summary>
public sealed class GameplaySettingsSearchCoalescingTests
{
    [Fact]
    public void TheQuerySetter_RequestsOneCoalescedPass_InsteadOfSearchingInline()
    {
        var src = UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "ViewModels", "PlayMode",
            "PlayModeSettingsViewModel.cs")));
        src = Regex.Replace(src, @"^\s*//.*$", "", RegexOptions.Multiline);

        var setter = Body(src, "partial void OnGameplaySettingsQueryChanged(string value)");
        Assert.Contains("GameplaySettingsSearchPass.Request();", setter);
        Assert.DoesNotContain("GameplaySettingsCatalog.Search(", setter);
        Assert.DoesNotContain("GameplaySettingsResults.Clear()", setter);

        // The pass itself does the search, exactly once per drain.
        var pass = Body(src, "private void ApplyGameplaySettingsQuery()");
        Assert.Contains("GameplaySettingsCatalog.Search(GameplaySettingsQuery, ", pass);
        Assert.Contains("new Tone.UI.ViewModels.Shell.CoalescedFilterPass(ApplyGameplaySettingsQuery)", src);
    }

    private static string Body(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{signature}' has moved or been renamed — repoint this lint.");
        var open = source.IndexOf('{', start + signature.Length);
        var depth = 0;
        for (var i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return source[open..i];
        }

        throw new InvalidOperationException($"Unbalanced braces after '{signature}'.");
    }
}
