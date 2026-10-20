using System.Reflection;
using Avalonia.Input;
using DotCharter.Services.Editing;

namespace DotCharter.Tests.UI;

/// <summary>
///     The shipped keybind table, guarded three ways.
///     <para>
///         ~25 editing actions used to ship <c>KeyChord.None</c> — every "group"
///         technique toggle, every <c>SetFinger*</c>,
///         <c>ApplyHandShapeTemplateToChords</c>, <c>ToggleChordSplit</c>,
///         <c>ConvertToPowerChord*</c> — so a large part of the vocabulary was
///         unreachable unless the user went looking in the Keybinds settings tab
///         (editing-rework audit 03 §0(b), §8 stage 0). Binding them is only safe
///         if nothing collides: <c>ChartKeybindHandler.RefreshBinds</c> resolves a
///         collision last-registration-wins with a log line and no UI.
///     </para>
///     <para>
///         The reachability checks are source lints, the same shape as
///         <c>PlaybackGateHoldGuardTests</c> — <c>AppKeybindRegistrar</c> needs
///         the whole app booted to construct, and the Keybinds tab is markup.
///     </para>
/// </summary>
public sealed class KeybindDefaultsTests
{
    /// <summary>Chords claimed by the MainWindow tunnel rather than the keybind
    /// registry (<c>GlobalChordGate</c>), so a config default landing on one
    /// would be unreachable.</summary>
    private static readonly (Key Key, KeyModifiers Modifiers)[] ReservedByTheTunnel =
    [
        (Key.P, KeyModifiers.Control | KeyModifiers.Shift), // command palette
        (Key.F11, KeyModifiers.None), // fullscreen
        (Key.Enter, KeyModifiers.Alt) // fullscreen
    ];

    /// <summary>Fixed (non-rebindable) registrations in
    /// <c>AppKeybindRegistrar.RegisterAll</c>.</summary>
    private static readonly (Key Key, KeyModifiers Modifiers)[] FixedRegistrations =
    [
        (Key.F12, KeyModifiers.None), // highway screenshot
        (Key.Add, KeyModifiers.None), // numpad speed up
        (Key.Subtract, KeyModifiers.None) // numpad speed down
    ];

    /// <summary>Properties whose chord is a TEMPLATE: only the modifiers are
    /// used, the key is derived per digit by a loop in the registrar.
    /// <para>
    ///     The two string families are sized from <c>StringEditMask.MaxStrings</c>
    ///     rather than a literal 9, because that constant is what the registrar's
    ///     loops actually count to — hard-coding it here would let a bump go
    ///     unnoticed in exactly the file whose job is to notice
    ///     (see <see cref="EveryDigitFamilyStaysInsideTheDigitKeys" />).
    /// </para></summary>
    private static readonly Dictionary<string, (Key First, int Count)> DigitFamilies = new()
    {
        ["BookmarkBase"] = (Key.D0, 10),
        ["SetFretBase"] = (Key.D0, 10),
        ["StringMaskBase"] = (Key.D1, StringEditMask.MaxStrings),
        ["PenToggleStringBase"] = (Key.D1, StringEditMask.MaxStrings)
    };

    /// <summary>
    ///     A digit family walks <c>(Key)((int)Key.D0 + i)</c>, and
    ///     <see cref="Key" /> is a plain enum — <c>Key.D9 + 1</c> is
    ///     <see cref="Key.A" />, not an error. So raising
    ///     <c>StringEditMask.MaxStrings</c> to 10 would silently bind
    ///     <c>Ctrl+A</c> (shadowing <c>SelectAll</c>) and a BARE <c>A</c>,
    ///     and <see cref="NoTwoDefaultChordsCollide" /> would report it as a
    ///     collision with a confusing name rather than as what it is. Fail here,
    ///     where the message says what happened.
    /// </summary>
    [Fact]
    public void EveryDigitFamilyStaysInsideTheDigitKeys()
    {
        foreach (var (name, (first, count)) in DigitFamilies)
        {
            var last = (Key)((int)first + count - 1);
            Assert.True(first >= Key.D0 && last <= Key.D9,
                $"The {name} family spans {first}..{last}, which leaves the digit row. The registrar derives its " +
                "key with `(Key)((int)Key.D0 + i)` on a plain enum, so running off the end binds letters " +
                $"({Key.D9 + 1} follows {Key.D9}) instead of failing.");
        }
    }

    [Fact]
    public void EveryEditingActionShipsBound()
    {
        var unbound = KeyChordProperties()
            .Where(p => !((KeyChord)p.GetValue(new KeybindConfig())!).IsBound)
            .Select(p => p.Name)
            .ToList();

        Assert.True(unbound.Count == 0,
            "These actions ship UNBOUND, so they are unreachable without a trip to the Keybinds " +
            "settings tab — the gap Stage 0 of the editing rework closed: " + string.Join(", ", unbound));
    }

    [Fact]
    public void NoTwoDefaultChordsCollide()
    {
        var seen = new Dictionary<string, string>();
        var collisions = new List<string>();

        void Claim(Key key, KeyModifiers modifiers, string owner)
        {
            var hash = Hash(key, modifiers);
            if (seen.TryGetValue(hash, out var existing))
                collisions.Add($"{hash}: {existing} vs {owner}");
            else
                seen[hash] = owner;
        }

        foreach (var (key, modifiers) in FixedRegistrations) Claim(key, modifiers, "<fixed registration>");
        foreach (var (key, modifiers) in ReservedByTheTunnel) Claim(key, modifiers, "<window tunnel>");

        var defaults = new KeybindConfig();
        foreach (var property in KeyChordProperties())
        {
            var chord = (KeyChord)property.GetValue(defaults)!;
            if (!chord.IsBound) continue;

            if (DigitFamilies.TryGetValue(property.Name, out var family))
            {
                for (var i = 0; i < family.Count; i++)
                    Claim((Key)((int)family.First + i), chord.Modifiers, $"{property.Name}[{i}]");
            }
            else
            {
                Claim(chord.Key, chord.Modifiers, property.Name);
            }
        }

        Assert.True(collisions.Count == 0,
            "Colliding default keybinds resolve last-registration-wins with only a log line, so one of " +
            "each pair is silently dead:\n" + string.Join("\n", collisions));
    }

    [Fact]
    public void EveryKeybindIsRegisteredByTheRegistrar()
    {
        // Two registration sites, both feeding the same IKeybindRegistry:
        // AppKeybindRegistrar for everything editor-facing, and
        // MainWindowViewModel for Escape (whose handler is the shell's
        // dialog → pause → back → clear-selection priority chain).
        var sources = string.Concat(
            UiSourceTree.ReadAllText(RepoPath("DotCharter.UI/Input/AppKeybindRegistrar.cs")),
            UiSourceTree.ReadAllText(RepoPath("DotCharter.UI/ViewModels/Shell/MainWindowViewModel.cs")));

        var missing = KeyChordProperties()
            .Where(p => !sources.Contains("." + p.Name, StringComparison.Ordinal))
            .Select(p => p.Name)
            .ToList();

        Assert.True(missing.Count == 0,
            "These KeybindConfig properties are never read by AppKeybindRegistrar — they reserve a chord " +
            "and do nothing, which is exactly what the dead `ToggleMidi` binding was: " +
            string.Join(", ", missing));
    }

    /// <summary>The Keybinds tab's rows are DATA (<c>KeybindRowCatalog</c>), so the
    /// row for a chord is a line in that catalogue — in both directions: a chord
    /// with no row cannot be rebound, and a row over no chord binds nothing.</summary>
    [Fact]
    public void EveryKeybindHasASettingsRow()
    {
        var rows = DotCharter.Presentation.Screens.Settings.KeybindRowCatalog.Rows.Select(r => r.Property).ToList();
        var chords = KeyChordProperties().Select(p => p.Name).ToList();

        var missing = chords.Except(rows, StringComparer.Ordinal).ToList();
        Assert.True(missing.Count == 0,
            "These keybinds cannot be rebound — no row in KeybindRowCatalog: " + string.Join(", ", missing));

        var stray = rows.Except(chords, StringComparer.Ordinal).ToList();
        Assert.True(stray.Count == 0,
            "These KeybindRowCatalog rows name no KeyChord property of KeybindConfig: " + string.Join(", ", stray));
        Assert.Equal(rows.Count, rows.Distinct(StringComparer.Ordinal).Count());
    }

    private static IEnumerable<PropertyInfo> KeyChordProperties() =>
        typeof(KeybindConfig)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType == typeof(KeyChord) && p.CanRead && p.CanWrite)
            .OrderBy(p => p.Name, StringComparer.Ordinal);

    /// <summary>Mirrors <c>ChartKeybindHandler.GetKeyHash</c> — note that Win/Meta
    /// is deliberately NOT part of it, so two chords differing only by Win DO
    /// collide.</summary>
    private static string Hash(Key key, KeyModifiers modifiers) =>
        $"{key}_{(modifiers.HasFlag(KeyModifiers.Control) ? "1" : "0")}" +
        $"_{(modifiers.HasFlag(KeyModifiers.Shift) ? "1" : "0")}" +
        $"_{(modifiers.HasFlag(KeyModifiers.Alt) ? "1" : "0")}";

    private static string RepoPath(string relative) =>
        Path.Combine(TestPaths.RepoRoot(), relative.Replace('/', Path.DirectorySeparatorChar));
}
