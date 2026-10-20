using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows.Input;
using System.Xml.Linq;
using DotCharter.Tone.UI.ViewModels.PlayMode;

namespace DotCharter.Tests.UI;

/// <summary>
///     NO DEAD BUTTONS IN THE RIG. Every <c>Button</c> / <c>ToggleButton</c> /
///     <c>RepeatButton</c> in <c>AmpFxRigView.axaml</c> must raise something: a
///     <c>Command</c> whose binding path RESOLVES, by reflection, to a real
///     <see cref="ICommand" /> member of the view model that scope actually
///     carries — or a <c>Click</c> handler the code-behind declares.
///
///     <para>
///         <b>The owner bug (v0.48.5).</b> "Several buttons in the Amp &amp; FX
///         rig have no function: A/B, Copy, Both, Switch." Two of those were not
///         buttons at all — the SCENE strip's A and B were <c>Border</c>s wearing
///         the <c>FireSegmentItem</c> look that the ROUTING strip in the same
///         screen gives to real <c>ToggleButton</c>s — and SWITCH was a live
///         button gated <c>IsEnabled="{Binding Rig.CanCompare}"</c>, false on
///         every rig nobody had seeded. The root rule this repo runs on is that a
///         capability the app cannot honour must never LOOK live; a control
///         wearing a button's clothes is the cheapest way to break it.
///     </para>
///     <para>
///         <b>Why a source lint rather than a click test.</b> The commands are
///         generated from <c>[RelayCommand]</c>, so reflection over the view
///         model happily finds properties that no markup names — which is the
///         state being looked for. What matters is whether the MARKUP names one,
///         and whether that name survives a walk over real types. Comments cannot
///         satisfy it: <c>XDocument</c> yields comments as <c>XComment</c>, never
///         as elements or attributes, so a button that only exists inside
///         <c>&lt;!-- --&gt;</c> is invisible here exactly as it is to Avalonia.
///     </para>
///     <para>
///         The complementary halves: <see cref="RelayCommandReachabilityTests" />
///         works the other direction (a command no view binds),
///         <see cref="RigCompareSceneTests" /> drives the SCENE strip's commands
///         for real, and <see cref="AmpFxRigScreenTests" /> owns the rest of this
///         view's structure.
///     </para>
/// </summary>
public sealed class AmpFxRigNoDeadButtonsTests
{
    private static readonly string[] ButtonKinds =
        ["Button", "ToggleButton", "RepeatButton", "SplitButton", "ToggleSplitButton"];

    /// <summary>
    ///     Every button in the rig resolves to a real command (or a real click
    ///     handler). A <c>{Binding}</c> discard, a misspelt path, a path that
    ///     stops on a non-command member and a button with neither attribute all
    ///     fail here.
    /// </summary>
    [Fact]
    public void EveryButtonInTheRig_RaisesSomethingThatExists()
    {
        var offenders = new List<string>();
        var byForm = new Dictionary<string, int>(StringComparer.Ordinal);
        // The screen, then the editor HOST and every family view under RigEditors/.
        // The screen and its Tones view (its own file since 2026-09-27) are both
        // views over AmpFxRigViewModel; each has its own floor, so a move of one's
        // buttons into the other cannot hide behind a total.
        var screens = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["AmpFxRigView"] = 40,
            ["RigTonesView"] = 11, // 15 until #242 moved the four NAM row buttons into RigNamRowTemplates
            // the view's own template dictionaries (#240): a lane of the split
            // region and a gear-browser tile, merged by AmpFxRigView
            ["RigBoardLaneTemplate"] = 4,
            ["RigGearTileTemplate"] = 1,
            // the NAM capture browser's rows, merged by RigTonesView (#242)
            ["RigNamRowTemplates"] = 4
        };
        var views = new List<(string Name, string Path)>
        {
            ("AmpFxRigView", ViewPath()),
            ("RigTonesView", TonesViewPath()),
            ("RigBoardLaneTemplate", TemplatePath("RigBoardLaneTemplate")),
            ("RigGearTileTemplate", TemplatePath("RigGearTileTemplate")),
            ("RigNamRowTemplates", TemplatePath("RigNamRowTemplates"))
        };
        views.AddRange(RigEditorViews.Paths(TestPaths.RepoRoot()).Select(p => (Path.GetFileNameWithoutExtension(p), p)));
        var editorButtons = new List<XElement>();
        foreach (var (view, path) in views)
        {
            var isEditor = !screens.ContainsKey(view);
            var doc = XDocument.Load(path, LoadOptions.SetLineInfo);
            var prefixes = PrefixNamespaces(doc.Root!);
            // a template dictionary has no code-behind: its buttons must bind
            var codeBehind = File.Exists(path + ".cs") ? File.ReadAllText(path + ".cs") : "";
            var named = doc.Descendants().Where(e => e.Attributes().Any(a => a.Name.LocalName == "Name"))
                .ToDictionary(e => e.Attributes().First(a => a.Name.LocalName == "Name").Value, e => e, StringComparer.Ordinal);
            var buttons = Buttons(doc).ToList();
            if (isEditor)
            {
                // The host itself holds no button; each family view is a view over the unit.
                Assert.Equal(typeof(RigDockUnit), ScopeOf(doc.Root!, prefixes));
                editorButtons.AddRange(buttons);
            }
            else
            {
                Assert.True(buttons.Count >= screens[view], $"Only {buttons.Count} {view} buttons found");
            }

            foreach (var button in buttons)
            {
                var fault = Verdict(button, prefixes, named, codeBehind, byForm, depth: 0);
                if (fault != null) offenders.Add($"{view}: {Describe(button)}: {fault}");
            }
        }
        Assert.True(offenders.Count == 0, string.Join(Environment.NewLine, offenders));
        Assert.NotEmpty(editorButtons);
        Assert.Contains(editorButtons, b => b.Attribute("Command")?.Value == "{Binding FileParam.PickFileCommand}");
        Assert.Contains(editorButtons, b => b.Attribute("Command")?.Value == "{Binding FileParam.ClearCommand}");
        Assert.Contains(editorButtons, b => b.Attribute("Command")?.Value == "{Binding LyricsParam.SaveLyricsCommand}");
        // 'element-alias' ({Binding #Other.Command}) is no longer required: its one
        // user, the Tones close button aliasing the header pill, crossed into the
        // Tones VIEW (2026-09-27), whose name scope cannot see the pill, and binds
        // the pill's command path directly now. The branch stays so an alias
        // written tomorrow is still followed rather than reported dead.
        foreach (var form in new[] { "direct", "ancestor-cast", "click", "checked-write" })
        {
            Assert.True((byForm.TryGetValue(form, out var seen) ? seen : 0) > 0,
                $"no button resolved through the '{form}' form — that branch is never exercised");
        }
    }

    /// <summary>
    ///     …and the ones the SCREEN's own view model owns resolve to a NON-NULL
    ///     command on a real instance. The lint above proves the member exists;
    ///     this proves the app hands the button something to press — a
    ///     <c>[RelayCommand]</c> is a lazily created property, and a hand-written
    ///     <c>ICommand</c> left null binds silently and does nothing.
    /// </summary>
    [Fact]
    public void EveryRootScopedButton_ResolvesToALiveCommandOnTheRealViewModel()
    {
        var rig = TestRigBuilders.Pedalboard(new FakeDspChainService(), new FakeDspTonePresetService(), FakeLocalization.English());
        var vm = new AmpFxRigViewModel(rig);

        var checkedPaths = new List<string>();
        var dead = new List<string>();

        // The screen and its Tones view: both are views over the same model.
        foreach (var file in new[] { ViewPath(), TonesViewPath() })
            CheckRootScoped(XDocument.Load(file, LoadOptions.SetLineInfo), vm, checkedPaths, dead);

        Assert.True(dead.Count == 0,
            "Rig buttons bound to a command the live view model does not hand out:" +
            $"{Environment.NewLine}  " + string.Join($"{Environment.NewLine}  ", dead));

        // Non-vacuity: the header, the board toolbar, the footer and the SCENE
        // strip are all root-scoped, so a real run resolves a couple of dozen.
        Assert.True(checkedPaths.Count >= 15,
            $"only {checkedPaths.Count} root-scoped command paths were resolved — the walk is not looking");
        Assert.Contains("Rig.SelectCompareSlotCommand", checkedPaths);
        Assert.Contains("Rig.ToggleCompareCommand", checkedPaths);
        Assert.Contains("Rig.SeedCompareCommand", checkedPaths);
        // the Tones view's close, reached through its UserControl's context
        Assert.Contains("Rig.ToggleBrowserCommand", checkedPaths);
    }

    private static void CheckRootScoped(XDocument doc, AmpFxRigViewModel vm, List<string> checkedPaths,
        List<string> dead)
    {
        var prefixes = PrefixNamespaces(doc.Root!);
        // the header's cog menu hosts SWITCH and the tone file exchange as MenuItems
        // (the dock's own menu binds through the selected unit, which this empty rig has none of)
        var menuItems = doc.Descendants().Where(e => e.Name.LocalName == "MenuItem"
            && e.Ancestors().Any(a => a.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml"))?.Value == "RigHeaderMenu"));
        foreach (var button in Buttons(doc).Concat(menuItems))
        {
            var binding = BindingBody(button.Attribute("Command")?.Value);
            if (binding is null) continue;

            var path = RootScopedPath(button, binding, prefixes);
            if (path is null) continue;

            object? value = vm;
            foreach (var step in path.Split('.'))
            {
                value = Member(value?.GetType(), step) switch
                {
                    PropertyInfo p => value is null ? null : p.GetValue(value),
                    FieldInfo f => value is null ? null : f.GetValue(value),
                    _ => null
                };
                if (value is null) break;
            }

            checkedPaths.Add(path);
            if (value is not ICommand) dead.Add($"{Describe(button)}: {path} is {value?.GetType().Name ?? "null"}");
        }
    }

    /// <summary>
    ///     The SCENE strip's A and B are BUTTONS over the slot they name, and
    ///     nothing in the strip carries an <c>IsEnabled</c> gate. Pinned as
    ///     markup because the fault was markup: the pair drew as <c>Border</c>s,
    ///     and the empty-slot case is a fork now, so nothing in the strip has a
    ///     reason to sit disabled.
    /// </summary>
    [Fact]
    public void NothingInTheSceneStrip_IsUnpressable()
    {
        var doc = XDocument.Load(ViewPath(), LoadOptions.SetLineInfo);

        foreach (var (name, slot) in new[] { ("RigSceneA", "A"), ("RigSceneB", "B") })
        {
            var lamp = Named(doc, name);
            Assert.Equal("ToggleButton", lamp.Name.LocalName);
            Assert.Contains("SelectCompareSlotCommand", lamp.Attribute("Command")?.Value ?? "");
            Assert.Equal(slot, lamp.Attribute("CommandParameter")?.Value);
        }

        foreach (var name in new[] { "RigSceneA", "RigSceneB", "RigCompareButton", "RigSeedButton" })
        {
            Assert.True(Named(doc, name).Attribute("IsEnabled") is null,
                $"{name} is gated by IsEnabled again — the strip's whole bug was a control that " +
                "could not be pressed in the state every player first meets it in");
        }
    }

    // ════════════════════════════════════════════════════════════ the walk

    private static IEnumerable<XElement> Buttons(XDocument doc) =>
        doc.Descendants()
            .Where(e => ButtonKinds.Contains(e.Name.LocalName, StringComparer.Ordinal))
            // `<Button.Styles>` and friends are property elements, not controls.
            .Where(e => !e.Name.LocalName.Contains('.', StringComparison.Ordinal));

    /// <summary>Null when the button is fine; otherwise why it is dead.</summary>
    private static string? Verdict(
        XElement button,
        IReadOnlyDictionary<string, string> prefixes,
        IReadOnlyDictionary<string, XElement> named,
        string codeBehind,
        IDictionary<string, int> byForm,
        int depth)
    {
        var command = button.Attribute("Command")?.Value;
        if (command is null)
        {
            var click = button.Attributes().FirstOrDefault(a => a.Name.LocalName == "Click")?.Value;
            if (click != null)
            {
                if (!Regex.IsMatch(codeBehind, $@"\b(void|Task)\s+{Regex.Escape(click)}\s*\("))
                    return $"Click=\"{click}\" has no handler in its code-behind";
                Bump(byForm, "click");
                return null;
            }

            // A button that OPENS A MENU acts through the menu's rows: each
            // MenuItem must itself resolve a command (the editor header's ···).
            var menuItems = button.Elements()
                .Where(e => e.Name.LocalName == "Button.Flyout")
                .SelectMany(f => f.Descendants().Where(e => e.Name.LocalName == "MenuItem"))
                .ToList();
            // A button that OPENS A POPOVER (a Flyout hosting a view, e.g. the
            // header's DI button over RigReampPanel) acts through that view,
            // whose own controls are linted by its own tests.
            var popover = button.Elements()
                .Where(e => e.Name.LocalName == "Button.Flyout")
                .SelectMany(f => f.Elements().Where(e => e.Name.LocalName == "Flyout"))
                .FirstOrDefault(f => f.Elements().Any());
            if (popover != null && menuItems.Count == 0)
            {
                Bump(byForm, "popover");
                return null;
            }
            if (menuItems.Count > 0)
            {
                foreach (var item in menuItems)
                {
                    // A submenu whose rows come from data (ItemsSource) has no
                    // command of its own; its rows carry theirs through a style.
                    if (item.Attribute("ItemsSource") != null) continue;
                    var fault = Verdict(item, prefixes, named, codeBehind, byForm, depth);
                    if (fault != null) return $"menu row '{item.Attribute("Header")?.Value}': {fault}";
                }
                Bump(byForm, "menu");
                return null;
            }

            // A ToggleButton can act by WRITING its binding instead — the per-unit
            // bypass foot is `IsChecked="{Binding Bypassed}"`, two-way by default,
            // and it is the pedal's real on/off. That only counts when the target
            // is settable and the binding is not pinned OneWay: the SCENE lamps
            // are OneWay ToggleButtons precisely because their press is a command.
            var writes = ChecksSomethingWritable(button, prefixes);
            if (writes is null) return "no Command, no Click and no two-way IsChecked — it does nothing at all";
            if (writes.Length > 0) return writes;
            Bump(byForm, "checked-write");
            return null;
        }

        var body = BindingBody(command);
        if (body is null) return $"Command=\"{command}\" is not a binding";
        if (body.Length == 0) return "Command=\"{Binding}\" binds the DataContext itself — a discard";

        // `{Binding #Other.Command}`: it presses whatever THAT button presses.
        var alias = Regex.Match(body, @"^#(\w+)\.Command$");
        if (alias.Success)
        {
            if (depth > 2) return "an element-alias chain that never lands on a command";
            if (!named.TryGetValue(alias.Groups[1].Value, out var target))
                return $"aliases #{alias.Groups[1].Value}, which is not in this view";
            Bump(byForm, "element-alias");
            return Verdict(target, prefixes, named, codeBehind, byForm, depth + 1);
        }

        // `{Binding $parent[X].((vm:Type)DataContext).Path}` names its own root.
        var cast = Regex.Match(body, @"\(\((\w+):(\w+)\)DataContext\)\.(.+)$");
        string path;
        Type? scope;
        if (cast.Success)
        {
            scope = Resolve(prefixes, cast.Groups[1].Value, cast.Groups[2].Value);
            path = cast.Groups[3].Value;
            Bump(byForm, "ancestor-cast");
        }
        else
        {
            if (body.Contains('$') || body.Contains('#'))
                return $"binding form not understood by this lint: {body}";
            scope = ScopeOf(button, prefixes);
            path = body;
            Bump(byForm, "direct");
        }

        if (scope is null) return $"the enclosing x:DataType does not resolve to a type ({body})";
        return Walk(scope, path);
    }

    /// <summary>
    ///     Null when the button has no usable <c>IsChecked</c> write at all;
    ///     <c>""</c> when it has one; otherwise the reason the write is not real.
    /// </summary>
    private static string? ChecksSomethingWritable(
        XElement button, IReadOnlyDictionary<string, string> prefixes)
    {
        var attribute = button.Attribute("IsChecked")?.Value;
        if (attribute is null) return null;
        if (attribute.Contains("Mode=OneWay", StringComparison.Ordinal))
            return "IsChecked is OneWay and there is no Command — pressing it writes nothing";

        var body = BindingBody(attribute);
        if (body is null || body.Length == 0 || body.Contains('$') || body.Contains('#')) return null;

        var scope = ScopeOf(button, prefixes);
        if (scope is null) return null;

        var current = scope;
        PropertyInfo? tail = null;
        foreach (var step in body.TrimStart('!').Split('.'))
        {
            if (Member(current, step) is not PropertyInfo p)
                return $"IsChecked binds '{step}', which is not a property of {current!.Name}";
            tail = p;
            current = p.PropertyType;
        }

        return tail!.CanWrite ? "" : $"IsChecked binds {body}, which is read-only — the press goes nowhere";
    }

    /// <summary>Walks a dotted binding path over real types; null when it lands
    /// on an <see cref="ICommand" />.</summary>
    private static string? Walk(Type scope, string path)
    {
        var current = scope;
        foreach (var step in path.Split('.'))
        {
            var member = Member(current, step);
            if (member is null)
                return $"'{step}' is not a member of {current!.Name} (path {path})";
            current = member switch
            {
                PropertyInfo p => p.PropertyType,
                FieldInfo f => f.FieldType,
                _ => null
            };
        }

        return typeof(ICommand).IsAssignableFrom(current)
            ? null
            : $"{path} is a {current?.Name ?? "?"}, not an ICommand";
    }

    private static MemberInfo? Member(Type? type, string name)
    {
        const BindingFlags flags =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy;
        return (MemberInfo?)type?.GetProperty(name, flags) ?? type?.GetField(name, flags);
    }

    /// <summary>The view model a binding without its own root resolves against:
    /// the nearest ancestor-or-self carrying <c>x:DataType</c> (a
    /// <c>DataTemplate</c>, or the <c>DataContext</c>-repointing panels the
    /// browser and the capture grid use).</summary>
    private static Type? ScopeOf(XElement element, IReadOnlyDictionary<string, string> prefixes)
    {
        for (var e = element; e != null; e = e.Parent)
        {
            var declared = e.Attributes().FirstOrDefault(a => a.Name.LocalName == "DataType")?.Value;
            if (declared is null) continue;

            var m = Regex.Match(declared, @"(?:\{x:Type\s+)?(\w+):(\w+)");
            if (m.Success) return Resolve(prefixes, m.Groups[1].Value, m.Groups[2].Value);
        }

        return null;
    }

    /// <summary>The path a button binds ON THE SCREEN's own view model, or null
    /// when it is scoped to an item/panel type instead.</summary>
    private static string? RootScopedPath(
        XElement button, string body, IReadOnlyDictionary<string, string> prefixes)
    {
        var cast = Regex.Match(body, @"\(\((\w+):(\w+)\)DataContext\)\.(.+)$");
        if (cast.Success)
        {
            return Resolve(prefixes, cast.Groups[1].Value, cast.Groups[2].Value) == typeof(AmpFxRigViewModel)
                ? cast.Groups[3].Value
                : null;
        }

        if (body.Contains('$') || body.Contains('#') || body.Length == 0) return null;
        return ScopeOf(button, prefixes) == typeof(AmpFxRigViewModel) ? body : null;
    }

    // ════════════════════════════════════════════════════════════ plumbing

    /// <summary>The inside of a <c>{Binding …}</c> / <c>{CompiledBinding …}</c>,
    /// without converters or binding options; null when the attribute is not a
    /// binding at all.</summary>
    private static string? BindingBody(string? attribute)
    {
        if (attribute is null) return null;
        var m = Regex.Match(attribute.Trim(), @"^\{(?:x:Static\s+)?(Binding|CompiledBinding|ReflectionBinding)\s*(.*)\}$");
        if (!m.Success) return null;

        var body = m.Groups[2].Value.Trim();
        var comma = body.IndexOf(',');
        if (comma >= 0) body = body[..comma].Trim();
        if (body.StartsWith("Path=", StringComparison.Ordinal)) body = body[5..].Trim();
        return body;
    }

    private static Dictionary<string, string> PrefixNamespaces(XElement root)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var a in root.Attributes().Where(a => a.IsNamespaceDeclaration))
        {
            // `using:` since the rig moved to DotCharter.Tone.UI (a bare
            // clr-namespace resolves only in its own assembly).
            var m = Regex.Match(a.Value, @"^(?:clr-namespace|using):([^;]+)");
            if (m.Success) map[a.Name.LocalName] = m.Groups[1].Value;
        }

        return map;
    }

    private static Type? Resolve(IReadOnlyDictionary<string, string> prefixes, string prefix, string name)
        => prefixes.TryGetValue(prefix, out var ns)
            ? typeof(AmpFxRigViewModel).Assembly.GetType($"{ns}.{name}")
            : null;

    private static XElement Named(XDocument doc, string name)
    {
        var found = doc.Descendants().SingleOrDefault(e =>
            e.Attributes().Any(a => a.Name.LocalName == "Name" && a.Value == name));
        Assert.True(found != null, $"'{name}' is not in AmpFxRigView.axaml");
        return found!;
    }

    private static void Bump(IDictionary<string, int> counts, string form)
        => counts[form] = counts.TryGetValue(form, out var n) ? n + 1 : 1;

    private static string Describe(XElement e)
    {
        var name = e.Attributes().FirstOrDefault(a => a.Name.LocalName == "Name")?.Value;
        var line = ((System.Xml.IXmlLineInfo)e).LineNumber;
        return $"{e.Name.LocalName} {(name is null ? "" : $"'{name}' ")}at line {line}";
    }

    private static string ViewPath() =>
        Path.Combine(TestPaths.RepoRoot(), "DotCharter.Tone.UI", "Views", "PlayMode", "AmpFxRigView.axaml");

    private static string TemplatePath(string name) =>
        Path.Combine(TestPaths.RepoRoot(), "DotCharter.Tone.UI", "Views", "PlayMode", name + ".axaml");

    private static string TonesViewPath() =>
        Path.Combine(TestPaths.RepoRoot(), "DotCharter.Tone.UI", "Views", "PlayMode", "RigTonesView.axaml");
}
