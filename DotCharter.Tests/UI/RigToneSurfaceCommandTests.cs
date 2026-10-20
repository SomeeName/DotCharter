using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows.Input;
using DotCharter.Tone.UI.ViewModels.PlayMode;
using DotCharter.UI.ViewModels.PlayMode;
using DotCharter.UI.ViewModels.PlayMode.ToneSections;

namespace DotCharter.Tests.UI;

/// <summary>
///     W4 (2026-09-16) — NO FEATURE LEFT THE TONE SURFACES IN THE REBUILD.
///     <para>
///         The designer, the matcher, the section list and the Tones catalogue
///         were moved out of the rig's workspace drawer into the Tones mode, and
///         the module editor's rack was reworked in the same pass. A move is the
///         cheapest way to lose a button: it draws nowhere, nothing throws, and
///         the view model keeps its own passing unit tests — the
///         <c>CoopRigApplier</c> shape this repo has hit four times.
///     </para>
///     <para>
///         So this asserts the one thing a move can break: <b>every command
///         these view models hand out is NAMED by one of the rig's own views.</b>
///         It is the per-feature sibling of
///         <c>UI/RelayCommandReachabilityTests</c> (which asks whether ANY file
///         names a command) and of
///         <c>UI/AmpFxRigNoDeadButtonsTests</c> (which asks whether every BUTTON
///         resolves one). Neither of those would notice a command whose only
///         binding was deleted with the drawer, because the generated property
///         would still be named by the view model's own tests.
///     </para>
/// </summary>
public sealed class RigToneSurfaceCommandTests
{
    /// <summary>The view models the rig's tone surfaces and module-editor rack
    /// bind. A type added here is a promise that its whole command surface is on
    /// screen somewhere.</summary>
    private static readonly Type[] Surfaces =
    [
        typeof(ToneDesignViewModel),
        typeof(ToneSectionsViewModel),
        typeof(ToneSectionRowViewModel),
        typeof(ToneSectionProposalViewModel),
        typeof(PresetBrowserViewModel),
        typeof(RigControlAssignmentEditorViewModel)
    ];

    /// <summary>Every view that may satisfy a binding. The tone surfaces are in
    /// three files and the rack's form in two, so a command bound in any of them
    /// counts — what this refuses is a command bound in NONE.</summary>
    private static readonly string[] Views =
    [
        Path.Combine("Views", "PlayMode", "AmpFxRigView.axaml"),
        Path.Combine("Views", "PlayMode", "RigTonesView.axaml"),
        Path.Combine("Views", "PlayMode", "ToneDesignPanel.axaml"),
        Path.Combine("Views", "PlayMode", "ToneDesignCorePanel.axaml"),
        Path.Combine("Views", "PlayMode", "ToneSectionsPanel.axaml"),
        Path.Combine("Views", "PlayMode", "RigModuleEditorView.axaml"),
        Path.Combine("Views", "PlayMode", "RigControlAssignmentView.axaml"),
        Path.Combine("Views", "PlayMode", "RigPerformanceControlsView.axaml"),
        Path.Combine("Views", "PlayMode", "RigMidiControlsView.axaml"),
        Path.Combine("Views", "PlayMode", "RigNodeMetersView.axaml"),
        // A tone card's right-click menu is built in code, per open, over the
        // browser's per-card commands — its items are the binding.
        Path.Combine("Views", "PlayMode", "RigToneCardContextMenu.cs")
    ];

    /// <summary>
    ///     Commands these view models expose that NO rig view binds, each with a
    ///     reason. <b>This list only ever shrinks</b> — adding a name to turn a
    ///     red run green is how a lint stops finding the thing it exists for.
    ///     <para>
    ///         It is EMPTY, and the two that would otherwise be on it are the
    ///         reason this case resolves ALIASES rather than matching names:
    ///         <c>PresetBrowserViewModel.ExportActiveCommand</c> and
    ///         <c>.ImportCommand</c> are drawn by the header cog's menu through
    ///         <c>AmpFxRigViewModel.ExportToneCommand</c> / <c>ImportToneCommand</c>,
    ///         which hand out the SAME command objects under other names. A
    ///         name-only lint would have demanded two exemptions for two live
    ///         menu items.
    ///     </para>
    /// </summary>
    private static readonly Dictionary<string, string> KnownUnbound = new(StringComparer.Ordinal);

    [Fact]
    public void EveryToneSurfaceCommand_IsBoundBySomeRigView()
    {
        var markup = string.Concat(Views.Select(v =>
            Regex.Replace(UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", v))),
                "<!--.*?-->", "", RegexOptions.Singleline)));

        // A live screen over a live rig, so a command can be matched by IDENTITY
        // as well as by name: the rig screen re-exposes several of the browser's
        // commands under its own names, and the object is what the button
        // actually gets.
        using var rig = TestRigBuilders.Pedalboard(new FakeDspChainService(), new FakeDspTonePresetService(), FakeLocalization.English(),
            toneDesignPanel: ToneDesignPanels.Real());
        var screen = new AmpFxRigViewModel(rig);
        var aliases = new Dictionary<ICommand, string>(ReferenceEqualityComparer.Instance
            as IEqualityComparer<ICommand> ?? EqualityComparer<ICommand>.Default);
        foreach (var p in typeof(AmpFxRigViewModel).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!typeof(ICommand).IsAssignableFrom(p.PropertyType)) continue;
            if (p.GetValue(screen) is ICommand c) aliases[c] = p.Name;
        }

        var live = new Dictionary<Type, object?>
        {
            [typeof(PresetBrowserViewModel)] = rig.Browser,
            [typeof(ToneDesignViewModel)] = ToneDesignPanels.Of(rig.Browser),
            [typeof(ToneSectionsViewModel)] = ToneDesignPanels.Of(rig.Browser).Sections
        };

        var checkedNames = new List<string>();
        var aliased = new List<string>();
        var dead = new List<string>();

        foreach (var type in Surfaces)
        {
            foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!p.Name.EndsWith("Command", StringComparison.Ordinal)) continue;
                if (!typeof(ICommand).IsAssignableFrom(p.PropertyType)) continue;

                var key = $"{type.Name}.{p.Name}";
                checkedNames.Add(key);
                if (markup.Contains(p.Name, StringComparison.Ordinal)) continue;
                if (KnownUnbound.ContainsKey(key)) continue;

                // …else the screen may re-expose the SAME command object under its
                // own name, which is what the header cog's menu binds.
                if (live.TryGetValue(type, out var instance) && instance is not null
                    && p.GetValue(instance) is ICommand cmd
                    && aliases.TryGetValue(cmd, out var alias)
                    && markup.Contains(alias, StringComparison.Ordinal))
                {
                    aliased.Add($"{key} -> AmpFxRigViewModel.{alias}");
                    continue;
                }

                dead.Add(key);
            }
        }

        // Non-vacuity for the alias half: the two the header cog draws are
        // reached ONLY that way, so a run that resolves none has stopped
        // following aliases and the case is back to matching names.
        Assert.Contains(aliased,
            a => a.StartsWith("PresetBrowserViewModel.ExportActiveCommand", StringComparison.Ordinal));
        Assert.Contains(aliased,
            a => a.StartsWith("PresetBrowserViewModel.ImportCommand", StringComparison.Ordinal));

        // Non-vacuity: these six view models hand out a couple of dozen commands
        // between them, so a walk that found five has stopped walking.
        Assert.True(checkedNames.Count >= 25,
            $"only {checkedNames.Count} commands were found on the tone surfaces' view models — "
            + "the reflection is not looking. " + string.Join(", ", checkedNames));
        Assert.Contains("ToneDesignViewModel.DesignCommand", checkedNames);
        Assert.Contains("ToneSectionsViewModel.ApplyCommand", checkedNames);

        Assert.True(dead.Count == 0,
            $"{dead.Count} command(s) the rig's tone surfaces expose are bound by NO rig view — a "
            + $"feature that draws nowhere:{Environment.NewLine}  "
            + string.Join(Environment.NewLine + "  ", dead));
    }

    /// <summary>…and every entry on the allow-list must still BE unbound, which
    /// today means the list must be EMPTY. A stale exemption is where the next
    /// lost button hides.</summary>
    [Fact]
    public void EveryUnboundExemption_IsStillUnbound()
    {
        var markup = string.Concat(Views.Select(v =>
            Regex.Replace(UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", v))),
                "<!--.*?-->", "", RegexOptions.Singleline)));

        foreach (var (key, why) in KnownUnbound)
        {
            Assert.False(markup.Contains(key.Split('.')[1], StringComparison.Ordinal),
                $"{key} is bound now — delete its exemption ({why})");
        }

        Assert.True(KnownUnbound.Count == 0,
            $"the tone surfaces' unbound-command allow-list has {KnownUnbound.Count} entr(y/ies) "
            + "again. It was empty when W4 landed and it only ever shrinks: bind the command or "
            + "delete it.");
    }
}
