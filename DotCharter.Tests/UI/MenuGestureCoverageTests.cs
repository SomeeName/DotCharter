using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Avalonia.Input;
using DotCharter.UI.Converters;

namespace DotCharter.Tests.UI;

/// <summary>
///     A menu row or toolbar button that runs exactly what a keybind runs SHOWS that keybind,
///     and shows that one only (#131). The menus printed the chord for Undo, Paste or the riff
///     library and printed nothing for the Inspector (F9), the 3D preview (Ctrl+P), the waveform
///     (F5), snap, length, bookmarks or insert/remove beat, so those read as "no shortcut
///     exists". Nothing compared the two lists: <c>AppKeybindRegistrar</c> knows which chord
///     drives which command and <c>MainMenuView</c> did not.
///     <para>
///         The pairing is DERIVED from the registrar's source, not restated: a registration whose
///         action executes a shell command (<c>_shell.XCommand</c>, <c>_transport.XCommand</c>),
///         flips a property (<c>_projectState.P = !_projectState.P</c>) or writes out the very
///         expression an expression-bodied shell <c>[RelayCommand]</c> runs (snap, zoom and the
///         bookmarks call the coordinator directly) drives that command or property, and a shell
///         <c>[RelayCommand] ToggleX() =&gt; P = !P</c> makes <c>ToggleXCommand</c> and <c>P</c>
///         the same verb (the Panels rows bind <c>IsChecked</c>, the keys execute the command).
///         The few left — a block body, or a command on another view model (Undo, Save, Cut…) —
///         are listed in <see cref="Equivalents" /> with a reason, and a stale or derivable entry
///         fails.
///     </para>
///     <para>
///         The label itself is a binding to the LIVE <c>ProjectState.Keybinds</c> table through
///         <c>KeyChordToGestureConverter</c> — the table the registrar re-reads on every
///         <c>RefreshBinds</c> — so a rebind in Settings moves the label with the key. That is
///         why a hand-written literal is refused unless the registrar registers that literal key.
///     </para>
///     <para>
///         Source-shaped for the reason <c>CommandPaletteMenuVerbTests</c> is: the registrar and
///         the shell need the whole app session, which the default gate does not have.
///     </para>
/// </summary>
public sealed class MenuGestureCoverageTests
{
    private const string Converter = "KeyChordToGestureConverter";

    /// <summary>
    ///     Registrations whose action calls a service, not the command the menu or toolbar
    ///     binds — each runs the same verb by a different door. Keyed by <c>KeybindConfig</c>
    ///     property; the value is every shell-relative binding path that verb is reached by.
    /// </summary>
    private static readonly Dictionary<string, (string[] Paths, string Reason)> Equivalents = new(StringComparer.Ordinal)
    {
        ["PlayPause"] = (["Transport.PlayCommand", "Transport.PauseCommand"],
            "one chord toggles; the toolbar shows the two halves as two buttons"),
        ["Undo"] = (["ChartEditor.UndoCommand"], "one HistoryManager: the menu steps it directly, the key through IGlobalEditingCoordinator.Undo"),
        ["Redo"] = (["ChartEditor.RedoCommand"], "one HistoryManager: the menu steps it directly, the key through IGlobalEditingCoordinator.Redo"),
        ["Cut"] = (["CutCommand"], "the shell's Cut is the same copy + delete the registrar writes as a block body"),
        ["SelectAll"] = (["SelectAllCommand"], "the shell's SelectAll is the same focus-area branch the registrar writes as a block body"),
        ["SaveProject"] = (["Project.SaveProjectCommand"], "ProjectMenuViewModel awaits the IProjectSessionService.SaveProjectAsync the key fires"),
        ["SaveProjectAs"] = (["Project.SaveProjectAsCommand"], "ProjectMenuViewModel awaits the IProjectSessionService.SaveProjectAsAsync the key fires"),
        ["OpenProject"] = (["Project.OpenProjectCommand"], "ProjectMenuViewModel awaits the IProjectSessionService.OpenProject the key fires"),
    };

    /// <summary>A literal gesture the registrar does not own, with the reason.</summary>
    private static readonly Dictionary<string, string> UnregisteredLiterals = new(StringComparer.Ordinal)
    {
        ["ExitApplicationCommand"] = "Alt+F4 is the window manager's close; the app registers no chord for it",
    };

    /// <summary>A toolbar button whose chord is printed somewhere other than its tooltip —
    /// keyed by x:Name, valued by the file that must print it.</summary>
    private static readonly Dictionary<string, string> ChordShownElsewhere = new(StringComparer.Ordinal)
    {
        // Undo/redo carry a hover HISTORY flyout instead of a tooltip, and its footer prints the chord.
        ["UndoBtn"] = "UndoPopoverView.axaml",
        ["RedoBtn"] = "RedoPopoverView.axaml",
    };

    [Fact]
    public void TheRegistrarParse_FindsTheBindingsItIsAbout()
    {
        var map = KeybindMap.Load();
        // Spot checks on each derivation route, so a registrar reshape cannot make every
        // other fact here pass vacuously.
        Assert.Contains("ToggleInspectorCommand", map.Targets("ToggleInspector"));
        Assert.Contains("IsInspectorVisible", map.Targets("ToggleInspector"));
        Assert.Contains("ProjectState.ShowWaveform", map.Targets("ToggleWaveform"));
        Assert.Contains("Transport.MetronomeEnabled", map.Targets("ToggleMetronome"));
        Assert.Contains("ShellDialogs.OpenPasteSpecialCommand", map.Targets("PasteSpecial"));
        Assert.Equal("BookmarkBase", map.Families["ToggleBookmarkCommand"]);
        Assert.Equal("CaptureHighwayScreenshotCommand", map.Literals["F12"].Split('.')[^1]);
        Assert.True(map.ChordToTargets.Count > 30,
            $"only {map.ChordToTargets.Count} registrations resolved to a command or property — the parse no longer fits AppKeybindRegistrar");
    }

    [Fact]
    public void EveryMenuRowWithABoundVerb_ShowsItsChord()
    {
        var map = KeybindMap.Load();
        var rows = MenuRows().ToList();
        Assert.True(rows.Count > 100, $"only {rows.Count} menu rows parsed — the pattern no longer fits the menu");

        var failures = rows.Select(row => MenuVerdict(row, map)).OfType<string>().ToList();

        Assert.True(failures.Count == 0,
            $"{failures.Count} menu row(s) disagree with AppKeybindRegistrar:\n  " + string.Join("\n  ", failures));
    }

    /// <summary>What is wrong with <paramref name="row" />'s gesture, or null.</summary>
    private static string? MenuVerdict(MenuRow row, KeybindMap map)
    {
        var expected = map.ExpectedFor(row.Targets);
        var family = row.Command is { } cmd && map.Families.TryGetValue(cmd, out var f) ? f : null;
        var literal = row.Command is { } c ? map.LiteralFor(c) : null;
        var bound = expected.Count > 0 ? string.Join(" / ", expected)
            : family is not null ? $"{family} + {row.Parameter}"
            : literal;

        if (row.Gesture is null) return bound is null ? null : $"{row.Name}: shows nothing, bound to {bound}";

        var shown = ParseGesture(row.Gesture);
        if (shown.Chord is not { } chord)
        {
            if (literal is not null) return literal == row.Gesture ? null : $"{row.Name}: shows \"{row.Gesture}\", bound to {literal}";
            return row.Command is not null && UnregisteredLiterals.ContainsKey(row.Command)
                ? null
                : $"{row.Name}: hand-written \"{row.Gesture}\" that no registration backs — bind ProjectState.Keybinds.<X> through {Converter}";
        }

        if (!map.Registered.Contains(chord)) return $"{row.Name}: shows Keybinds.{chord}, which the registrar never registers";
        if (family is not null)
        {
            return chord == family && shown.Parameter == row.Parameter
                ? null
                : $"{row.Name}: shows {chord} + {shown.Parameter}, bound to {bound}";
        }

        if (expected.Count == 0) return $"{row.Name}: shows Keybinds.{chord}, but nothing ties that chord to {string.Join(" / ", row.Targets)}";
        return expected.Contains(chord) ? null : $"{row.Name}: shows Keybinds.{chord}, bound to {bound}";
    }

    [Fact]
    public void EveryToolbarButtonWithABoundVerb_ShowsItsChordInItsTooltip()
    {
        var map = KeybindMap.Load();
        var doc = XDocument.Load(ShellFile("ToolbarView.axaml"));
        var buttons = doc.Descendants()
            .Where(e => e.Name.LocalName is "Button" or "ToggleButton")
            .ToList();
        Assert.True(buttons.Count > 10, $"only {buttons.Count} toolbar buttons parsed — the pattern no longer fits the toolbar");

        var failures = new List<string>();
        foreach (var button in buttons)
        {
            var targets = Targets(button).ToList();
            var expected = map.ExpectedFor(targets);
            var name = (string?)button.Attribute(XName.Get("Name", Xaml)) ?? string.Join(" / ", targets);
            var shown = TooltipChords(button).ToList();

            if (expected.Count == 0)
            {
                failures.AddRange(shown.Select(c => $"{name}: tooltip shows Keybinds.{c}, which drives nothing this button does"));
                continue;
            }

            if (ChordShownElsewhere.TryGetValue(name, out var file))
            {
                var elsewhere = File.ReadAllText(UiSourceTree.Resolve(Path.Combine(
                    TestPaths.RepoRoot(), "DotCharter.UI", "Views", "Shell", "Popovers", file)));
                if (!expected.Any(c => elsewhere.Contains($"Keybinds.{c}", StringComparison.Ordinal)))
                {
                    failures.Add($"{name}: its chord is meant to be printed by {file}, which does not print it");
                }

                continue;
            }

            var bound = string.Join(" / ", expected);
            if (shown.Count == 0) failures.Add($"{name}: tooltip shows no chord, bound to {bound}");
            else if (!shown.All(expected.Contains)) failures.Add($"{name}: tooltip shows {string.Join(", ", shown)}, bound to {bound}");
        }

        Assert.True(failures.Count == 0,
            $"{failures.Count} toolbar button(s) disagree with AppKeybindRegistrar:\n  " + string.Join("\n  ", failures));
    }

    [Fact]
    public void EveryEquivalent_IsStillRegistered_AndStillBound()
    {
        var map = KeybindMap.Load();
        var bound = MenuRows().SelectMany(r => r.Targets)
            .Concat(XDocument.Load(ShellFile("ToolbarView.axaml")).Descendants().SelectMany(Targets))
            .ToHashSet(StringComparer.Ordinal);

        var stale = Equivalents
            .SelectMany(e => e.Value.Paths.Select(p => (Chord: e.Key, Path: p)))
            .Where(e => !map.Registered.Contains(e.Chord) || !bound.Contains(e.Path))
            .Select(e => $"{e.Chord} → {e.Path}")
            .ToList();
        Assert.True(stale.Count == 0, "stale Equivalents (chord unregistered or path bound nowhere):\n  " + string.Join("\n  ", stale));
        Assert.All(Equivalents.Values, e => Assert.False(string.IsNullOrWhiteSpace(e.Reason)));

        // An equivalent the registrar can now follow by itself is no longer an excuse.
        var derivable = Equivalents
            .Where(e => e.Value.Paths.All(p => map.Targets(e.Key).Contains(p)))
            .Select(e => e.Key)
            .ToList();
        Assert.True(derivable.Count == 0, "Equivalents the registrar already routes through a command: " + string.Join(", ", derivable));
    }

    /// <summary>The Bookmarks rows bind <c>BookmarkBase</c> with their digit as the
    /// parameter; the converter must build the chord the registrar builds for that digit —
    /// <c>(Key)((int)Key.D0 + i)</c> with the base's modifiers — or the label lies.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(9)]
    public void ADigitFamilyRow_GetsTheChordTheRegistrarDerives(int digit)
    {
        var bookmarkBase = new KeyChord(Key.D0, KeyModifiers.Shift);

        var boxed = KeyChordToGestureConverter.Instance.Convert(bookmarkBase, typeof(KeyGesture), digit, CultureInfo.InvariantCulture);
        var fromXaml = KeyChordToGestureConverter.Instance.Convert(bookmarkBase, typeof(KeyGesture), digit.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

        var expected = new KeyGesture((Key)((int)Key.D0 + digit), KeyModifiers.Shift);
        Assert.Equal(expected, boxed);
        Assert.Equal(expected, fromXaml);
    }

    [Fact]
    public void ADigitFamily_FollowsARebindOfItsModifiers_AndAnUnboundBaseShowsNothing()
    {
        Assert.Equal(new KeyGesture(Key.D4, KeyModifiers.Control | KeyModifiers.Shift),
            KeyChordToGestureConverter.Instance.Convert(new KeyChord(Key.D0, KeyModifiers.Control | KeyModifiers.Shift),
                typeof(KeyGesture), "4", CultureInfo.InvariantCulture));
        Assert.Null(KeyChordToGestureConverter.Instance.Convert(KeyChord.None, typeof(KeyGesture), "4", CultureInfo.InvariantCulture));
        // No parameter: the chord as it is (every non-family row).
        Assert.Equal(new KeyGesture(Key.F9),
            KeyChordToGestureConverter.Instance.Convert(new KeyChord(Key.F9), typeof(KeyGesture), null, CultureInfo.InvariantCulture));
    }

    // ── the menu ────────────────────────────────────────────────────────────

    private sealed record MenuRow(string Name, string? Command, IReadOnlyList<string> Targets, string? Parameter, string? Gesture);

    private static IEnumerable<MenuRow> MenuRows()
    {
        var doc = MainMenuSource.Document();
        foreach (var item in doc.Descendants().Where(e => e.Name.LocalName == "MenuItem"))
        {
            var command = BindingPath((string?)item.Attribute("Command"));
            var parameter = (string?)item.Attribute("CommandParameter")
                            ?? item.Elements().FirstOrDefault(e => e.Name.LocalName == "MenuItem.CommandParameter")?.Value.Trim();
            yield return new MenuRow(
                Header(item), command, Targets(item).ToList(), parameter, (string?)item.Attribute("InputGesture"));
        }
    }

    private static string Header(XElement item)
    {
        var header = (string?)item.Attribute("Header") ?? "?";
        var key = Regex.Match(header, @"Loc (\w+)");
        var own = key.Success ? key.Groups[1].Value : header;
        var parent = item.Parent is { Name.LocalName: "MenuItem" } p ? Header(p) + " > " : "";
        return parent + own;
    }

    /// <summary>What a control DRIVES: its command, and a two-way <c>IsChecked</c>.</summary>
    private static IEnumerable<string> Targets(XElement element)
    {
        if (BindingPath((string?)element.Attribute("Command")) is { } command) yield return command;
        var isChecked = (string?)element.Attribute("IsChecked");
        var twoWay = isChecked is not null && !isChecked.Contains("OneWay", StringComparison.Ordinal);
        if (twoWay && BindingPath(isChecked) is { } path) yield return path;
    }

    private static string? BindingPath(string? binding)
    {
        if (binding is null) return null;
        var m = Regex.Match(binding, @"^\{(?:Binding|CompiledBinding)\s+(?:Path=)?([\w.]+)");
        return m.Success ? m.Groups[1].Value : null;
    }

    private static (string? Chord, string? Parameter) ParseGesture(string gesture)
    {
        var m = Regex.Match(gesture, @"^\{Binding ProjectState\.Keybinds\.(\w+),\s*Converter=\{x:Static converters:" + Converter + @"\.Instance\}(?:,\s*ConverterParameter=(\w+))?\}$");
        return m.Success ? (m.Groups[1].Value, m.Groups[2].Success ? m.Groups[2].Value : null) : (null, null);
    }

    private static IEnumerable<string> TooltipChords(XElement button)
    {
        var tip = button.Elements().FirstOrDefault(e => e.Name.LocalName == "ToolTip.Tip");
        var inline = (string?)button.Attribute("ToolTip.Tip");
        var text = (tip?.ToString() ?? "") + (inline ?? "");
        return Regex.Matches(text, @"ProjectState\.Keybinds\.(\w+)").Select(m => m.Groups[1].Value);
    }

    private static string ShellFile(string name) => UiSourceTree.Resolve(Path.Combine(
        TestPaths.RepoRoot(), "DotCharter.UI", "Views", "Shell", name));

    private const string Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    // ── the registrar ───────────────────────────────────────────────────────

    /// <summary>What <c>AppKeybindRegistrar.RegisterAll</c> wires, read from its source.</summary>
    private sealed class KeybindMap
    {
        /// <summary>Every <c>KeybindConfig</c> property the registrar registers.</summary>
        public HashSet<string> Registered { get; } = new(StringComparer.Ordinal);

        /// <summary>Chord → the shell-relative paths its action executes or flips.</summary>
        public Dictionary<string, HashSet<string>> ChordToTargets { get; } = new(StringComparer.Ordinal);

        /// <summary>Command path → the digit family (<c>BookmarkBase</c>) that executes it.</summary>
        public Dictionary<string, string> Families { get; } = new(StringComparer.Ordinal);

        /// <summary>Fixed key (<c>F12</c>) → the command path it executes.</summary>
        public Dictionary<string, string> Literals { get; } = new(StringComparer.Ordinal);

        public IReadOnlyCollection<string> Targets(string chord) =>
            ChordToTargets.TryGetValue(chord, out var t) ? t : [];

        public string? LiteralFor(string command) =>
            Literals.FirstOrDefault(l => l.Value == command).Key;

        /// <summary>Every chord that drives any of <paramref name="targets" />.</summary>
        public HashSet<string> ExpectedFor(IEnumerable<string> targets)
        {
            var set = targets.ToHashSet(StringComparer.Ordinal);
            var chords = ChordToTargets.Where(c => c.Value.Overlaps(set)).Select(c => c.Key);
            var equivalents = Equivalents.Where(e => e.Value.Paths.Any(set.Contains)).Select(e => e.Key);
            return chords.Concat(equivalents).ToHashSet(StringComparer.Ordinal);
        }

        public static KeybindMap Load()
        {
            var map = new KeybindMap();
            var registrar = StripComments(File.ReadAllText(UiSourceTree.Resolve(Path.Combine(
                TestPaths.RepoRoot(), "DotCharter.UI", "Input", "AppKeybindRegistrar.cs"))));
            var shellSources = UiSourceTree.GetFiles(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "ViewModels", "Shell"), "MainWindowViewModel*.cs")
                .Select(f => StripComments(File.ReadAllText(f)))
                .ToList();
            var toggles = ShellToggles(shellSources);
            var bodies = ShellCommandBodies(shellSources);

            foreach (var args in Registrations(registrar))
            {
                if (args.Count < 2) continue;
                var (key, action) = (args[0], args[1]);
                var paths = ActionTargets(action).ToHashSet(StringComparer.Ordinal);

                // The registrar writing out the very expression a shell command runs is the
                // same verb by a second door (snap, zoom and bookmarks call the coordinator
                // directly) — the menu row bound to that command still owes the chord.
                var loopLocal = Regex.Match(key, @"Key\.D\d\s*\+\s*(\w+)\)");
                var local = loopLocal.Success ? loopLocal.Groups[1].Value : null;
                if (bodies.TryGetValue(NormalizeBody(action, local), out var same)) paths.Add(same);
                foreach (var p in paths.ToList())
                {
                    if (toggles.TryGetValue(p, out var property)) paths.Add(property);
                }

                if (Regex.Match(key, @"^\(\)\s*=>\s*_projectState\.Keybinds\.(\w+)$") is { Success: true } chord)
                {
                    map.Registered.Add(chord.Groups[1].Value);
                    if (paths.Count > 0) map.ChordToTargets[chord.Groups[1].Value] = paths;
                }
                else if (Regex.Match(key, @"_projectState\.Keybinds\.(\w+)\.Modifiers") is { Success: true } family)
                {
                    map.Registered.Add(family.Groups[1].Value);
                    foreach (var p in paths) map.Families[p] = family.Groups[1].Value;
                }
                else if (Regex.Match(key, @"^\(\)\s*=>\s*Key\.(\w+)$") is { Success: true } literal)
                {
                    foreach (var p in paths) map.Literals[literal.Groups[1].Value] = p;
                }
            }

            return map;
        }

        /// <summary>Field on the registrar → its path relative to the shell view model.</summary>
        private static readonly Dictionary<string, string> Owners = new(StringComparer.Ordinal)
        {
            ["_shell"] = "",
            ["_transport"] = "Transport.",
            ["_chartEditor"] = "ChartEditor.",
            ["_projectState"] = "ProjectState.",
        };

        private static IEnumerable<string> ActionTargets(string action)
        {
            foreach (Match m in Regex.Matches(action, @"\b(_\w+)((?:\.\w+)*?)\.(\w+Command)\.Execute\("))
            {
                if (!Owners.TryGetValue(m.Groups[1].Value, out var owner)) continue;
                var middle = m.Groups[2].Value.TrimStart('.');
                yield return owner + (middle.Length > 0 ? middle + "." : "") + m.Groups[3].Value;
            }

            foreach (Match m in Regex.Matches(action, @"\b(_\w+)\.(\w+)\s*=\s*!\s*\1\.\2\b"))
            {
                if (Owners.TryGetValue(m.Groups[1].Value, out var owner)) yield return owner + m.Groups[2].Value;
            }
        }

        /// <summary><c>[RelayCommand] ToggleX() =&gt; P = !P;</c> on the shell: ToggleXCommand ≡ P.</summary>
        private static Dictionary<string, string> ShellToggles(IEnumerable<string> shellSources)
        {
            var toggles = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var source in shellSources)
            {
                foreach (Match m in Regex.Matches(source, @"\[RelayCommand\]\s*private void (\w+)\(\)\s*=>\s*(\w+)\s*=\s*!\s*\2\s*;"))
                    toggles[m.Groups[1].Value + "Command"] = m.Groups[2].Value;
            }

            return toggles;
        }

        /// <summary>Normalized expression body → <c>XCommand</c>, for every expression-bodied
        /// <c>[RelayCommand]</c> on the shell with at most one parameter.</summary>
        private static Dictionary<string, string> ShellCommandBodies(IEnumerable<string> shellSources)
        {
            var bodies = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var source in shellSources)
            {
                foreach (Match m in Regex.Matches(source,
                             @"\[RelayCommand\]\s*private void (\w+)\((?:\w+\s+(\w+))?\)\s*=>\s*([^;]+);"))
                {
                    var body = NormalizeBody(m.Groups[3].Value, m.Groups[2].Success ? m.Groups[2].Value : null);
                    bodies.TryAdd(body, m.Groups[1].Value + "Command");
                }
            }

            return bodies;
        }

        /// <summary>One spelling for the registrar's view of the shell and the shell's own:
        /// <c>_transport.X</c> is <c>Transport.X</c>, and the one parameter is <c>$p</c>.</summary>
        private static string NormalizeBody(string expression, string? parameter)
        {
            var body = Regex.Replace(expression, @"^\s*\(\)\s*=>\s*", "");
            body = Regex.Replace(body, @"\s+", "");
            body = body.Replace("_transport.", "Transport.", StringComparison.Ordinal)
                .Replace("_chartEditor.", "ChartEditor.", StringComparison.Ordinal)
                .Replace("_projectState", "ProjectState", StringComparison.Ordinal);
            if (parameter is not null) body = Regex.Replace(body, $@"\b{Regex.Escape(parameter)}\b", "$$p");
            return body;
        }

        /// <summary>The top-level arguments of every <c>_registry.Register(…)</c> call.</summary>
        private static IEnumerable<List<string>> Registrations(string source)
        {
            const string call = "_registry.Register(";
            for (var at = source.IndexOf(call, StringComparison.Ordinal); at >= 0;
                 at = source.IndexOf(call, at + 1, StringComparison.Ordinal))
            {
                var args = new List<string>();
                var depth = 0;
                var start = at + call.Length;
                for (var i = start; i < source.Length; i++)
                {
                    var ch = source[i];
                    var closesCall = ch is ')' && depth == 0;
                    if (closesCall || (ch == ',' && depth == 0))
                    {
                        args.Add(Normalize(source[start..i]));
                        start = i + 1;
                    }

                    if (closesCall) break;
                    depth += ch switch
                    {
                        '(' or '{' or '[' => 1,
                        ')' or '}' or ']' => -1,
                        _ => 0
                    };
                }

                yield return args;
            }
        }

        private static string Normalize(string s) => Regex.Replace(s, @"\s+", " ").Trim();

        private static string StripComments(string source) =>
            Regex.Replace(source, @"//[^\r\n]*", "");
    }
}
