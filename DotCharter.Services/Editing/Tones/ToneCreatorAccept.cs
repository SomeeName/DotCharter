using DotCharter.Services.Ai.Chart;
using DotCharter.Services.Ai.Generate;
using DotCharter.Services.State;
using DotCharter.Tone.Audio.Dsp.Tones;

namespace DotCharter.Services.Editing.Tones;

/// <summary>Where an accepted AI proposal goes.</summary>
public enum ToneCreatorTarget
{
    /// <summary>Into the tone being edited: its key and name stay, so the placed changes that name it follow.</summary>
    ReplaceEdited,

    /// <summary>A new tone definition, declared in a free slot of the arrangement.</summary>
    NewTone
}

/// <summary>Why an accept cannot be written.</summary>
public enum ToneCreatorRefusal
{
    None,

    /// <summary>Replace was asked with no tone being edited (or it was deleted meanwhile).</summary>
    NoEditedTone,

    /// <summary>The arrangement's Base and A–D slots are all taken: the game's limit.</summary>
    NoFreeSlot,

    /// <summary>The tone, re-keyed, fails the catalogue gate (an unusable key, say).</summary>
    InvalidTone
}

/// <summary>An accept's plan: the ONE action to run, the key it writes, and the slot it declares the tone in.</summary>
public sealed record ToneCreatorAcceptPlan(IChartAction? Action, string Key, string? Slot, ToneCreatorRefusal Refusal, string Detail = "")
{
    public bool IsReady => Action is not null;
}

/// <summary>
///     The Tone designer's AI creator, accepted: the proposal written into the project as ONE undo step — a replaced
///     body (<see cref="UpsertToneDefinitionAction" />, the designer's own action), or a new definition with its slot
///     (<see cref="AssignArrangementTonesAction" />), and optionally a placed tone change naming it. Pure: the caller
///     hands the plan's action to the history.
/// </summary>
public static class ToneCreatorAccept
{
    /// <summary>The slot names, in the order a free one is taken; the game has these five and no more.</summary>
    public static IReadOnlyList<string> SlotOrder { get; } = ["Base", "A", "B", "C", "D"];

    /// <summary>The first empty slot of <paramref name="arrangement" /> in <see cref="SlotOrder" />, or null when all five are taken.</summary>
    public static string? FreeSlot(Arrangement arrangement)
    {
        ArgumentNullException.ThrowIfNull(arrangement);
        string[] values = [arrangement.ToneBase, arrangement.ToneA, arrangement.ToneB, arrangement.ToneC, arrangement.ToneD];
        for (var i = 0; i < values.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(values[i])) return SlotOrder[i];
        }

        return null;
    }

    /// <summary>
    ///     Plans the accept. A new tone takes a unique key made from <paramref name="newName" />; a tone placed at
    ///     <paramref name="placeAtMs" /> that its arrangement does not declare yet is declared in a free slot too.
    /// </summary>
    /// <param name="project">The live project.</param>
    /// <param name="arrangement">The arrangement whose slot and timeline take the tone.</param>
    /// <param name="tone">The validated proposal.</param>
    /// <param name="target">Replace the edited tone, or save a new one.</param>
    /// <param name="editedKey">The tone being edited, for <see cref="ToneCreatorTarget.ReplaceEdited" />.</param>
    /// <param name="newName">The new tone's name, for <see cref="ToneCreatorTarget.NewTone" />.</param>
    /// <param name="placeAtMs">Where to place a tone change naming the tone, or null for none.</param>
    public static ToneCreatorAcceptPlan Build(ChartProject project, Arrangement arrangement, RsTone tone, ToneCreatorTarget target,
        string? editedKey, string newName, double? placeAtMs)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(arrangement);
        ArgumentNullException.ThrowIfNull(tone);

        string key, name;
        string? replaceKey = null;
        if (target == ToneCreatorTarget.ReplaceEdited)
        {
            if (editedKey is null || AiToneApplier.Find(project, editedKey) is not { } edited)
                return Refuse(ToneCreatorRefusal.NoEditedTone);
            key = edited.Key;
            name = string.IsNullOrWhiteSpace(edited.Name) ? edited.Key : edited.Name;
            replaceKey = edited.Key;
        }
        else
        {
            name = string.IsNullOrWhiteSpace(newName) ? tone.Name : newName.Trim();
            key = RsToneAuthoring.MakeUniqueKey(RsToneAuthoring.SanitizeKey(name), project.ToneDefinitions.Select(d => d.Key));
        }

        var keyed = tone with { Key = key, Name = name };
        if (RsToneCatalogValidation.FirstFault(keyed, RsGearCatalog.Shared) is { } fault)
            return Refuse(ToneCreatorRefusal.InvalidTone, fault.Message);

        var actions = new List<IChartAction>(3)
        {
            new UpsertToneDefinitionAction(project, replaceKey, new ToneDefinition { Key = key, Name = name, Json = RsToneJsonWriter.Write(keyed) })
        };

        string? slot = null;
        var needsSlot = target == ToneCreatorTarget.NewTone || (placeAtMs is not null && !ChartToneNames.IsDeclared(arrangement, key));
        if (needsSlot)
        {
            slot = FreeSlot(arrangement);
            if (slot is null) return Refuse(ToneCreatorRefusal.NoFreeSlot);
            actions.Add(Declare(arrangement, slot, key));
        }

        if (placeAtMs is { } ms) actions.AddRange(Place(project, arrangement, key, ms));
        return new ToneCreatorAcceptPlan(new CompositeAction(actions) { LabelKey = "History_AiToneCreate" }, key, slot,
            ToneCreatorRefusal.None);
    }

    private static ToneCreatorAcceptPlan Refuse(ToneCreatorRefusal why, string detail = "") => new(null, "", null, why, detail);

    private static AssignArrangementTonesAction Declare(Arrangement arrangement, string slot, string key)
    {
        var before = ToneSlotAssignment.Capture(arrangement);
        string Pick(string name, string current) => name == slot ? key : current;
        var after = ToneSlotAssignment.FromSlots(Pick("Base", arrangement.ToneBase), Pick("A", arrangement.ToneA),
            Pick("B", arrangement.ToneB), Pick("C", arrangement.ToneC), Pick("D", arrangement.ToneD));
        // A slot write never moves the starting tone unless it fills the base.
        if (slot != "Base") after = after with { StartingTone = before.StartingTone };
        return new AssignArrangementTonesAction(arrangement, before, after);
    }

    /// <summary>A tone change at the position, replacing one already there.</summary>
    private static IEnumerable<IChartAction> Place(ChartProject project, Arrangement arrangement, string key, double ms)
    {
        var position = TimelineMath.PositionFromMs(Math.Max(0, ms), project.Beats);
        var there = arrangement.ToneChanges.Where(t => t.Position == position).ToList();
        if (there.Count > 0) yield return new RemoveToneChangesAction(arrangement.ToneChanges, there);
        yield return new AddToneChangesAction(arrangement.ToneChanges, [new ToneChange { Position = position, ToneName = key }]);
    }
}
