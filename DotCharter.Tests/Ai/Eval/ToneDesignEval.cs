using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DotCharter.Tone.Ai;

namespace DotCharter.Tests.Ai.Eval;

/// <summary>
///     The eval runner: takes an <see cref="IToneDesignService" />, asks it for a
///     tone per corpus entry, grades each answer with <see cref="ToneDesignScorer" />
///     and writes <c>artifacts/ai-eval/&lt;timestamp&gt;/{results.json,report.md}</c>.
///     <para>
///         It knows nothing about providers, keys or HTTP — it takes the interface.
///         That is what lets the SAME runner be driven by a deterministic fake in
///         the default gate and by the real Claude-backed service in the owner's
///         live run, and it is the only way the report writer gets tested at all.
///     </para>
/// </summary>
public static class ToneDesignEval
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>
    ///     Runs the eval. Never throws for a per-row failure: a call that faults is
    ///     recorded as a row with <see cref="ToneEvalRow.Error" /> set and a total
    ///     of 0, because "the model refused on six songs" is a result, not a crash.
    ///     <see cref="OperationCanceledException" /> propagates.
    /// </summary>
    public static async Task<ToneEvalRun> RunAsync(
        IToneDesignService service,
        IReadOnlyList<ToneEvalEntry> entries,
        ToneEvalOptions options,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(options);

        var selected = entries.Take(Math.Max(0, options.MaxSongs)).ToList();
        var rows = new List<ToneEvalRow>(options.CallCount(selected.Count));

        foreach (var entry in selected)
        {
            rows.Add(await GradeAsync(service, entry, ToneEvalPromptKind.Full, options, ct)
                .ConfigureAwait(false));

            if (options.IncludeShortPrompt)
            {
                rows.Add(await GradeAsync(service, entry, ToneEvalPromptKind.Short, options, ct)
                    .ConfigureAwait(false));
            }
        }

        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var directory = Path.Combine(ResolveArtifactRoot(options), "artifacts", "ai-eval", timestamp);
        var run = Aggregate(timestamp, directory, selected.Count, rows, options);

        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(
            Path.Combine(directory, "results.json"),
            JsonSerializer.Serialize(run, Json), ct).ConfigureAwait(false);
        await File.WriteAllTextAsync(
            Path.Combine(directory, "report.md"), WriteReport(run, options), ct).ConfigureAwait(false);

        return run;
    }

    private static async Task<ToneEvalRow> GradeAsync(
        IToneDesignService service,
        ToneEvalEntry entry,
        ToneEvalPromptKind kind,
        ToneEvalOptions options,
        CancellationToken ct)
    {
        var prompt = kind == ToneEvalPromptKind.Full ? entry.Prompt : entry.ShortPrompt;

        // Pure, no provider, no network: what a PERFECT answer could have scored
        // once the draft schema and the validator have had their say.
        var ceiling = ToneEvalCeiling.TotalOf(entry.Truth);

        var request = new ToneDesignRequest(
            prompt, Array.Empty<ToneDesignTurn>(), null, entry.SongTitle, entry.Artist);

        var clock = Stopwatch.StartNew();
        try
        {
            var result = await service.DesignAsync(request, null, ct).ConfigureAwait(false);
            clock.Stop();

            var score = ToneDesignScorer.Score(result.Tone, entry.Truth);
            return new ToneEvalRow(
                entry.Id, entry.SongTitle, entry.Artist, entry.ToneName, kind, prompt,
                score.Total, ceiling, score.FamilyScore, score.ModuleScore, score.KnobScore,
                score.OrderScore, score.ComparedParams,
                score.DesignedModules, score.TruthModules,
                result.Confidence, result.Warnings,
                result.Usage.InputTokens, result.Usage.OutputTokens,
                Cost(result.Usage, options), clock.Elapsed.TotalMilliseconds, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            clock.Stop();
            var detail = ex is ToneDesignException fault
                ? $"{fault.Fault}: {fault.Message}"
                : $"{ex.GetType().Name}: {ex.Message}";

            return new ToneEvalRow(
                entry.Id, entry.SongTitle, entry.Artist, entry.ToneName, kind, prompt,
                0, ceiling, 0, 0, 0, 0, 0,
                Array.Empty<string>(),
                ToneDesignScorer.Canonicalize(entry.Truth).Modules
                    .Select(m => m.ModuleTypeId).ToList(),
                0, Array.Empty<string>(), 0, 0, 0,
                clock.Elapsed.TotalMilliseconds, detail);
        }
    }

    private static double Cost(AiUsage usage, ToneEvalOptions options)
        => usage.InputTokens / 1e6 * options.InputUsdPerMTok +
           usage.OutputTokens / 1e6 * options.OutputUsdPerMTok;

    private static ToneEvalRun Aggregate(
        string timestamp, string directory, int songs,
        IReadOnlyList<ToneEvalRow> rows, ToneEvalOptions options)
    {
        var ok = rows.Where(r => r.Error is null).ToList();
        var totals = ok.Select(r => r.Total).OrderBy(v => v).ToList();

        return new ToneEvalRun(
            timestamp, options.Label, options.Provider, songs,
            Mean(totals), Mean(rows.Select(r => r.Ceiling)),
            Percentile(totals, 0.50), Percentile(totals, 0.10),
            Mean(ok.Select(r => r.Family)), Mean(ok.Select(r => r.Module)),
            Mean(ok.Select(r => r.Knob)), Mean(ok.Select(r => r.Order)),
            rows.Count - ok.Count,
            rows.Sum(r => r.InputTokens), rows.Sum(r => r.OutputTokens),
            rows.Sum(r => r.CostUsd), rows, directory, options.Anchored);
    }

    private static double Mean(IEnumerable<double> values)
    {
        var list = values as IList<double> ?? values.ToList();
        return list.Count == 0 ? 0 : list.Average();
    }

    /// <summary>Linear-interpolated percentile over an ASCENDING list.</summary>
    private static double Percentile(IReadOnlyList<double> sorted, double q)
    {
        if (sorted.Count == 0) return 0;
        if (sorted.Count == 1) return sorted[0];

        var position = (sorted.Count - 1) * q;
        var low = (int)Math.Floor(position);
        var high = (int)Math.Ceiling(position);
        return low == high ? sorted[low] : sorted[low] + (position - low) * (sorted[high] - sorted[low]);
    }

    /// <summary>The repository root (nearest ancestor holding <c>DotCharter.sln</c>),
    /// or the test binaries' directory when there is none — an artifact must always
    /// land somewhere, even from a published test bundle.</summary>
    public static string ResolveArtifactRoot(ToneEvalOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.ArtifactRoot)) return options.ArtifactRoot;

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "DotCharter.sln"))) return dir.FullName;
            dir = dir.Parent;
        }

        return AppContext.BaseDirectory;
    }

    // ------------------------------------------------------------------- report

    /// <summary>Renders the human-readable half of the artifact.</summary>
    public static string WriteReport(ToneEvalRun run, ToneEvalOptions options)
    {
        var text = new StringBuilder();
        var ci = CultureInfo.InvariantCulture;

        text.AppendLine(ci, $"# AI tone design eval — {run.TimestampUtc} UTC");
        text.AppendLine();
        text.AppendLine(ci, $"- **Subject**: {run.Label} (provider `{run.Provider}`)");
        // The MODE comes before the number, deliberately: an anchored number read
        // as a score is the one way this report can mislead.
        text.AppendLine(run.Anchored
            ? "- **Mode**: **ANCHORED** — grounding stage 2 was left ON, so the model was "
              + "handed the song's own converted Rocksmith tone, which is the very chain "
              + "the scorer grades against. Read the number below as an **upper bound, not "
              + "a generalisation score**: it measures how well the pipeline can copy a "
              + "reference it was given, not how well it designs for a song the library "
              + "does not have."
            : "- **Mode**: unanchored — grounding stage 2 was OFF for the scored song "
              + "(`UseLibraryTones = false`), so the model never saw the ground truth. This "
              + "is the number to quote.");
        text.AppendLine(ci, $"- **Songs**: {run.Songs}   **Requests**: {run.Results.Count}   " +
                            $"**Failed**: {run.Failures}");
        text.AppendLine(ci, $"- **Score**: mean **{run.Mean:F1}** · median {run.Median:F1} · " +
                            $"p10 {run.P10:F1} (0..100)");
        text.AppendLine(ci, $"- **Ceiling**: mean {run.MeanCeiling:F1} — the best a PERFECT " +
                            $"model could score here, after the draft schema and the validator. " +
                            $"Read every score against this, not against 100.");
        text.AppendLine(ci, $"- **Components** (0..1): family {run.MeanFamily:F2} · " +
                            $"module {run.MeanModule:F2} · knob {run.MeanKnob:F2} · " +
                            $"order {run.MeanOrder:F2}");
        text.AppendLine(ci, $"- **Weights**: family {ToneScoreWeights.Family:F2} · " +
                            $"module {ToneScoreWeights.Module:F2} · knob {ToneScoreWeights.Knob:F2} · " +
                            $"order {ToneScoreWeights.Order:F2}");
        text.AppendLine(ci, $"- **Tokens**: {run.InputTokens:N0} in / {run.OutputTokens:N0} out   " +
                            $"**Cost**: ${run.CostUsd:F4} at " +
                            $"${options.InputUsdPerMTok:F2}/${options.OutputUsdPerMTok:F2} per MTok");
        text.AppendLine();
        text.AppendLine("The score compares the designed CHAIN against the chain DotCharter's own");
        text.AppendLine("`RsToneConverter` builds from the song's real Rocksmith tone. It is not a");
        text.AppendLine("measurement of how the tone sounds — see `tools/ai-eval/README.md`.");
        text.AppendLine();

        text.AppendLine("| Song | Tone | Prompt | Total | Ceil | Fam | Mod | Knob | Ord | In | Out | $ |");
        text.AppendLine("|---|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var row in run.Results)
        {
            text.AppendLine(ci,
                $"| {Cell(row.Artist)} — {Cell(row.SongTitle)} | {Cell(row.ToneName)} | " +
                $"{(row.PromptKind == ToneEvalPromptKind.Full ? "full" : "short")} | " +
                $"{(row.Error is null ? row.Total.ToString("F1", ci) : "—")} | " +
                $"{row.Ceiling:F1} | " +
                $"{row.Family:F2} | {row.Module:F2} | {row.Knob:F2} | {row.Order:F2} | " +
                $"{row.InputTokens} | {row.OutputTokens} | {row.CostUsd:F4} |");
        }

        text.AppendLine();
        text.AppendLine("## Chains, side by side");
        text.AppendLine();
        foreach (var row in run.Results)
        {
            text.AppendLine(ci, $"### {row.Artist} — {row.SongTitle} · {row.ToneName} " +
                                $"({(row.PromptKind == ToneEvalPromptKind.Full ? "full" : "short")})");
            text.AppendLine(ci, $"> {row.Prompt}");
            text.AppendLine();
            if (row.Error is not null)
            {
                text.AppendLine(ci, $"**FAILED** — {row.Error}");
                text.AppendLine();
                continue;
            }

            text.AppendLine(ci, $"- truth:    `{Chain(row.TruthModules)}`");
            text.AppendLine(ci, $"- designed: `{Chain(row.DesignedModules)}`");
            text.AppendLine(ci, $"- score {row.Total:F1} of a reachable {row.Ceiling:F1} · " +
                                $"confidence {row.Confidence:F2} · " +
                                $"{row.ComparedParams} params compared · {row.ElapsedMs:F0} ms");
            if (row.Warnings.Count > 0)
                text.AppendLine(ci, $"- warnings: {string.Join("; ", row.Warnings.Select(Cell))}");
            text.AppendLine();
        }

        return text.ToString();
    }

    private static string Chain(IReadOnlyList<string> modules)
        => modules.Count == 0 ? "(empty)" : string.Join(" → ", modules);

    /// <summary>A pipe in a song title would tear the markdown table in half.</summary>
    private static string Cell(string value) => value.Replace("|", "\\|", StringComparison.Ordinal);
}
