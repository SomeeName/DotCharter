using System.Diagnostics;
using System.Globalization;
using System.Text;
using DotCharter.Services.Ai.Match;
using DotCharter.Services.Ai.Match.Model;
using DotCharter.Services.Ai.Match.Similarity;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;
using JuceSharp.AudioIo;

namespace DotCharter.Tools.RigDataset;

/// <summary>
///     Measures, once per (module, param), how far a FULL-TRAVEL move of that knob
///     shifts the tone metric — the number the python trainer turns into a per-knob
///     weight for its knob loss.
///     <para>
///         <b>The method is an A/B render, not an opinion.</b> For every param in
///         <see cref="RigLabelVocabulary" />: build a minimal rig holding that module
///         with every param at its catalogue DEFAULT, render one fixed reference DI
///         through it with the param under test normalised to 0.1, render it again at
///         0.9, and take the <see cref="ToneSimilarity" /> distance between the two
///         renders. Nothing else moves, so the number is that knob's authority over
///         the tone and nothing else's.
///     </para>
///     <para>
///         <b>Context is decided per module by a PROBE, not by a hand-written list.</b>
///         A module is first rendered solo at its defaults and compared against the DI
///         through an empty chain; when the two are indistinguishable the module is
///         inaudible on its own (a <c>micpos</c> with no cabinet in front of it, a
///         gate with nothing to gate) and the whole module is re-measured inside a
///         default <c>amp → cab → module</c> context instead. The context each module
///         was measured in is recorded in the JSON, because a distance measured behind
///         an amp is not on the same scale as a solo one.
///     </para>
///     <para>
///         <b>Level knobs read LOW on purpose.</b> <see cref="RigRenderer" /> level-
///         matches its output (<see cref="LevelMatch" />) and the metric is
///         content-invariant, so a pure output-level param genuinely does not move the
///         tone distance. That is the metric's position, not a measurement error — and
///         it is the right signal for a loss that is trained against this same metric.
///     </para>
///     <para>
///         <b>One engine per process and no concurrency</b> — the same rule
///         <see cref="RenderCommand" /> documents. This is a single sequential loop.
///     </para>
/// </summary>
public sealed class KnobSensitivityCommand
{
    /// <summary>Schema version of the emitted JSON. Bump on any layout change.</summary>
    public const int SchemaVersion = 1;

    /// <summary>Normalised value of the "low" render.</summary>
    public const float LowUnit = 0.1f;

    /// <summary>Normalised value of the "high" render.</summary>
    public const float HighUnit = 0.9f;

    /// <summary>Seconds of DI rendered per A/B leg.</summary>
    public const float Seconds = 5f;

    /// <summary>Distance below which two renders count as the same audio — used only
    /// by the audibility probe that picks a module's context.</summary>
    private const double InaudibleEpsilon = 1e-6;

    private readonly KnobSensitivityOptions _o;

    /// <summary>Builds the command over its parsed flags.</summary>
    /// <param name="options">The parsed command line.</param>
    public KnobSensitivityCommand(KnobSensitivityOptions options) => _o = options;

    /// <summary>Runs the whole sweep and writes the JSON artefact.</summary>
    /// <param name="ct">Cancellation; a Ctrl-C writes what has been measured so far.</param>
    /// <returns>0 on success, 2 when the renderer is unavailable.</returns>
    public async Task<int> RunAsync(CancellationToken ct)
    {
        var (di, diName) = LoadReferenceDi();
        Console.WriteLine($"Reference DI: {diName} ({di.Samples.Length} samples @ {di.SampleRate} Hz)");

        Environment.SetEnvironmentVariable("DCA_ENABLE_NULL_DEVICE", "1");
        using var host = new NullDeviceRigHost();
        var renderer = new RigRenderer(host);
        if (!renderer.IsAvailable)
        {
            Console.Error.WriteLine($"error: the offline renderer is unavailable ({renderer.Describe()}). "
                                    + "No numbers are written — a fabricated sensitivity table is worse than none.");
            return 2;
        }

        Console.WriteLine($"Renderer: {renderer.Describe()}");
        var similarity = new ToneSimilarity();

        var modules = new Dictionary<string, Dictionary<string, double>>(StringComparer.Ordinal);
        var contexts = new Dictionary<string, string>(StringComparer.Ordinal);
        var failed = new List<string>();

        var total = RigLabelVocabulary.Modules.Sum(m => m.Params.Count);
        var budget = _o.Limit > 0 ? Math.Min(_o.Limit, total) : total;
        Console.WriteLine($"{RigLabelVocabulary.Modules.Count} modules, {total} params"
                          + (budget < total ? $" — measuring the first {budget}." : "."));

        var sw = Stopwatch.StartNew();
        var measured = 0;
        var stopped = false;

        foreach (var module in RigLabelVocabulary.Modules)
        {
            if (stopped) break;
            var values = new Dictionary<string, double>(StringComparer.Ordinal);
            modules[module.TypeId] = values;

            // Routing is topology, not tone: a splitter has no knob that can move a
            // distance measured on one linear chain. Recorded as 0, flagged, skipped.
            if (string.Equals(module.Category, "routing", StringComparison.Ordinal))
            {
                contexts[module.TypeId] = "skipped";
                foreach (var p in module.Params) values[p.Id] = 0.0;
                Console.WriteLine($"{module.TypeId}: skipped (routing)");
                continue;
            }

            string context;
            try
            {
                context = await PickContextAsync(renderer, similarity, di, module.TypeId, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                contexts[module.TypeId] = "failed";
                foreach (var p in module.Params) values[p.Id] = 0.0;
                failed.Add($"{module.TypeId}: context probe {ex.GetType().Name}: {ex.Message}");
                Console.Error.WriteLine($"{module.TypeId}: FAILED ({ex.GetType().Name}: {ex.Message})");
                continue;
            }

            contexts[module.TypeId] = context;

            foreach (var param in module.Params)
            {
                if (ct.IsCancellationRequested || measured >= budget) { stopped = true; break; }
                try
                {
                    var low = await RenderAsync(renderer, di, module.TypeId, context, param.Id, LowUnit, ct)
                        .ConfigureAwait(false);
                    var high = await RenderAsync(renderer, di, module.TypeId, context, param.Id, HighUnit, ct)
                        .ConfigureAwait(false);
                    values[param.Id] = similarity.Distance(low, high);
                }
                catch (OperationCanceledException) { stopped = true; break; }
                catch (Exception ex)
                {
                    values[param.Id] = 0.0;
                    failed.Add($"{module.TypeId}.{param.Id}: {ex.GetType().Name}: {ex.Message}");
                    Console.Error.WriteLine($"{module.TypeId}.{param.Id}: FAILED ({ex.GetType().Name}: {ex.Message})");
                }

                measured++;
            }

            var shown = module.Params.Where(p => values.ContainsKey(p.Id))
                .OrderByDescending(p => values[p.Id]).Take(3)
                .Select(p => $"{p.Id}={values[p.Id]:F3}");
            var perMin = measured / Math.Max(sw.Elapsed.TotalMinutes, 1e-9);
            Console.WriteLine($"[{measured}/{budget}] {module.TypeId} ({context}) "
                              + $"top: {string.Join(", ", shown)} · {perMin:F1} params/min · "
                              + $"eta {TimeSpan.FromMinutes((budget - measured) / Math.Max(perMin, 1e-9)):d\\.hh\\:mm\\:ss}");
        }

        // Every pair in the vocabulary gets an entry, measured or not — the python
        // side indexes this by the vocabulary and a hole there is a crash, not a
        // default.
        foreach (var module in RigLabelVocabulary.Modules)
        {
            if (!modules.TryGetValue(module.TypeId, out var values))
            {
                values = new Dictionary<string, double>(StringComparer.Ordinal);
                modules[module.TypeId] = values;
                contexts.TryAdd(module.TypeId, "unmeasured");
            }

            foreach (var p in module.Params) values.TryAdd(p.Id, 0.0);
        }

        Write(modules, contexts, failed, diName, measured, budget, total, sw.Elapsed);
        Console.WriteLine($"Done in {sw.Elapsed:d\\.hh\\:mm\\:ss}. {measured} params measured, "
                          + $"{failed.Count} failures. Wrote {_o.OutPath}");
        return 0;
    }

    // ------------------------------------------------------------------ render

    /// <summary>Renders the DI through <paramref name="moduleTypeId" /> with every
    /// param at its catalogue default except <paramref name="paramId" />, which is
    /// denormalised from <paramref name="unit" />.</summary>
    private static Task<MonoAudio> RenderAsync(RigRenderer renderer, MonoAudio di, string moduleTypeId,
        string context, string paramId, float unit, CancellationToken ct)
        => renderer.RenderAsync(di, BuildRig(moduleTypeId, context, paramId, unit), ct);

    /// <summary>Which context a module is measured in: solo when it is audible on its
    /// own, otherwise behind a default amp and cab.</summary>
    private static async Task<string> PickContextAsync(RigRenderer renderer, ToneSimilarity similarity,
        MonoAudio di, string moduleTypeId, CancellationToken ct)
    {
        var dry = await renderer.RenderAsync(di, new DcRigPreset([]), ct).ConfigureAwait(false);
        var solo = await renderer.RenderAsync(di, BuildRig(moduleTypeId, "solo", null, 0f), ct).ConfigureAwait(false);
        return similarity.Distance(dry, solo) > InaudibleEpsilon ? "solo" : "ampcab";
    }

    private static DcRigPreset BuildRig(string moduleTypeId, string context, string? paramId, float unit)
    {
        var modules = new List<DcRigModule>(3);
        if (string.Equals(context, "ampcab", StringComparison.Ordinal))
        {
            modules.Add(Defaults("amp"));
            modules.Add(Defaults("cab"));
        }

        modules.Add(Defaults(moduleTypeId, paramId, unit));
        return new DcRigPreset(modules);
    }

    /// <summary>A module with every catalogue param written out explicitly at its
    /// default, optionally overriding one of them from a normalised value.</summary>
    private static DcRigModule Defaults(string moduleTypeId, string? paramId = null, float unit = 0f)
    {
        var values = new Dictionary<string, float>(StringComparer.Ordinal);
        if (RigLabelVocabulary.TryGetModule(moduleTypeId, out var entry) && entry is not null)
        {
            foreach (var p in entry.Params)
            {
                values[p.Id] = string.Equals(p.Id, paramId, StringComparison.Ordinal)
                    ? RigLabelVocabulary.Denormalise(p, unit)
                    : p.Default;
            }
        }

        return new DcRigModule(moduleTypeId, values);
    }

    // ---------------------------------------------------------------------- io

    /// <summary>
    ///     The fixed reference DI, trimmed to <see cref="Seconds" />. A real DI off
    ///     the local pool when there is one (the sweep is about how knobs behave on
    ///     guitar, not on a synthesiser), and the repo's own deterministic
    ///     <see cref="ReferenceDi" /> when the pool is absent — flagged either way in
    ///     the artefact, because the two are not the same measurement.
    /// </summary>
    private (MonoAudio Audio, string Name) LoadReferenceDi()
    {
        var path = _o.DiPath;
        if (path is not null && Directory.Exists(path))
        {
            path = Directory.EnumerateFiles(path, "*.wav", SearchOption.AllDirectories)
                .OrderBy(p => p, StringComparer.Ordinal).FirstOrDefault();
        }

        if (path is not null && File.Exists(path))
        {
            var buffer = WavIo.Read(path);
            var mono = buffer.Channels == 1 ? buffer.Interleaved : buffer.ToMono();
            var audio = MonoResampler.To(new MonoAudio(mono, buffer.SampleRate), RigRenderer.RenderRate);
            return (Trim(audio), Path.GetFileName(path));
        }

        if (_o.DiPath is not null)
            Console.WriteLine($"WARNING: no DI found at {_o.DiPath}; falling back to the synthesised reference DI.");

        return (Trim(ReferenceDi.Generate()), $"{ReferenceDi.Id} (synthesised, no DI pool)");
    }

    private static MonoAudio Trim(MonoAudio audio)
    {
        var frames = (int)(Seconds * RigRenderer.RenderRate);
        var samples = new float[frames];
        Array.Copy(audio.Samples, samples, Math.Min(frames, audio.Samples.Length));
        return new MonoAudio(samples, audio.SampleRate);
    }

    private void Write(Dictionary<string, Dictionary<string, double>> modules,
        Dictionary<string, string> contexts, List<string> failed, string diName,
        int measured, int budget, int total, TimeSpan elapsed)
    {
        // Hand-written rather than serialised: the python side reads this file by
        // hand too, and a stable, diffable, module-per-block layout is worth more
        // here than a serializer's formatting.
        var sb = new StringBuilder();
        var inv = CultureInfo.InvariantCulture;
        sb.Append("{\n");
        sb.Append($"  \"schemaVersion\": {SchemaVersion},\n");
        sb.Append("  \"method\": \"render-ab-tonefx\",\n");
        sb.Append($"  \"generated\": \"{DateTimeOffset.Now:yyyy-MM-dd}\",\n");
        sb.Append("  \"distance\": \"ToneSimilarity\",\n");
        sb.Append($"  \"referenceDi\": {Quote(diName)},\n");
        sb.Append($"  \"lowUnit\": {LowUnit.ToString("0.###", inv)},\n");
        sb.Append($"  \"highUnit\": {HighUnit.ToString("0.###", inv)},\n");
        sb.Append("  \"modules\": {\n");

        var first = true;
        foreach (var module in RigLabelVocabulary.Modules)
        {
            if (!first) sb.Append(",\n");
            first = false;
            var values = modules[module.TypeId];
            var body = module.Params.Select(p =>
                $"{Quote(p.Id)}: {values[p.Id].ToString("0.######", inv)}");
            sb.Append($"    {Quote(module.TypeId)}: {{ {string.Join(", ", body)} }}");
        }

        sb.Append("\n  },\n");
        sb.Append("  \"contexts\": {\n    ");
        sb.Append(string.Join(",\n    ", RigLabelVocabulary.Modules
            .Select(m => $"{Quote(m.TypeId)}: {Quote(contexts.GetValueOrDefault(m.TypeId, "unmeasured"))}")));
        sb.Append("\n  },\n");
        sb.Append("  \"failed\": [");
        if (failed.Count > 0)
            sb.Append("\n    " + string.Join(",\n    ", failed.Select(Quote)) + "\n  ");
        sb.Append("],\n");
        // Rendered fine, every knob read 0.0 — a module that needs something this
        // sweep does not give it (a footswitch event, a capture file). Named rather
        // than left for the reader to notice, because a row of zeros otherwise looks
        // like "this module does not matter".
        var inert = RigLabelVocabulary.Modules
            .Where(m => !string.Equals(contexts.GetValueOrDefault(m.TypeId), "skipped", StringComparison.Ordinal))
            .Where(m => m.Params.Count > 0 && m.Params.All(p => modules[m.TypeId][p.Id] == 0.0))
            .Select(m => m.TypeId)
            .ToArray();
        sb.Append($"  \"inert\": [{string.Join(", ", inert.Select(Quote))}],\n");
        sb.Append($"  \"notes\": {Quote(Notes(measured, budget, total, elapsed, inert))}\n");
        sb.Append("}\n");

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_o.OutPath))!);
        File.WriteAllText(_o.OutPath, sb.ToString());
    }

    private static string Notes(int measured, int budget, int total, TimeSpan elapsed, string[] inert) =>
        "RAW ToneSimilarity distances between two renders of one fixed DI through one module, "
        + $"that module's param at normalised {LowUnit.ToString(CultureInfo.InvariantCulture)} vs "
        + $"{HighUnit.ToString(CultureInfo.InvariantCulture)}, every other param at its catalogue default. "
        + "NOT normalised — the python side normalises and applies its own floor. "
        + "'contexts' says how each module was measured: 'solo' = the module alone; 'ampcab' = behind a "
        + "default amp and cab, used when the module was inaudible on its own (probed, not hand-listed); "
        + "'skipped' = routing (splitter/merger), recorded as 0; 'failed'/'unmeasured' = 0, see 'failed'. "
        + "A distance measured in 'ampcab' is not on the same scale as a 'solo' one. "
        + "RigRenderer level-matches its output and the metric is content-invariant, so a pure output-level "
        + "or mix-to-unity knob legitimately reads near zero: the tone metric does not hear it. "
        + (inert.Length == 0
            ? ""
            : $"'inert' ({string.Join(", ", inert)}) rendered without error but moved NOTHING on any knob — "
              + "a module that needs something an offline A/B does not give it: a footswitch/trigger event "
              + "(freeze, looper, glitch-style captures) or a string param this sweep never sets (namamp's "
              + "capture path, a cab IR path). Those zeros are a gap in the measurement, NOT evidence that "
              + "the module's knobs do not matter — weight them by hand or by family, never by this file. ")
        + $"{measured} of {total} params measured (budget {budget}) in {elapsed:d\\.hh\\:mm\\:ss}.";

    private static string Quote(string s)
    {
        var sb = new StringBuilder(s.Length + 2).Append('"');
        foreach (var c in s)
        {
            sb.Append(c switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                < ' ' => $"\\u{(int)c:x4}",
                _ => c.ToString()
            });
        }

        return sb.Append('"').ToString();
    }
}

/// <summary>The <c>knob-sensitivity</c> command line.</summary>
/// <param name="OutPath">Where the JSON artefact is written.</param>
/// <param name="DiPath">A WAV file, or a directory whose first WAV ordinally is used;
/// null falls back to the synthesised reference DI.</param>
/// <param name="Limit">Measure only the first N params; 0 or less means all of them.</param>
public sealed record KnobSensitivityOptions(string OutPath, string? DiPath, int Limit);
