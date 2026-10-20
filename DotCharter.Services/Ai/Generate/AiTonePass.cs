using System.Globalization;
using System.Text;
using DotCharter.Localization;
using DotCharter.Services.Ai.Chart;
using DotCharter.Tone.Audio.Dsp.Tones;

namespace DotCharter.Services.Ai.Generate;

/// <summary>One tone definition the AI chat's Tones task generates a body for.</summary>
/// <param name="Key">The definition's key: what the placed tone changes name.</param>
/// <param name="Name">The definition's display name, else the key.</param>
/// <param name="Window">The window it is generated from: the LONGEST of its in-scope changes' windows
/// (more audio to measure), the earliest on a tie.</param>
/// <param name="CurrentJson">Its body now, or null when the project has no definition under the key.</param>
public sealed record AiToneTarget(string Key, string Name, ToneWindow Window, string? CurrentJson);

/// <summary>What the Tones task will generate: one target per tone definition, and the in-scope
/// changes it cannot generate for (no tone name), with why.</summary>
public sealed record AiTonePlan(IReadOnlyList<AiToneTarget> Targets, IReadOnlyList<AiChartRejection> Refused);

/// <summary>
/// The AI chat's Tones task over the W6 generator: which tone definitions the scope's
///     placed tone changes name, the request the generator runs (the chat's instruction in every
///     window's prompt), and the proposals HELD from its answer. Pure apart from the generator call,
///     which <see cref="AiChartPassRun" /> makes off the UI thread.
///     <para>
///         <b>The unit is a tone DEFINITION, not a placed change.</b> A song that switches to "Clean"
///         three times has one "Clean" body, so one proposal per key — and the write is that body
///         alone: no change is renamed, no slot reassigned (the menu verb's
///         <see cref="RsToneProjectWriter" /> does both; this task never calls it).
///     </para>
///     <para>
///         <b>The gate is the generator's.</b> A reply naming gear the game does not have, or a knob
///         outside its range, is refused by <see cref="RsToneReplyReader" /> inside the mapper and comes
///         back as a failed window with its reason; the re-keyed tone is validated again here before it
///         is held. Rejected, never repaired.
///     </para>
/// </summary>
public static class AiTonePass
{
    /// <summary>A placed tone change in scope names no tone.</summary>
    public const string UnnamedCode = "unnamed-tone";

    /// <summary>The generator produced no tone for the window (its reason says why).</summary>
    public const string NotGeneratedCode = "not-generated";

    /// <summary>
    ///     The targets for <paramref name="changes" /> (placed changes of <paramref name="arrangement" />).
    ///     Windows are planned over ALL of the arrangement's changes, so a window is clamped by its real
    ///     neighbours whatever the scope.
    /// </summary>
    /// <param name="project">The live project (its beat map and tone definitions).</param>
    /// <param name="arrangement">The arrangement.</param>
    /// <param name="changes">The scope's tone changes.</param>
    /// <param name="endMs">Where the last window stops: the song's length.</param>
    /// <param name="loc">The refusal's words.</param>
    public static AiTonePlan Plan(ChartProject project, Arrangement arrangement, IReadOnlyList<ToneChange> changes,
        double endMs, ILocalizationService loc)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(arrangement);
        ArgumentNullException.ThrowIfNull(changes);
        ArgumentNullException.ThrowIfNull(loc);

        var windows = ToneWindowPlanner.Plan(arrangement.ToneChanges, project.Beats, endMs);
        // The planner's own order (time, then list position): window i belongs to ordered[i].
        var ordered = arrangement.ToneChanges
            .Select((change, i) => (Change: change, Ms: change.GetTimeMs(project.Beats), Original: i))
            .OrderBy(x => x.Ms)
            .ThenBy(x => x.Original)
            .Select(x => x.Change)
            .ToList();

        var refused = new List<AiChartRejection>();
        var byKey = new Dictionary<string, ToneWindow>(StringComparer.OrdinalIgnoreCase);
        var keys = new List<string>();
        for (var i = 0; i < ordered.Count; i++)
        {
            if (!changes.Any(c => ReferenceEquals(c, ordered[i]))) continue;
            var window = windows[i];
            var key = (ordered[i].ToneName ?? "").Trim();
            if (key.Length == 0)
            {
                refused.Add(new AiChartRejection(i, UnnamedCode, loc.Format("AiTones_Unnamed", Clock(window.PlacedMs))));
                continue;
            }

            if (!byKey.TryGetValue(key, out var best))
            {
                byKey[key] = window;
                keys.Add(key);
            }
            else if (window.DurationMs > best.DurationMs)
            {
                byKey[key] = window;
            }
        }

        var targets = keys.Select(key =>
            {
                var definition = AiToneApplier.Find(project, key);
                var name = definition is { Name.Length: > 0 } ? definition.Name : definition?.Key ?? key;
                return new AiToneTarget(definition?.Key ?? key, name, byKey[key], definition?.Json);
            })
            .OrderBy(t => t.Window.PlacedMs)
            .ToList();
        return new AiTonePlan(targets, refused);
    }

    /// <summary>The generator's request: one window per target, the project's own context
    /// (<see cref="AiToneGenerationRun.ContextFor" />), and the chat's instruction with the conversation
    /// before it as the instruction section of every window's prompt.</summary>
    public static ToneGenerateRequest Request(ChartProject project, Arrangement arrangement, AiTonePlan plan,
        string songAudioPath, AiChartInstruction? instruction)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(arrangement);
        ArgumentNullException.ThrowIfNull(plan);
        return new ToneGenerateRequest(
            songAudioPath,
            string.IsNullOrWhiteSpace(arrangement.Name) ? "tone" : arrangement.Name,
            AiToneGenerationRun.StemFor(arrangement, project),
            [.. plan.Targets.Select(t => t.Window)],
            AiToneGenerationRun.ContextFor(project, arrangement),
            project.Chart.SongName,
            project.Chart.Artist,
            [],
            Instruction: InstructionText(instruction));
    }

    /// <summary>The instruction section's body: the instruction, then the conversation excerpt under the
    /// chart pass's own header (<see cref="AiChartPrompt.ConversationHeader" />). Null for none.</summary>
    internal static string? InstructionText(AiChartInstruction? instruction)
    {
        if (instruction is null || string.IsNullOrWhiteSpace(instruction.Text)) return null;
        if (string.IsNullOrWhiteSpace(instruction.Conversation)) return instruction.Text.Trim();
        var sb = new StringBuilder(instruction.Text.Trim()).Append('\n');
        sb.Append(AiChartPrompt.ConversationHeader).Append('\n');
        sb.Append(instruction.Conversation.Trim()).Append('\n');
        sb.Append("--- end of conversation ---");
        return sb.ToString();
    }

    /// <summary>
    ///     The proposals from the generator's <paramref name="result" /> (window i = target i). A failed
    ///     window is refused with the generator's reason; a tone is re-keyed to its definition and
    ///     validated against the catalogue again (a definition key the game cannot load refuses it).
    ///     Nothing is written.
    /// </summary>
    public static (IReadOnlyList<AiToneReviewItem> Held, IReadOnlyList<AiChartRejection> Refused) Hold(
        AiTonePlan plan, ToneSetGenerationResult result, ILocalizationService loc)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(loc);
        var held = new List<AiToneReviewItem>();
        var refused = new List<AiChartRejection>();
        for (var i = 0; i < plan.Targets.Count; i++)
        {
            var target = plan.Targets[i];
            var generated = i < result.Tones.Count ? result.Tones[i] : null;
            if (generated?.Tone is not { } tone)
            {
                refused.Add(new AiChartRejection(i, NotGeneratedCode,
                    loc.Format("AiTones_NotGenerated", target.Name, generated?.Error ?? "")));
                continue;
            }

            // The generator keys a tone after its window's name; this task writes the DEFINITION the
            // changes already name, so the tone takes that key and name.
            var keyed = tone with { Key = target.Key, Name = target.Name };
            if (RsToneCatalogValidation.FirstFault(keyed, RsGearCatalog.Shared) is { } fault)
            {
                refused.Add(new AiChartRejection(i, fault.Code, loc.Format("AiTones_NotGenerated", target.Name, fault.Message)));
                continue;
            }

            RsTone? current = null;
            if (target.CurrentJson is { } json && RsToneJsonParser.TryParse(json, out var parsed)) current = parsed;
            held.Add(new AiToneReviewItem(target.Key, target.Name, target.Window, keyed, target.CurrentJson, current,
                generated.Provenance));
        }

        // The gate's refusals first (they are about what the model said), then the changes never asked about.
        refused.AddRange(plan.Refused);
        return (held, refused);
    }

    private static string Clock(double ms)
    {
        var time = TimeSpan.FromMilliseconds(Math.Max(0, ms));
        return string.Create(CultureInfo.InvariantCulture, $"{(int)time.TotalMinutes}:{time.Seconds:00}");
    }
}
