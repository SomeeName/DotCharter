using System.Reflection;
using System.Text.RegularExpressions;
using DotCharter.Services.Editing;

namespace DotCharter.Tests.Services.Editing;

/// <summary>
///     #185: Undo / Redo and Edit ▸ History named a step after its C# type —
///     "Mutate notes", "Upsert tone definition" — in English for every language,
///     and a paste or a generator after whatever its FIRST child happened to be.
///     A step is now stored as a token (<see cref="HistoryActionText.LabelKeyFor" />)
///     and worded from the resx when it is shown.
/// </summary>
public sealed class HistoryLabelTests
{
    [Theory]
    [InlineData("MutateNotesAction", "History_MutateNotes")]
    [InlineData("CompositeAction`1", "History_Composite")]
    [InlineData("verb:anchor-fret", "History_Verb_AnchorFret")]
    [InlineData("verb:fret", "History_Verb_Fret")]
    [InlineData("key:History_Paste", "History_Paste")]
    public void A_description_resolves_to_its_resx_key(string description, string key) =>
        Assert.Equal(key, HistoryActionText.LabelKeyFor(description));

    [Fact]
    public void A_step_is_worded_from_the_resx_in_the_ui_language()
    {
        var english = FakeLocalization.English();

        Assert.Equal(english["History_Verb_Fret"], HistoryActionText.Describe("verb:fret", k => english[k]));
        Assert.NotEqual("History_Verb_Fret", english["History_Verb_Fret"]);
    }

    [Fact]
    public void A_step_with_no_string_falls_back_to_the_humanized_name_never_the_key()
    {
        // FakeLocalization.KeysOnly answers every lookup with the key itself — the
        // production service's answer for a key it does not have.
        var keys = FakeLocalization.KeysOnly();

        Assert.Equal("Frobnicate widgets", HistoryActionText.Describe("FrobnicateWidgetsAction", k => keys[k]));
        Assert.Equal("Anchor fret", HistoryActionText.Describe("verb:anchor-fret", k => keys[k]));
        Assert.False(string.IsNullOrEmpty(HistoryActionText.Describe("key:History_Nope", k => keys[k])));
        Assert.Equal(string.Empty, HistoryActionText.Describe(string.Empty, k => keys[k]));
    }

    [Fact]
    public void A_labelled_composite_is_named_by_its_gesture_not_its_first_child()
    {
        var history = new HistoryService(new FakeAppLogger());
        history.Do(new CompositeAction([new ProbeAction(), new ProbeAction()]) { LabelKey = "History_Paste" });

        Assert.Equal("key:History_Paste", history.GetUndoSteps().Single().Description);
    }

    [Fact]
    public void An_unlabelled_composite_still_takes_its_first_childs_name()
    {
        var history = new HistoryService(new FakeAppLogger());
        history.Do(new CompositeAction([new ProbeAction(), new ProbeAction()]));

        Assert.Equal(nameof(ProbeAction), history.GetUndoSteps().Single().Description);
    }

    [Fact]
    public void A_generic_edit_is_named_by_the_verb_it_carries()
    {
        var history = new HistoryService(new FakeAppLogger());
        history.Do(new VerbAction(MergeVerbs.Sustain));

        Assert.Equal("verb:sustain", history.GetUndoSteps().Single().Description);
    }

    private sealed class ProbeAction : IChartAction
    {
        public void Execute() { }
        public void Undo() { }
    }

    private sealed class VerbAction(string verb) : IMergeableAction
    {
        public string MergeVerb => verb;
        public void Execute() { }
        public void Undo() { }
        public bool CanAbsorb(IChartAction next) => false;
        public void Absorb(IChartAction next) { }
    }
}

/// <summary>
///     Every key the history can ask for, enumerated from the TYPES (and the one
///     literal each labelled composite sets) — nothing spells
///     <c>History_MutateNotes</c> out, so a text search would call the family
///     unused. Read by <c>LocalizationSweepTests.EveryComputedKeyFamily_IsCompleteInEveryLanguage</c>.
/// </summary>
internal static class HistoryLabelKeys
{
    private static readonly Regex LabelKeyLiteral = new(@"\bLabelKey\s*=\s*""(?<key>[A-Za-z0-9_]+)""", RegexOptions.Compiled);

    public static IEnumerable<string> All()
    {
        var actionTypes = ActionTypes().ToList();
        var verbs = MergeVerbConstants().ToList();
        var labels = LabelKeyLiterals().ToList();
        // Non-vacuity: the walks still reach the action catalogue and the verbs.
        Assert.True(actionTypes.Count >= 40, $"only {actionTypes.Count} IChartAction types found");
        Assert.True(verbs.Count >= 8, $"only {verbs.Count} merge verbs found");
        Assert.True(labels.Count >= 1, "no LabelKey = \"…\" literal found in the product source");
        return actionTypes.Select(t => HistoryActionText.LabelKeyFor(t.Name))
            .Concat(verbs.Select(v => HistoryActionText.LabelKeyFor(HistoryActionText.VerbToken + v)))
            .Concat(labels)
            .Distinct(StringComparer.Ordinal);
    }

    /// <summary>Every concrete <see cref="IChartAction" /> the product can push.</summary>
    public static IEnumerable<Type> ActionTypes() =>
        new[]
            {
                typeof(HistoryService).Assembly,
                typeof(DotCharter.Presentation.Screens.Dialogs.HistoryViewModel).Assembly,
                typeof(DotCharter.UI.ViewModels.Editor.ChartEditorViewModel).Assembly,
            }
            .SelectMany(a => a.GetTypes())
            .Where(t => t is { IsAbstract: false, IsInterface: false } && typeof(IChartAction).IsAssignableFrom(t)
                && t.GetCustomAttribute<System.Runtime.CompilerServices.CompilerGeneratedAttribute>() == null);

    /// <summary>The <c>const string</c> merge verbs: <see cref="MergeVerbs" /> and any
    /// action's own <c>*MergeVerb</c> constant.</summary>
    public static IEnumerable<string> MergeVerbConstants() =>
        typeof(HistoryService).Assembly.GetTypes()
            .SelectMany(t => t.GetFields(BindingFlags.Public | BindingFlags.Static)
                .Where(f => f is { IsLiteral: true, FieldType.Name: nameof(String) }
                    && (t == typeof(MergeVerbs) || f.Name.EndsWith("MergeVerb", StringComparison.Ordinal))))
            .Select(f => (string)f.GetRawConstantValue()!);

    /// <summary>The keys labelled composites set — literals in the product source.</summary>
    public static IEnumerable<string> LabelKeyLiterals() =>
        new[] { "DotCharter.Services", "DotCharter.Presentation", "DotCharter.UI" }
            .Select(d => Path.Combine(TestPaths.RepoRoot(), d))
            .SelectMany(d => Directory.EnumerateFiles(d, "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .SelectMany(f => LabelKeyLiteral.Matches(File.ReadAllText(f)).Select(m => m.Groups["key"].Value))
            .Distinct(StringComparer.Ordinal);
}
