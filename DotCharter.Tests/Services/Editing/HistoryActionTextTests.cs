using DotCharter.Services.Editing;

namespace DotCharter.Tests.Services.Editing;

/// <summary>
///     The history dialog and the undo/redo hover previews print an action's
///     type name. Graphite's copy rule is sentence case, so only the first word
///     is capitalised — an acronym keeps its capitals.
/// </summary>
public sealed class HistoryActionTextTests
{
    [Theory]
    [InlineData("MutateBeatsAction", "Mutate beats")]
    [InlineData("AddElementsAction", "Add elements")]
    [InlineData("UpsertToneDefinitionAction", "Upsert tone definition")]
    [InlineData("SetFHPAction", "Set FHP")]
    [InlineData("CompositeAction`1", "Composite")]
    [InlineData("Initial state", "Initial state")]
    public void An_action_name_reads_in_sentence_case(string typeName, string expected) =>
        Assert.Equal(expected, HistoryActionText.Humanize(typeName));
}
