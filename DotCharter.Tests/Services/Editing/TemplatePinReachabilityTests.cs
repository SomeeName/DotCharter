using System.Reflection;
using System.Text.RegularExpressions;

namespace DotCharter.Tests.Services.Editing;

/// <summary>
///     #785: every AUTOMATIC flow that re-fingers or replaces a chord template asks
///     <see cref="ChordTemplate.IsPinned" /> first, and a NEW flow cannot skip the question.
///     <list type="bullet">
///         <item><b>The registry</b> names each known flow, the source file that must ask, and the
///         behaviour test that proves it does (<see cref="TemplatePinTests" />, one per flow, each
///         with an unpinned control); a renamed test or a dropped check fails here.</item>
///         <item><b>The lint</b> finds every product file that calls an automatic fingering or grip
///         verb and fails unless that file asks <c>IsPinned</c> or is listed in
///         <see cref="NotAFlow" /> with the REASON it never rewrites an existing template.</item>
///     </list>
/// </summary>
public sealed class TemplatePinReachabilityTests
{
    /// <summary>The flows #785 names, and the one the wheel adds: (file that must ask, behaviour test).</summary>
    public static readonly (string File, string Test)[] Flows =
    [
        ("DotCharter.Services/Editing/ChordTemplateResolver.cs", nameof(TemplatePinTests.Resolver_KeepsAPinnedTemplateItsChordStillPlays)),
        ("DotCharter.Services/Editing/Fingering/ArrangementFingeringStyle.cs", nameof(TemplatePinTests.FingeringStyle_FollowAuthored_LeavesAPinnedPairAlone)),
        ("DotCharter.Services/Editing/Fingering/ArrangementFingeringStyle.cs", nameof(TemplatePinTests.FingeringStyle_SetSuggestedFingers_LeavesAPinnedGripAlone)),
        ("DotCharter.Services/Editing/Refret/RefretPlanner.cs", nameof(TemplatePinTests.Refret_NeverMovesAChordOnAPinnedTemplate)),
        ("DotCharter.Services/Editing/FretAnchors/ChordFingeringFollower.cs", nameof(TemplatePinTests.Follower_NeverReFingersAPinnedGrip)),
        ("DotCharter.Core/Utilities/HandShapeAutoTemplate.cs", nameof(TemplatePinTests.HandShapeAutoTemplate_NeverRebuildsAPinnedGrip)),
        ("DotCharter.UI/Input/TimelineWheelAdjuster.cs", nameof(TemplatePinTests.HandShapeAutoTemplate_NeverRebuildsAPinnedGrip)),
        ("DotCharter.UI/ViewModels/Panels/ChordTemplateFingeringBackfill.cs", "AutoFill_LeavesAPinnedTemplateAsItIs"),
        ("DotCharter.Services/Ai/Chart/AiChartApplier.cs", "An_AI_fingering_never_moves_a_chord_off_a_pinned_template")
    ];

    /// <summary>Files that call a verb below but never rewrite a template that already exists.</summary>
    private static readonly Dictionary<string, string> NotAFlow = new(StringComparer.Ordinal)
    {
        ["DotCharter.Core/Utilities/ChordTemplateFingerSetter.cs"] = "the pure setter itself; its callers decide",
        ["DotCharter.Services/Editing/Fingering/FingeringGenerationService.cs"] = "suggests on a CLONE and returns it; the pin lives in its callers and in ArrangementFingeringStyle.SetSuggestedFingers",
        ["DotCharter.Services/Editing/Fingering/IFingeringGenerationService.cs"] = "the interface",
        ["DotCharter.Services/Editing/Refret/RefretService.cs"] = "fingers only the template it mints for a moved chord group; RefretPlanner fixes every chord on a pinned grip, so none is moved",
        ["DotCharter.Services/Editing/ChordShapes/ChordShapeCatalog.cs"] = "read-only: compares a shape with its suggestion",
        ["DotCharter.Services/Editing/ChordShapes/GhostGems.cs"] = "fingers only the grips it mints, and mints them pinned",
        ["DotCharter.Services/Editing/HandShapes/HandShapeSuggester.cs"] = "fingers a probe clone for a suggested bar's label",
        ["DotCharter.Services/Parsing/GuitarPro/ChordHandShapeBuilder.cs"] = "import: fingers a template it has just minted",
        ["DotCharter.Services/Parsing/GuitarPro/GpHandShapeBuilder.cs"] = "import: fingers a template it has just minted",
        ["DotCharter.Services/Ai/Chart/AiChartPrompt.cs"] = "reads the free grips to describe them; FreeGripResolver asks IsPinned",
        ["DotCharter.Services/Editing/FretAnchors/ImportFhpPass.cs"] = "delegates to FollowAuthored and FreeGripResolver, which ask IsPinned",
        ["DotCharter.Services/Editing/Maintenance/FhpGenerationCommands.cs"] = "Generate FHP: delegates to FreeGripResolver, which asks IsPinned",
        ["DotCharter.UI/ViewModels/Panels/ChordTemplateEditorViewModel.cs"] = "the display seed of an UNTEMPLATED chord, a probe never added"
    };

    private static readonly Regex Verb = new(
        @"\b(SetSuggestedFingers|SuggestFingeringFor|FreeGripResolver|FollowAuthored|RebuildHandShapeGrip|HandShapeAutoTemplate\.Rebuild)\s*\(",
        RegexOptions.Compiled);

    private static readonly string[] ProductProjects =
        ["DotCharter.Core", "DotCharter.Services", "DotCharter.UI", "DotCharter.Presentation", "DotCharter.Tone"];

    private static string Code(string relative) =>
        SourceText.StripComments(File.ReadAllText(Path.Combine(TestPaths.RepoRoot(), relative)));

    [Fact]
    public void EveryRegisteredFlow_AsksIsPinned_AndHasItsBehaviourTest()
    {
        var tests = typeof(TemplatePinReachabilityTests).Assembly.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Where(m => m.GetCustomAttributes().Any(a => a.GetType().Name is "FactAttribute" or "TheoryAttribute"))
            .Select(m => m.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.All(Flows, flow =>
        {
            Assert.Contains("IsPinned", Code(flow.File), StringComparison.Ordinal);
            Assert.Contains(flow.Test, tests);
        });
    }

    [Fact]
    public void EveryFileThatCallsAnAutomaticFingeringVerb_AsksIsPinned_OrSaysWhyItNeedNot()
    {
        var root = TestPaths.RepoRoot();
        var offenders = new List<string>();
        var seen = 0;
        foreach (var project in ProductProjects)
        {
            foreach (var path in Directory.EnumerateFiles(Path.Combine(root, project), "*.cs", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                if (relative.Contains("/obj/", StringComparison.Ordinal) || relative.Contains("/bin/", StringComparison.Ordinal)) continue;
                var code = SourceText.StripComments(File.ReadAllText(path));
                if (!Verb.IsMatch(code)) continue;
                seen++;
                if (code.Contains("IsPinned", StringComparison.Ordinal) || NotAFlow.ContainsKey(relative)) continue;
                offenders.Add(relative);
            }
        }

        Assert.True(seen >= Flows.Select(f => f.File).Distinct().Count() - 1,
            $"the lint found only {seen} callers — the verb pattern no longer matches the product");
        Assert.True(offenders.Count == 0,
            "These files run an automatic fingering or grip rebuild without asking ChordTemplate.IsPinned (#785). " +
            "Skip a pinned template, or list the file in NotAFlow with why it never rewrites an existing one:\n" +
            string.Join("\n", offenders));
    }

    [Fact]
    public void EveryExemption_StillCallsAVerb_AndStillExists() =>
        Assert.All(NotAFlow.Keys, relative => Assert.Matches(Verb, Code(relative)));
}
