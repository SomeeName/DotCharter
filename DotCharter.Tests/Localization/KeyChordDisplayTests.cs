using System.Text.RegularExpressions;
using Avalonia.Input;
using DotCharter.Localization;

namespace DotCharter.Tests.Localization;

/// <summary>
///     <para>
///         A keybind label names KEYS, and a German keyboard has no key marked
///         "Ctrl" — the cap says <i>Strg</i>. <c>KeyChord</c> hard-coded
///         "Ctrl" / "Alt" / "Shift" until 2026-08-29, so the settings Keybinds
///         tab, the menu accelerators, the command palette and the shortcuts
///         dialog all printed English modifier names in every language.
///     </para>
///     <para>
///         Only the MODIFIER half is language. <c>FormatKey</c> already turns
///         every digit, Oem key and numpad key into the character printed on
///         the cap, and that character is the same everywhere.
///     </para>
///     <para>
///         The split matters as much as the translation:
///         <see cref="KeyChord.ToString" /> stays INVARIANT and
///         <see cref="KeyChord.ToDisplayString" /> is the localized one. Making
///         <c>ToString</c> follow the UI language would have put process-global
///         state inside a method `Services/Configuration/KeyChordTests` asserts
///         on in the default gate — the exact flake the
///         <c>LocalizationSingleton</c> collection exists to stop, and that
///         class is not in it (nor should it be: it does not MOVE the culture,
///         it would merely be a victim).
///     </para>
/// </summary>
[Collection("LocalizationSingleton")]
public sealed class KeyChordDisplayTests
{
    [Fact]
    public void TheModifiersFollowTheUiLanguage_ButTheKeyDoesNot()
    {
        try
        {
            var chord = new KeyChord(Key.S, KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Shift);

            LocalizationService.Instance.SetConfiguredCulture("en-US");
            Assert.Equal("Ctrl+Alt+Shift+S", chord.ToDisplayString(LocalizationService.Instance));

            LocalizationService.Instance.SetConfiguredCulture("de-DE");
            var german = chord.ToDisplayString(LocalizationService.Instance);

            Assert.Equal("Strg+Alt+Umschalt+S", german);
            Assert.EndsWith("+S", german, StringComparison.Ordinal); // the CAP is not translated
        }
        finally
        {
            LocalizationService.Instance.SetConfiguredCulture("en-US");
        }
    }

    /// <summary>
    ///     <c>ToString</c> is the invariant form and must stay that way — it is
    ///     what logs, failure messages and the default gate read.
    /// </summary>
    [Fact]
    public void ToString_StaysInvariant_WhateverTheUiLanguageIs()
    {
        try
        {
            var chord = new KeyChord(Key.S, KeyModifiers.Control | KeyModifiers.Shift);

            LocalizationService.Instance.SetConfiguredCulture("de-DE");
            Assert.Equal("Ctrl+Shift+S", chord.ToString());

            LocalizationService.Instance.SetConfiguredCulture("ja-JP");
            Assert.Equal("Ctrl+Shift+S", chord.ToString());
        }
        finally
        {
            LocalizationService.Instance.SetConfiguredCulture("en-US");
        }
    }

    /// <summary>
    ///     Every surface a user reads a chord on goes through the LOCALIZED
    ///     method. A source lint, because calling the wrong one of two
    ///     one-line methods compiles perfectly and simply prints English.
    /// </summary>
    [Fact]
    public void EveryDisplaySite_UsesTheLocalizedForm()
    {
        var root = TestPaths.RepoRoot();
        string[] displaySites =
        [
            UiSourceTree.Resolve(Path.Combine("DotCharter.UI", "Controls", "KeybindCaptureBox.cs")),
            UiSourceTree.Resolve(Path.Combine("DotCharter.UI", "Converters", "KeyChordToGestureConverter.cs")),
            UiSourceTree.Resolve(Path.Combine("DotCharter.UI", "ViewModels", "CommandPalette", "CommandPaletteViewModel.cs")),
            UiSourceTree.Resolve(Path.Combine("DotCharter.UI", "ViewModels", "CommandPalette", "ShortcutEntries.cs"))
        ];

        var offenders = new List<string>();
        foreach (var relative in displaySites)
        {
            var path = Path.Combine(root, relative);
            Assert.True(File.Exists(path), $"{relative} moved — repoint this lint at it.");

            var text = SourceText.StripComments(UiSourceTree.ReadAllText(path));

            // Non-vacuous: the site really does still render a chord.
            Assert.Contains("ToDisplayString(", text, StringComparison.Ordinal);

            if (Regex.IsMatch(text, @"\b(?:chord|Chord)\.ToString\s*\(\s*\)"))
                offenders.Add(relative);
        }

        Assert.True(offenders.Count == 0,
            "These render a chord for the screen with the INVARIANT ToString(); use "
            + "ToDisplayString(localization) so the modifiers follow the UI language:"
            + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }
}
