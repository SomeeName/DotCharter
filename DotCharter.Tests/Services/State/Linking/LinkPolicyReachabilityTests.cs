using System.Reflection;
using System.Runtime.CompilerServices;

namespace DotCharter.Tests.Services.State.Linking;

/// <summary>
///     #88: every chart action declares a link policy, and a NEW action cannot ship without one.
///     <list type="bullet">
///         <item><b>The registry</b> names every product <see cref="IChartAction" /> type with the
///         policy it must declare. The reflection walk fails for a type missing from it (a new
///         action has to be classified: is it a gesture, a whole-chart tool, or unable to touch
///         linked content?) and for a type that declares something else.</item>
///         <item><b>The tool flows</b> build ordinary edits but are whole-chart tools; each file must
///         wrap them with its policy — <see cref="LinkPolicy.ShareFirstChanged" /> for a generator,
///         <see cref="LinkPolicy.UnlinkOnDivergence" /> for outside truth — and each has a behaviour test.</item>
///     </list>
///     The default for an UNDECLARED action (a test fake) is the safe one, unlink: an unknown tool
///     never fans its change out to every copy.
/// </summary>
public sealed class LinkPolicyReachabilityTests
{
    private static readonly Dictionary<string, LinkPolicy?> Registry = new(StringComparer.Ordinal)
    {
        // Editing gestures: propagate.
        ["CollectionActionBase`1"] = LinkPolicy.Propagate,
        ["AddElementsAction`1"] = LinkPolicy.Propagate,
        ["RemoveElementsAction`1"] = LinkPolicy.Propagate,
        ["MutateElementsAction`1"] = LinkPolicy.Propagate,
        ["AddNotesAction"] = LinkPolicy.Propagate,
        ["RemoveNotesAction"] = LinkPolicy.Propagate,
        ["MutateNotesAction"] = LinkPolicy.Propagate,
        ["AddChordsAction"] = LinkPolicy.Propagate,
        ["RemoveChordsAction"] = LinkPolicy.Propagate,
        ["MutateChordsAction"] = LinkPolicy.Propagate,
        ["AddHandShapesAction"] = LinkPolicy.Propagate,
        ["RemoveHandShapesAction"] = LinkPolicy.Propagate,
        ["MutateHandShapesAction"] = LinkPolicy.Propagate,
        ["AddAnchorsAction"] = LinkPolicy.Propagate,
        ["RemoveAnchorsAction"] = LinkPolicy.Propagate,
        ["MutateAnchorsAction"] = LinkPolicy.Propagate,
        ["AddEventPointsAction"] = LinkPolicy.Propagate,
        ["RemoveEventPointsAction"] = LinkPolicy.Propagate,
        ["MutateEventPointsAction"] = LinkPolicy.Propagate,
        ["AddToneChangesAction"] = LinkPolicy.Propagate,
        ["RemoveToneChangesAction"] = LinkPolicy.Propagate,
        ["AddChordTemplatesAction"] = LinkPolicy.Propagate,
        ["RemoveChordTemplatesAction"] = LinkPolicy.Propagate,
        ["EditTemplateGlobalAction"] = LinkPolicy.Propagate,
        ["SwitchTemplateAction"] = LinkPolicy.Propagate,
        ["CreateAndSwitchTemplateAction"] = LinkPolicy.Propagate,
        ["SetTemplateFingersAction"] = LinkPolicy.Propagate,
        ["SetTemplateNameAction"] = LinkPolicy.Propagate,
        ["SetTemplateIdsAction"] = LinkPolicy.Propagate,
        ["SetHandShapeTemplateIdsAction"] = LinkPolicy.Propagate,
        ["SetTemplatePinnedAction"] = LinkPolicy.Propagate,
        ["AddAnchorAction"] = LinkPolicy.Propagate,
        ["MutateAnchorAction"] = LinkPolicy.Propagate,
        ["ResizeHandShapesAction"] = LinkPolicy.Propagate,
        ["SetHandShapeSpanAction"] = LinkPolicy.Propagate,

        // Whole-chart tools: never propagated; what they make differ is unlinked.
        ["ProjectSnapshotAction"] = LinkPolicy.UnlinkOnDivergence,
        ["ArrangementSnapshotAction"] = LinkPolicy.UnlinkOnDivergence,
        ["AudioSpliceAction"] = LinkPolicy.UnlinkOnDivergence,
        ["BeatScaleAction"] = LinkPolicy.UnlinkOnDivergence,

        // Cannot change linked content in beats.
        ["MutateBeatsAction"] = LinkPolicy.Retain,
        ["SetSongMetadataAction"] = LinkPolicy.Retain,
        ["AddArrangementAction"] = LinkPolicy.Retain,
        ["MutateArrangementSettingsAction"] = LinkPolicy.Retain,
        ["EditPhrasesAction"] = LinkPolicy.Retain,
        ["DeleteChordShapesAction"] = LinkPolicy.Retain,
        ["RemoveStrandedShapesAction"] = LinkPolicy.Retain,
        ["SetPhraseSoloAction"] = LinkPolicy.Retain,
        ["RenameStringFieldAction`1"] = LinkPolicy.Retain,
        ["SetKeySignatureAction"] = LinkPolicy.Retain,
        ["MoveLyricAction"] = LinkPolicy.Retain,
        ["ResizeLyricAction"] = LinkPolicy.Retain,
        ["ResizeLyricPhraseAction"] = LinkPolicy.Retain,
        ["RemoveLyricAction"] = LinkPolicy.Retain,
        ["SetLyricNoteAction"] = LinkPolicy.Retain,
        ["RenameLyricAction"] = LinkPolicy.Retain,
        ["LyricTapAction"] = LinkPolicy.Retain,
        ["AddAnnotationAction"] = LinkPolicy.Retain,
        ["RemoveAnnotationAction"] = LinkPolicy.Retain,
        ["AddVocalTrackAction"] = LinkPolicy.Retain,
        ["SetVocalPhrasesAction"] = LinkPolicy.Retain,
        ["SetStyleAction"] = LinkPolicy.Retain,
        ["SetSynthPlayStyleAction"] = LinkPolicy.Retain,
        ["UpsertToneDefinitionAction"] = LinkPolicy.Retain,
        ["AssignArrangementTonesAction"] = LinkPolicy.Retain,
        ["RetargetToneChangesAction"] = LinkPolicy.Retain,
        ["ToneKeyRenameJournalStep"] = LinkPolicy.Retain,
        ["LinkFollowUpAction"] = LinkPolicy.Retain,
        ["CreateLinkGroupAction"] = LinkPolicy.Retain,
        ["OnlyHereAction"] = LinkPolicy.Retain,
        ["SetLinkGroupsAction"] = LinkPolicy.Retain,
        ["AddLinkPlacementAction"] = LinkPolicy.Retain,

        // Computed: an override, else the strictest of its children (LinkPolicies.Of).
        ["CompositeAction"] = null
    };

    /// <summary>The whole-chart tools that commit ordinary edits, the file that must wrap them, the
    /// policy it wraps them with, and the behaviour test that proves the wrap.</summary>
    public static readonly (string File, LinkPolicy Policy, string Test)[] ToolFlows =
    [
        ("DotCharter.Services/Ai/Chart/AiChartApplier.cs", LinkPolicy.ShareFirstChanged,
            nameof(LinkedMeasuresGeneratorTests.An_ai_chat_pass_keeps_every_link_and_writes_the_first_copy_into_the_others)),
        ("DotCharter.Services/Editing/Maintenance/FhpGenerationCommands.cs", LinkPolicy.ShareFirstChanged,
            nameof(LinkedMeasuresGeneratorTests.Generate_fhp_keeps_every_link_and_writes_the_first_copy_into_the_others)),
        ("DotCharter.Services/Editing/Maintenance/ChartMaintenanceService.Generation.cs", LinkPolicy.ShareFirstChanged,
            nameof(LinkedMeasuresGeneratorTests.Generate_hand_shapes_keeps_every_link_and_carries_the_shapes_to_the_other_arrangement)),
        ("DotCharter.Services/Editing/Maintenance/ChartMaintenanceService.PowerTools.cs", LinkPolicy.ShareFirstChanged,
            nameof(LinkedMeasuresGeneratorTests.Generate_levels_keeps_every_link_and_the_lower_levels_stay_per_arrangement)),
        ("DotCharter.UI/ViewModels/Panels/ChordTemplateEditorViewModel.cs", LinkPolicy.ShareFirstChanged,
            nameof(LinkedMeasuresGeneratorTests.Generate_fingerings_keeps_every_link_and_carries_the_fingering_to_the_other_arrangement)),
        ("DotCharter.Services/Editing/Maintenance/ChartMaintenanceService.cs", LinkPolicy.UnlinkOnDivergence,
            nameof(LinkedMeasuresToolPolicyTests.A_single_undo_step_tool_that_changes_one_copy_unlinks_it_and_never_propagates)),
        ("DotCharter.Services/Editing/Maintenance/ChartMaintenanceService.cs", LinkPolicy.UnlinkOnDivergence,
            nameof(LinkedMeasuresToolPolicyTests.Refret_output_is_never_altered_by_links_and_surviving_links_hold_equal_copies)),
        ("DotCharter.Services/Editing/Maintenance/ChartMaintenanceService.cs", LinkPolicy.UnlinkOnDivergence,
            nameof(LinkedMeasuresToolPolicyTests.Sync_arrangement_unlinks_the_copy_it_made_differ)),
        ("DotCharter.Services/Editing/Maintenance/ChartMaintenanceService.cs", LinkPolicy.UnlinkOnDivergence,
            nameof(LinkedMeasuresToolPolicyTests.Inserting_a_beat_inside_a_placement_unlinks_it_and_shifts_the_others))
    ];

    private static IEnumerable<Type> ProductActionTypes() =>
        new[] { typeof(HistoryService).Assembly, typeof(DotCharter.Presentation.Screens.Dialogs.FhpViewModel).Assembly, typeof(DotCharter.UI.App).Assembly }
            .SelectMany(a => a.GetTypes())
            .Where(t => t.IsClass && typeof(IChartAction).IsAssignableFrom(t))
            .Where(t => t.GetCustomAttribute<CompilerGeneratedAttribute>() == null);

    [Fact]
    public void Every_product_chart_action_declares_a_link_policy()
    {
        var undeclared = ProductActionTypes()
            .Where(t => !typeof(ILinkPolicyAction).IsAssignableFrom(t))
            .Select(t => t.FullName)
            .ToList();

        Assert.True(undeclared.Count == 0,
            "These chart actions declare no link policy, so linked copies would be unlinked by default. "
            + "Implement ILinkPolicyAction (Propagate for an editing gesture, UnlinkOnDivergence for a "
            + "whole-chart tool, Retain when it cannot touch linked content):\n  " + string.Join("\n  ", undeclared));
    }

    [Fact]
    public void Every_product_chart_action_is_registered_with_the_policy_it_declares()
    {
        var types = ProductActionTypes().ToList();
        var unregistered = types.Where(t => !Registry.ContainsKey(t.Name)).Select(t => t.FullName).ToList();
        Assert.True(unregistered.Count == 0,
            "New chart actions: classify each in LinkPolicyReachabilityTests.Registry:\n  " + string.Join("\n  ", unregistered));

        var stale = Registry.Keys.Where(name => types.All(t => t.Name != name)).ToList();
        Assert.True(stale.Count == 0, "Registry names no longer exist:\n  " + string.Join("\n  ", stale));

        foreach (var type in types.Where(t => !t.IsAbstract && Registry[t.Name] is not null))
        {
            var closed = type.IsGenericTypeDefinition ? type.MakeGenericType(typeof(object)) : type;
            var instance = (ILinkPolicyAction)RuntimeHelpers.GetUninitializedObject(closed);
            Assert.True(Registry[type.Name] == instance.LinkPolicy,
                $"{type.FullName} declares {instance.LinkPolicy}; the registry says {Registry[type.Name]}.");
        }
    }

    [Fact]
    public void Every_whole_chart_tool_flow_wraps_its_edits_with_its_policy_and_has_its_behaviour_test()
    {
        var tests = typeof(LinkPolicyReachabilityTests).Assembly.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Where(m => m.GetCustomAttributes().Any(a => a.GetType().Name is "FactAttribute" or "TheoryAttribute"))
            .Select(m => m.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.All(ToolFlows, flow =>
        {
            var code = SourceText.StripComments(File.ReadAllText(Path.Combine(TestPaths.RepoRoot(), flow.File)));
            Assert.Contains("LinkPolicy." + flow.Policy, code, StringComparison.Ordinal);
            Assert.Contains(flow.Test, tests);
        });
    }

    [Fact]
    public void A_composite_takes_its_override_else_its_strictest_child_and_an_undeclared_action_unlinks()
    {
        var gesture = new MutateNotesAction([], [], [], []);
        var beats = new MutateBeatsAction([], [], []);

        Assert.Equal(LinkPolicy.Propagate, LinkPolicies.Of(gesture));
        Assert.Equal(LinkPolicy.Propagate, LinkPolicies.Of(new CompositeAction([new AddNotesAction([], [new GuitarNote()]), new SetKeySignatureAction(new ChartData(), 1, false)])));
        Assert.Equal(LinkPolicy.UnlinkOnDivergence,
            LinkPolicies.Of(new CompositeAction([new AddNotesAction([], [new GuitarNote()])]) { LinkPolicyOverride = LinkPolicy.UnlinkOnDivergence }));
        var add = new AddNotesAction([], [new GuitarNote()]);
        var shared = new CompositeAction([add]) { LinkPolicyOverride = LinkPolicy.ShareFirstChanged };
        Assert.Equal(LinkPolicy.ShareFirstChanged, LinkPolicies.Of(new CompositeAction([add, shared, add])));
        Assert.Equal(LinkPolicy.UnlinkOnDivergence, LinkPolicies.Of(new CompositeAction([shared, new UndeclaredAction()])));
        Assert.Equal(LinkPolicy.Retain, LinkPolicies.Of(beats));
        Assert.Equal(LinkPolicy.UnlinkOnDivergence, LinkPolicies.Of(new UndeclaredAction()));
    }

    private sealed class UndeclaredAction : IChartAction
    {
        public void Execute() { }
        public void Undo() { }
    }
}
