using DotCharter.Localization;
using DotCharter.Services.Audio.Dsp.Tones;
using DotCharter.Services.State;
using DotCharter.Tone.Audio.Dsp.Tones;

namespace DotCharter.Services.Editing.Tones;

/// <summary>What one "generate tones from audio" run did, and the sentence that says so.</summary>
public sealed record ToneGenerationOutcome(
    IReadOnlyList<ToneDefinition> Generated, int Skipped, int Silent, int NoiseLike, int TooShort, string LastRationale)
{
    public static ToneGenerationOutcome NoChanges { get; } = new([], 0, 0, 0, 0, "");

    /// <summary>A count alone does not explain why a span produced nothing, so each reason gets its own clause.</summary>
    public string Describe(ILocalizationService loc)
    {
        if (Generated.Count == 0 && Skipped == 0 && Silent == 0 && NoiseLike == 0 && TooShort == 0)
            return loc["Dialog_Tones_GenerateNoChanges"];

        var parts = new List<string> { loc.Plural("Dialog_Tones_GenerateDoneOne", "Dialog_Tones_GenerateDone", Generated.Count) };
        if (Skipped > 0) parts.Add(loc.Plural("Dialog_Tones_GenerateSkippedOne", "Dialog_Tones_GenerateSkipped", Skipped));
        if (Silent > 0) parts.Add(loc.Plural("Dialog_Tones_GenerateSilentOne", "Dialog_Tones_GenerateSilent", Silent));
        if (NoiseLike > 0) parts.Add(loc.Plural("Dialog_Tones_GenerateNoisyOne", "Dialog_Tones_GenerateNoisy", NoiseLike));
        if (TooShort > 0) parts.Add(loc.Plural("Dialog_Tones_GenerateTooShortOne", "Dialog_Tones_GenerateTooShort", TooShort));
        if (Generated.Count > 0 && LastRationale.Length > 0) parts.Add(LastRationale);
        return string.Join(" ", parts);
    }
}

/// <summary>What a run reads from the project, captured on the UI thread so the slow part can run anywhere.</summary>
public sealed record ToneGenerationInput(
    IReadOnlyList<ToneChange> Changes,
    IList<BeatMarker> Beats,
    double SongLengthMs,
    IReadOnlyList<(string Name, string Path)> StemCandidates,
    string? SongCandidate,
    IReadOnlyCollection<string> ExistingNames,
    IReadOnlyCollection<string> ExistingKeys);

/// <summary>One change's analysis: the key and name it would be written under, and what the audio said.</summary>
public sealed record ToneGenerationCandidate(string Key, string Name, ToneGenerationResult Result);

/// <summary>Everything the analysis produced; <see cref="ToneGenerationRun.Apply" /> turns it into undoable edits.</summary>
public sealed record ToneGenerationPlan(IReadOnlyList<ToneGenerationCandidate> Candidates, int SkippedExisting, int ChangeCount);

/// <summary>
///     Builds a tone for every tone change the charter has PLACED on one arrangement,
///     from the audio each one governs. It reads placements and never writes them;
///     a tone name the project already has is a decision and is skipped, never
///     overwritten; each generated tone is declared in a free A-D slot, because a
///     tone the arrangement does not declare is one the game cannot switch to (D01).
///     Every body is one undoable upsert, and the slot writes fold into ONE undoable
///     assignment at the end.
///     <para>
/// Three phases: <see cref="Capture" /> reads the project on the UI
///         thread, <see cref="Analyse" /> decodes and analyses on any thread (the Tone
///         designer runs it in <c>Task.Run</c>: inline it would freeze the
///         window for one full decode PER tone change), and <see cref="Apply" /> writes
///         the edits back on the UI thread. The audio is decoded once per run
///         (<see cref="ToneGenerationService" /> caches it per path).
///     </para>
/// </summary>
public sealed class ToneGenerationRun(IHistoryService history, IExportTargetWriter files, IAppLogger logger)
{
    /// <summary>Generates and applies in one go, on the calling thread.</summary>
    public ToneGenerationOutcome Run(ChartProject project, Arrangement arrangement, string? projectPath) =>
        arrangement.ToneChanges.Count == 0
            ? ToneGenerationOutcome.NoChanges
            : Apply(project, arrangement, Analyse(Capture(project, arrangement, projectPath)));

    /// <summary>The project state a run reads. UI thread; touches no file.</summary>
    public static ToneGenerationInput Capture(ChartProject project, Arrangement arrangement, string? projectPath)
    {
        var projectDir = string.IsNullOrWhiteSpace(projectPath) ? "" : Path.GetDirectoryName(projectPath) ?? "";
        var beats = project.Beats.ToList();
        var stems = new List<(string Name, string Path)>();
        foreach (var stem in project.Stems)
        {
            if (string.IsNullOrWhiteSpace(stem.Name) || string.IsNullOrWhiteSpace(stem.Path)) continue;
            if (!ToneAudioSource.IsGuitarish(stem.Name)) continue;
            stems.Add((stem.Name, Path.IsPathRooted(stem.Path) || string.IsNullOrEmpty(projectDir)
                ? stem.Path
                : Path.Combine(projectDir, stem.Path)));
        }

        string? song = null;
        var name = project.AudioFileName;
        if (!string.IsNullOrWhiteSpace(name))
        {
            if (Path.IsPathRooted(name)) song = name;
            else if (!string.IsNullOrEmpty(projectDir)) song = Path.Combine(projectDir, name);
        }

        return new ToneGenerationInput(
            arrangement.ToneChanges.ToList(), beats, beats.Count > 0 ? beats[^1].Time : 0, stems, song,
            project.ToneDefinitions.Select(t => t.Name).ToList(),
            project.ToneDefinitions.Select(t => t.Key).ToList());
    }

    /// <summary>Decodes and analyses. Any thread: reads only <paramref name="input" /> and the disk.</summary>
    public ToneGenerationPlan Analyse(ToneGenerationInput input, ToneGenerationService? service = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        service ??= new ToneGenerationService(logger);
        var stemPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (stemName, path) in input.StemCandidates)
        {
            if (files.FileExists(path)) stemPaths[stemName] = path;
        }

        var songPath = input.SongCandidate is { } song && files.FileExists(song) ? song : null;
        var names = new HashSet<string>(input.ExistingNames, StringComparer.OrdinalIgnoreCase);
        var keys = new List<string>(input.ExistingKeys);
        var candidates = new List<ToneGenerationCandidate>();
        var skipped = 0;

        foreach (var change in input.Changes)
        {
            var wanted = (change.ToneName ?? "").Trim();
            if (wanted.Length == 0) continue;
            if (!names.Add(wanted))
            {
                skipped++;
                continue;
            }

            var key = RsToneAuthoring.MakeUniqueKey(RsToneAuthoring.SanitizeKey(wanted), keys);
            var result = service.Generate(change, input.Changes, input.Beats, input.SongLengthMs, stemPaths, songPath, key, wanted);
            if (result.Status == ToneGenerationStatus.Generated && result.ToneJson is { Length: > 0 }) keys.Add(key);
            else names.Remove(wanted);
            candidates.Add(new ToneGenerationCandidate(key, wanted, result));
        }

        return new ToneGenerationPlan(candidates, skipped, input.Changes.Count);
    }

    /// <summary>
    ///     Writes the plan as undoable edits. UI thread. A name or key the project gained
    ///     while the analysis ran is skipped, never overwritten.
    /// </summary>
    public ToneGenerationOutcome Apply(ChartProject project, Arrangement arrangement, ToneGenerationPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var slotsBefore = ToneSlotAssignment.Capture(arrangement);
        var generated = new List<ToneDefinition>();
        int skipped = plan.SkippedExisting, silent = 0, noisy = 0, tooShort = 0;
        var lastRationale = "";

        foreach (var candidate in plan.Candidates)
        {
            var result = candidate.Result;
            switch (result.Status)
            {
                case ToneGenerationStatus.Generated when result.ToneJson is { Length: > 0 }:
                    if (project.ToneDefinitions.Any(t =>
                            string.Equals(t.Name, candidate.Name, StringComparison.OrdinalIgnoreCase)
                            || string.Equals(t.Key, candidate.Key, StringComparison.OrdinalIgnoreCase)))
                    {
                        skipped++;
                        break;
                    }

                    var definition = new ToneDefinition { Key = candidate.Key, Name = candidate.Name, Json = result.ToneJson };
                    history.Do(new UpsertToneDefinitionAction(project, null, definition));
                    generated.Add(definition);
                    DeclareInAFreeSlot(arrangement, candidate.Name);
                    lastRationale = result.Suggestion?.Rationale ?? "";
                    break;
                case ToneGenerationStatus.Silent: silent++; break;
                case ToneGenerationStatus.NoiseLike: noisy++; break;
                case ToneGenerationStatus.SpanTooShort: tooShort++; break;
                default: skipped++; break;
            }
        }

        if (BuildSlotAssignmentAction(arrangement, slotsBefore) is { } slotAction) history.Do(slotAction);
        logger.Info(AppLogCategory.Project,
            $"Tone generation over {plan.ChangeCount} change(s): {generated.Count} generated, {skipped} skipped, " +
            $"{silent} silent, {noisy} noise-like, {tooShort} too short.");
        return new ToneGenerationOutcome(generated, skipped, silent, noisy, tooShort, lastRationale);
    }

    /// <summary>
    ///     Folds the run's direct slot writes into ONE undoable node and re-derives
    ///     <c>ToneNames</c>, keeping <c>StartingTone</c>: without it, undoing a generated
    ///     tone removed its body and left the slot declared, and the project never went
    ///     dirty for the slot. Restores <paramref name="before" /> first, so the action's
    ///     own <c>Execute</c> applies the result; null when nothing moved.
    /// </summary>
    public static IChartAction? BuildSlotAssignmentAction(Arrangement arrangement, ToneSlotAssignment before)
    {
        var after = ToneSlotAssignment
            .FromSlots(arrangement.ToneBase, arrangement.ToneA, arrangement.ToneB, arrangement.ToneC, arrangement.ToneD)
            with
        { StartingTone = before.StartingTone };
        if (before.SameAs(after)) return null;
        before.ApplyTo(arrangement);
        return new AssignArrangementTonesAction(arrangement, before, after);
    }

    /// <summary>The first free A–D slot; occupied slots are left alone, and past four the tone still exists in the project.</summary>
    public static void DeclareInAFreeSlot(Arrangement arrangement, string toneName)
    {
        if (ChartToneNames.IsDeclared(arrangement, toneName)) return;
        if (string.IsNullOrWhiteSpace(arrangement.ToneA)) arrangement.ToneA = toneName;
        else if (string.IsNullOrWhiteSpace(arrangement.ToneB)) arrangement.ToneB = toneName;
        else if (string.IsNullOrWhiteSpace(arrangement.ToneC)) arrangement.ToneC = toneName;
        else if (string.IsNullOrWhiteSpace(arrangement.ToneD)) arrangement.ToneD = toneName;
    }
}
