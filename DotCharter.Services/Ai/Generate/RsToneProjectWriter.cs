using DotCharter.Tone.Audio.Dsp.Tones;

namespace DotCharter.Services.Ai.Generate;

/// <summary>What writing a generated set into a project changed.</summary>
/// <param name="Keys">The tone keys written, in window order.</param>
/// <param name="ToneChangesPointed">How many placed tone changes now name one.</param>
/// <param name="BaseTone">The key that became the arrangement's
/// <see cref="Arrangement.ToneBase" />, or null when nothing was written.</param>
public sealed record RsToneWriteResult(IReadOnlyList<string> Keys, int ToneChangesPointed, string? BaseTone);

/// <summary>
///     Puts a generated set into the project the app already has: one
///     <see cref="ToneDefinition" /> per tone, the placed
///     <see cref="Core.Domain.Events.ToneChange" />s pointed at them, and the
///     arrangement's Rocksmith tone SLOTS filled.
///     <para>
///         <b>There is no second writer here, and there must not be.</b> The tone
///         body is serialized by <see cref="RsToneJsonWriter" /> — the one thing in
///         this app that turns an <see cref="RsTone" /> into the manifest JSON the
///         package export embeds verbatim. Everything downstream (the `.dcproj`, the
///         `.psarc` manifest, the RS-XML tone slots, and the `.rs2dlc`'s
///         <c>BaseTone</c>) reads what is written here.
///     </para>
///     <para>
///         <b>Every tone is validated AGAIN at this boundary.</b> The reply reader
///         already refused an out-of-catalogue tone, but a caller can hand this
///         method an <see cref="RsTone" /> from anywhere, and "validated before it is
///         written" has to be true of the WRITE rather than of one path to it.
///         <c>RsToneWriterLintTests</c> is the mechanical half.
///     </para>
///     <para>
///         <b>What it deliberately does NOT touch: the `.rs2dlc`'s <c>Tones</c>
///         arrays.</b> Those are hard-coded empty by
///         <c>Rs2DlcProjectBuilder</c> because DLC Builder's own project file is
///         somebody else's and a save that filled them blanked hand-assigned tones
///         (`CompanionOwnership`). Setting
///         <see cref="Arrangement.ToneBase" /> is the ONE tone field that builder
///         already forwards, so a generated set reaches the `.rs2dlc` without the
///         format or the ownership rule changing.
///     </para>
/// </summary>
public static class RsToneProjectWriter
{
    /// <summary>
    ///     Writes every generated tone of <paramref name="tones" /> into
    ///     <paramref name="project" /> and points <paramref name="arrangement" />'s
    ///     tone changes at them. Failed entries are skipped (their change keeps
    ///     whatever name it had).
    /// </summary>
    /// <param name="project">The project that owns the tone definitions.</param>
    /// <param name="arrangement">The arrangement whose changes were generated.</param>
    /// <param name="tones">The generated set, in window order.</param>
    /// <param name="catalog">The gear catalogue to validate against; null for the
    /// shared one.</param>
    /// <exception cref="RsToneCatalogException">A tone in the set is not something
    /// Rocksmith can load. Nothing is written.</exception>
    public static RsToneWriteResult Write(
        ChartProject project,
        Arrangement arrangement,
        IReadOnlyList<GeneratedTone> tones,
        RsGearCatalog? catalog = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(arrangement);
        ArgumentNullException.ThrowIfNull(tones);
        var gear = catalog ?? RsGearCatalog.Shared;

        // Validate the WHOLE set first: a half-written arrangement whose fifth tone
        // was rejected is worse than one that was not touched.
        var written = new List<(ToneWindow Window, RsTone Tone)>(tones.Count);
        foreach (var generated in tones)
        {
            if (generated.Tone is null) continue;
            RsToneCatalogValidation.Require(generated.Tone, gear, $"the generated tone '{generated.Tone.Key}'");
            written.Add((generated.Window, generated.Tone));
        }

        if (written.Count == 0) return new RsToneWriteResult([], 0, null);

        foreach (var (_, tone) in written)
        {
            var definition = new ToneDefinition
            {
                Key = tone.Key,
                Name = string.IsNullOrWhiteSpace(tone.Name) ? tone.Key : tone.Name,
                Json = RsToneJsonWriter.Write(tone)
            };

            var existing = project.ToneDefinitions
                .FirstOrDefault(d => string.Equals(d.Key, tone.Key, StringComparison.OrdinalIgnoreCase));
            if (existing is null) project.ToneDefinitions.Add(definition);
            else existing.CopyFrom(definition);
        }

        // Point the placed changes at their tones. The window carries the INDEX into
        // the sorted change list, which is what the planner sorted them by — so a
        // change is matched by position in that order rather than by its old name,
        // which may be empty or may be another tone's.
        var ordered = arrangement.ToneChanges
            .Select((change, i) => (Change: change, Ms: change.GetTimeMs(project.Beats), Original: i))
            .OrderBy(x => x.Ms)
            .ThenBy(x => x.Original)
            .ToList();

        var pointed = 0;
        foreach (var (window, tone) in written)
        {
            if (window.Index < 0 || window.Index >= ordered.Count) continue;
            ordered[window.Index].Change.ToneName = tone.Key;
            pointed++;
        }

        // The arrangement's Rocksmith tone SLOTS. Rocksmith has one base plus four
        // switchable slots; a set longer than five keeps every DEFINITION (the
        // package embeds them all) but only the first five can be slotted, which is
        // the game's limit and not this app's.
        var keys = written.Select(w => w.Tone.Key).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        arrangement.ToneBase = keys[0];
        arrangement.ToneA = keys.Count > 1 ? keys[1] : "";
        arrangement.ToneB = keys.Count > 2 ? keys[2] : "";
        arrangement.ToneC = keys.Count > 3 ? keys[3] : "";
        arrangement.ToneD = keys.Count > 4 ? keys[4] : "";

        arrangement.ToneNames.Clear();
        foreach (var key in keys) arrangement.ToneNames.Add(key);

        return new RsToneWriteResult(keys, pointed, arrangement.ToneBase);
    }
}
