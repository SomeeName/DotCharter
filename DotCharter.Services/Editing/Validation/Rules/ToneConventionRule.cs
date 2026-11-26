using System.Text.Json;

namespace DotCharter.Services.Editing.Validation.Rules;

/// <summary>
///     The tone conventions experienced charters follow, as warnings (D20–D25). Each is about the
///     tones the arrangement USES — its declared slots and its tone changes — so a tone that only sits
///     in the project for another arrangement is never judged here:
///     <list type="bullet">
///         <item>D20: a tone change before the first note or chord. Nothing has played yet, so the
///         change only belongs in the starting tone.</item>
///         <item>D21: exactly one tone change. Usually a leftover, or a starting tone set to the wrong one.</item>
///         <item>D22: a tone whose display name differs from its key. The chart switches by key and the
///         game shows the name, so a mismatch reads as the wrong tone.</item>
///         <item>D23: a tone with no amp or no cabinet, or with more than <see cref="MaxEffects" /> pedals
///         and racks.</item>
///         <item>D24: two different tones under one name, or one name in two of the four switchable slots.</item>
///         <item>D25: a tone change to the tone that is already on.</item>
///     </list>
///     A tone change naming no declared tone and a declared tone with no body are D01 and D04
///     (<see cref="ToneSilenceAndPhraseRule" />); a change ON a note is I30 (<see cref="NoteRule" />).
///     Pure: domain model, beat grid and the project's tone bodies only.
/// </summary>
public sealed class ToneConventionRule : IArrangementRule
{
    /// <summary>The most pedals and racks one tone carries in the charting conventions.</summary>
    public const int MaxEffects = 4;

    // Position slop (ms), the one the tone-gap checks use.
    private const double Epsilon = 0.5;

    private static readonly string[] EffectSlots =
    [
        "PrePedal1", "PrePedal2", "PrePedal3", "PrePedal4", "Rack1", "Rack2", "Rack3", "Rack4",
        "PostPedal1", "PostPedal2", "PostPedal3", "PostPedal4"
    ];

    private static readonly JsonDocumentOptions DocOptions = new() { AllowTrailingCommas = true };

    public void Validate(ArrangementValidationContext context, ICollection<ArrangementIssue> issues)
    {
        var arrangement = context.Arrangement;
        var beats = context.Beats;
        var changes = arrangement.ToneChanges.OrderBy(change => change.Position).ToList();

        CheckBeforeFirstNote(arrangement, beats, changes, issues);
        if (changes.Count == 1)
        {
            issues.Add(ArrangementIssues.Make(ArrangementIssueType.SingleToneChange, "Validation_SingleToneChange",
                changes[0].Position.GetPositionMs(beats), args: [Shown(changes[0].ToneName)]));
        }

        CheckRepeats(arrangement, beats, changes, issues);
        CheckSlotDuplicates(arrangement, issues, out var reported);
        if (context.Project is { } project) CheckBodies(UsedTones(project, arrangement, changes), issues, reported);
    }

    private static void CheckBeforeFirstNote(Arrangement arrangement, IList<BeatMarker> beats, List<ToneChange> changes,
        ICollection<ArrangementIssue> issues)
    {
        if (changes.Count == 0) return;
        var first = double.MaxValue;
        foreach (var level in arrangement.Levels)
        {
            foreach (var note in level.Notes) first = Math.Min(first, note.Position.GetPositionMs(beats));
            foreach (var chord in level.Chords) first = Math.Min(first, chord.Position.GetPositionMs(beats));
        }

        if (first == double.MaxValue) return;
        foreach (var change in changes)
        {
            var ms = change.Position.GetPositionMs(beats);
            if (ms >= first - Epsilon) break;
            issues.Add(ArrangementIssues.Make(ArrangementIssueType.ToneChangeBeforeFirstNote,
                "Validation_ToneChangeBeforeFirstNote", ms, args: [Shown(change.ToneName)]));
        }
    }

    private static void CheckRepeats(Arrangement arrangement, IList<BeatMarker> beats, List<ToneChange> changes,
        ICollection<ArrangementIssue> issues)
    {
        var active = string.IsNullOrWhiteSpace(arrangement.StartingTone) ? arrangement.ToneBase : arrangement.StartingTone;
        foreach (var change in changes)
        {
            if (!string.IsNullOrWhiteSpace(change.ToneName) && SameName(change.ToneName, active))
            {
                issues.Add(ArrangementIssues.Make(ArrangementIssueType.RepeatedTone, "Validation_RepeatedTone",
                    change.Position.GetPositionMs(beats), args: [Shown(change.ToneName)]));
            }

            active = change.ToneName;
        }
    }

    private static void CheckSlotDuplicates(Arrangement arrangement, ICollection<ArrangementIssue> issues,
        out HashSet<string> reported)
    {
        reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var slots = new[] { arrangement.ToneA, arrangement.ToneB, arrangement.ToneC, arrangement.ToneD }
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!.Trim());
        foreach (var group in slots.GroupBy(name => name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
        {
            reported.Add(group.Key);
            issues.Add(ArrangementIssues.Make(ArrangementIssueType.DuplicateToneName, "Validation_DuplicateToneName",
                args: [group.Last()]));
        }
    }

    private static List<ToneDefinition> UsedTones(ChartProject project, Arrangement arrangement, List<ToneChange> changes)
    {
        var used = new HashSet<string>(ChartToneNames.Declared(arrangement), StringComparer.OrdinalIgnoreCase);
        foreach (var change in changes)
        {
            if (!string.IsNullOrWhiteSpace(change.ToneName)) used.Add(change.ToneName.Trim());
        }

        return project.ToneDefinitions
            .Where(tone => used.Contains(tone.Key.Trim()) || used.Contains(tone.Name.Trim()))
            .ToList();
    }

    private static void CheckBodies(List<ToneDefinition> tones, ICollection<ArrangementIssue> issues,
        HashSet<string> reported)
    {
        foreach (var tone in tones)
        {
            var key = tone.Key.Trim();
            if (!string.IsNullOrWhiteSpace(tone.Name) && !SameName(tone.Name, key))
            {
                issues.Add(ArrangementIssues.Make(ArrangementIssueType.ToneNameKeyMismatch,
                    "Validation_ToneNameKeyMismatch", args: [key, tone.Name.Trim()]));
            }

            if (ReadGear(tone.Json) is not var (hasAmp, hasCabinet, effects)) continue;
            if (!hasAmp || !hasCabinet)
            {
                issues.Add(ArrangementIssues.Make(ArrangementIssueType.ToneGearContent,
                    "Validation_ToneWithoutAmpOrCabinet", args: [key]));
            }
            else if (effects > MaxEffects)
            {
                issues.Add(ArrangementIssues.Make(ArrangementIssueType.ToneGearContent,
                    "Validation_ToneTooManyEffects", args: [key, effects, MaxEffects]));
            }
        }

        // Two DIFFERENT tones (keys) under one name: the game's tone list shows two identical entries.
        var named = tones.Where(tone => !string.IsNullOrWhiteSpace(tone.Name))
            .GroupBy(tone => tone.Name.Trim(), StringComparer.OrdinalIgnoreCase);
        foreach (var group in named)
        {
            if (group.Select(tone => tone.Key.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() < 2) continue;
            if (!reported.Add(group.Key)) continue;
            issues.Add(ArrangementIssues.Make(ArrangementIssueType.DuplicateToneName, "Validation_DuplicateToneName",
                args: [group.Last().Name.Trim()]));
        }
    }

    /// <summary>Amp, cabinet and effect count of a tone body, or null when it holds no readable
    ///     <c>GearList</c> (an empty body is D04's to report, not a tone without gear).</summary>
    private static (bool Amp, bool Cabinet, int Effects)? ReadGear(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json, DocOptions);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("GearList", out var gear)
                || gear.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            return (HasGear(gear, "Amp"), HasGear(gear, "Cabinet"), EffectSlots.Count(slot => HasGear(gear, slot)));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool HasGear(JsonElement gearList, string slot) =>
        gearList.TryGetProperty(slot, out var gear)
        && gear.ValueKind == JsonValueKind.Object
        && gear.TryGetProperty("Key", out var key)
        && key.ValueKind == JsonValueKind.String
        && !string.IsNullOrWhiteSpace(key.GetString());

    private static bool SameName(string? a, string? b) =>
        string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);

    private static object Shown(string? name) =>
        string.IsNullOrWhiteSpace(name) ? new IssueTextRef("Validation_UnnamedTone") : name.Trim();
}
