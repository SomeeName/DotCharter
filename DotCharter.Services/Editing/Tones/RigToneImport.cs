using DotCharter.Services.Ai.Generate;
using DotCharter.Services.State;
using DotCharter.Tone.Audio.Dsp.Tones;

namespace DotCharter.Services.Editing.Tones;

/// <summary>
///     Writes a tone built from the tone rig (<c>RigToRsToneConverter</c>) into the project as ONE
///     undo step: either a new tone, declared in the arrangement's first free slot (Base, then
///     A–D), or the body of an existing tone replaced in place (its key, name and slots untouched).
///     The tone passes the catalogue gate first (<see cref="RsToneCatalogValidation.Require" />):
///     a tone Rocksmith cannot load is refused, never repaired.
/// </summary>
public static class RigToneImport
{
    /// <summary>The key a new tone from the rig starts from (made unique against the project).</summary>
    public const string NewKeyStem = "ToneRig";

    /// <summary>Writes <paramref name="tone" />. Returns the definition written.</summary>
    /// <param name="project">The project whose tones change.</param>
    /// <param name="arrangement">Where a NEW tone is declared; null declares it nowhere.</param>
    /// <param name="history">The undo history the one step goes into.</param>
    /// <param name="tone">The converted tone.</param>
    /// <param name="replaceKey">The tone whose body is replaced, or null for a new tone.</param>
    /// <exception cref="RsToneCatalogException">The tone is not one Rocksmith can load.</exception>
    public static ToneDefinition Apply(ChartProject project, Arrangement? arrangement, IHistoryService history, RsTone tone, string? replaceKey)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(tone);

        var existing = replaceKey is null ? null : project.ToneDefinitions.FirstOrDefault(t => ToneKeyEdits.Same(t.Key, replaceKey));
        var key = existing?.Key ?? RsToneAuthoring.MakeUniqueKey(NewKeyStem, project.ToneDefinitions.Select(t => t.Key));
        var named = RsToneCatalogValidation.Require(tone with { Key = key, Name = existing?.Name ?? key }, RsGearCatalog.Shared,
            $"the tone from the rig '{key}'");
        var definition = new ToneDefinition { Key = key, Name = named.Name, Json = RsToneJsonWriter.Write(named) };

        var actions = new List<IChartAction> { new UpsertToneDefinitionAction(project, existing?.Key, definition) };
        if (existing is null && arrangement is not null && DeclareSlot(arrangement, key) is { } slots) actions.Add(slots);
        history.Do(actions.Count == 1 ? actions[0] : new CompositeAction(actions) { LabelKey = "History_ImportFromToneRig" });
        return definition;
    }

    /// <summary>The Base slot when it is empty, else the first free A–D slot, as one undoable action
    /// (null when every slot is taken: the tone still exists in the project).</summary>
    private static IChartAction? DeclareSlot(Arrangement arrangement, string key)
    {
        var before = ToneSlotAssignment.Capture(arrangement);
        if (string.IsNullOrWhiteSpace(arrangement.ToneBase))
        {
            var after = ToneSlotAssignment.FromSlots(key, arrangement.ToneA, arrangement.ToneB, arrangement.ToneC, arrangement.ToneD);
            return new AssignArrangementTonesAction(arrangement, before, after);
        }

        ToneGenerationRun.DeclareInAFreeSlot(arrangement, key);
        return ToneGenerationRun.BuildSlotAssignmentAction(arrangement, before);
    }
}
