using System.Globalization;
using System.Text;
using DotCharter.Services.Ai.Match;
using DotCharter.Services.Audio.Coordination;
using DotCharter.Tone.Audio.Dsp.Loudness;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;
using DotCharter.Tests.Audio.Dsp.RsToneConversion.Coverage;
using DotCharter.Tests.Audio.Dsp.Tones;
using DotCharter.Tone.Audio.Dsp.Tones;

namespace DotCharter.Tests.Audio.Dsp.RsToneConversion.Fidelity;

/// <summary>
///     The Rocksmith-tone calibration harness: renders <see cref="ReferenceDi" />
///     through converted tones on the REAL native rig and holds the result to
///     <see cref="RsToneFidelityTargets" /> — relational targets from the gear's
///     documented character, because no Rocksmith reference render exists to
///     compare against. Every target runs in the default gate (skipped without
///     the DLL).
///     <para>
///         <b>Eval output</b>, never in the repo: set
///         <c>DOTCHARTER_RS_FIDELITY_OUT</c> to a directory and
///         <see cref="EvalReport_IsWrittenOnRequest" /> writes <c>targets.md</c>,
///         <c>tones.csv</c> and WAV renders there; add
///         <c>DOTCHARTER_RS_FIDELITY_LIBRARY=1</c> to also render every distinct
///         tone of the local Rocksmith library (read-only) into
///         <c>library.csv</c> with its loudness spread.
///     </para>
/// </summary>
[Collection("NativeEngine")]
public sealed class RsToneFidelityHarnessTests : IClassFixture<RsToneFidelityHarnessTests.Fixture>
{
    public const string OutVariable = "DOTCHARTER_RS_FIDELITY_OUT";
    public const string LibraryVariable = "DOTCHARTER_RS_FIDELITY_LIBRARY";

    private readonly Fixture _fx;

    public RsToneFidelityHarnessTests(Fixture fx) => _fx = fx;

    public static TheoryData<string> TargetIds => [.. RsToneFidelityTargets.All.Select(t => t.Id)];

    /// <summary>The targets that measure gear through the player's install (an amp's
    /// chain, a pedal's knob curves). Those are read from the player's install (#755),
    /// so these run on the owner's machine only; every other target runs everywhere.</summary>
    private static readonly HashSet<string> GameTableTargets = new(StringComparer.Ordinal)
    {
        "gain-knob-drives-every-guitar-amp", "or50h-punch-is-its-band", "semi-parametric-mid-moves", "hw300b-high-shift",
        "hw300b-low-shift", "hw300b-mid-selector", "hg500-mid-frequency-moves-the-cut", "clh300b-enhance-is-a-smile",
        "dsl-resonance-lifts-lows", "bright-switch-lifts-treble", "studio-eq-bass-freq-curve", "studio-eq-q-switch",
        "compression-release-follows-knob", "compression-attack-follows-knob", "bitcruncher-filter-knob-up-is-brighter",
        "lofi-hi-cut-follows-knob", "bass-emulator-octave-down"
    };

    [Fact]
    public void GameTableTargets_AreRealTargets()
        => Assert.Empty(GameTableTargets.Except(RsToneFidelityTargets.All.Select(t => t.Id)));

    [Theory]
    [MemberData(nameof(TargetIds))]
    public void Target_Holds(string id)
    {
        RequireNative();
        var real = TestRsGameTables.TryReal();
        Assert.SkipWhen(real is null && GameTableTargets.Contains(id), "needs the Rocksmith game tables: " + TestRsGameTables.Source);
        using var tables = RsGameTables.Scope(real ?? RsGameTables.Empty);
        var target = RsToneFidelityTargets.All.Single(t => t.Id == id);
        var result = target.Evaluate(_fx.Render);
        Assert.True(result.Pass, $"{target.Claim}\nmeasured: {result.Measured}\nsource: {target.Source}");
    }

    /// <summary>
    ///     #74: every cab model the bank gained (12-25) is named — by its catalogue
    ///     label — in the claim of at least one target, so no new IR ships without a
    ///     measured claim about it. Pure: reads the target list and the label mirror.
    /// </summary>
    [Fact]
    public void CabTargets_CoverEveryNewModel()
    {
        var labels = RsGearCoverageJudgements.Labels[(DcRigTargetCatalog.Cab, "model")];
        var claims = string.Join('\n', RsToneFidelityTargets.All.Select(t => t.Claim));
        var missing = Enumerable.Range(12, labels.Length - 12).Where(m => !claims.Contains(labels[m], StringComparison.Ordinal))
            .Select(m => $"{m} {labels[m]}").ToList();
        Assert.True(labels.Length == 26, $"the cab label mirror has {labels.Length} models");
        Assert.True(missing.Count == 0, "no fidelity target names: " + string.Join(", ", missing));
    }

    /// <summary>
    ///     #155: the Avenged Sevenfold "Afterlife" lead (Pedal_Compression into the
    ///     Cali-100 at Gain 91) amplifies an idle guitar's hiss and mains hum to the
    ///     level of the notes — measured on the real tone, a -90 dBFS idle came out at
    ///     -26 dBFS, louder than the playing. So every song tone sits behind an
    ///     input gate (<see cref="RsInputGate" />). Held here
    ///     through the product's own prepend: with a single-coil's idle (hiss -78,
    ///     hum -72 dBFS RMS) after three seconds of it, the idle output is at least
    ///     40 dB under the played phrase, and the phrase (over the same noise) is
    ///     within 1 dB of the ungated chain's: it shuts the buzz, not the playing.
    /// </summary>
    [Fact]
    public void AHighGainSongTone_IsSilentAtIdle_BehindTheInputGate()
    {
        RequireNative();
        var tone = RsToneFidelityTones.Convert("Amp_CA100", "Cab_CA412C_57_Cone",
            RsToneFidelityTones.Pots(("Gain", 91), ("Bass", 85), ("Mid", 79), ("Treble", 35), ("Pres", 15)),
            prePedals: [("Pedal_Compression", RsToneFidelityTones.Pots(("Comp", 25), ("Attack", 27), ("Release", 75)))]);
        var gated = RsInputGate.Prepend(tone);

        const int sr = ReferenceDi.SampleRate;
        const int lead = 3 * sr;
        var di = ReferenceDi.Samples;
        var noise = ReferenceDi.IdleNoise(lead + di.Length);
        var played = new float[noise.Length];
        for (var i = 0; i < played.Length; i++) played[i] = noise[i] + (i >= lead ? di[i - lead] : 0f);
        var idle = ReferenceDi.IdleNoise(lead + 4 * sr);

        var idleDb = RmsDb(_fx.Harness.RenderInput(gated, idle), lead + sr, idle.Length);
        var playDb = RmsDb(_fx.Harness.RenderInput(gated, played), lead, lead + ReferenceDi.PlayEndSample);
        var ungatedPlayDb = RmsDb(_fx.Harness.RenderInput(tone, played), lead, lead + ReferenceDi.PlayEndSample);
        var ungatedIdleDb = RmsDb(_fx.Harness.RenderInput(tone, idle), lead + sr, idle.Length);
        var measured = string.Create(CultureInfo.InvariantCulture,
            $"idle {idleDb:F1} dBFS (ungated {ungatedIdleDb:F1}), played {playDb:F1} dBFS (ungated {ungatedPlayDb:F1})");

        Assert.True(playDb - idleDb >= 40, "the idle buzz is not gated: " + measured);
        Assert.True(Math.Abs(playDb - ungatedPlayDb) < 1, "the input gate changed the playing: " + measured);
    }

    private static double RmsDb(float[] x, int from, int to)
    {
        double s = 0;
        for (var i = from; i < to; i++) s += (double)x[i] * x[i];
        return 20 * Math.Log10(Math.Sqrt(s / Math.Max(1, to - from)) + 1e-20);
    }

    [Fact]
    public void LoudnessEstimate_Error_StaysUnderItsMeasuredFloor()
    {
        // MEASURED FINDING, not a contract: the loudness walker
        // (DcRigLoudnessNormalizer) treats a cabinet as level-neutral, but a cab IR
        // raises the PEAK of a saturated amp by +5 to +11 dB (Gibbs overshoot of a
        // squared-off wave through a resonant low-pass; RMS moves only +1 to +7).
        // Measured 2026-10-04 on the three stock tones: lead renders +13.1 dB over
        // its estimate, rhythm -0.3, bass +3.1 — and the clean rhythm tone sits
        // ~21 dB under the lead in RMS though both "estimate" -12 dBFS.
        // This is a REGRESSION FLOOR (the repo's ±6 dB contract is what it should
        // become): it fails only if the error grows past what main shipped with.
        RequireNative();
        var lines = new List<string>();
        var worst = 0.0;
        foreach (var name in new[] { "default_lead", "default_rhythm", "default_bass" })
        {
            var tone = ToneFixtures.ParseFixture(name + ".json");
            var render = _fx.Render(name, new RsToneConverter().ConvertFilled(tone, out _));
            var error = render.Measurement.PeakDbfs - render.EstimatedPeakDbfs;
            worst = Math.Max(worst, Math.Abs(error));
            lines.Add(string.Create(CultureInfo.InvariantCulture,
                $"{name}: rendered peak {render.Measurement.PeakDbfs:F1} dBFS, estimate {render.EstimatedPeakDbfs:F1}, " +
                $"rms {render.Measurement.RmsDbfs:F1}"));
        }

        Assert.True(worst <= 16.0,
            "the walker's peak estimate is further off the render than the measured floor (16 dB):\n" +
            string.Join("\n", lines));
    }

    /// <summary>The ±3 LU contract the rendered levelling (#67) is held to.</summary>
    public const double LevelledToleranceLu = 3.0;

    [Fact]
    public async Task RenderedLevelling_PutsTheStockTones_OnOneLoudness()
    {
        // #67's contract, judged with ReferenceDi — NOT the phrase the product
        // levels with (CalibrationPhrase), so this measures how the levelling
        // GENERALISES, not a tautology. Under the estimate the stock lead and
        // rhythm rendered ~24 dB apart in RMS (both "estimated" -12 dBFS peak).
        // The lead/rhythm pair is one song's set; the bass is levelled on its own,
        // as the product levels per arrangement.
        RequireNative();
        var lines = new List<string>();
        var residuals = new List<double>();
        string[][] songs = [["default_lead", "default_rhythm"], ["default_bass"]];
        foreach (var song in songs)
        {
            var tones = song.Select(n => (Name: n, Tone: ToneFixtures.ParseFixture(n + ".json"))).ToList();
            var levelled = await _fx.LevelAsync(
                [.. tones.Select(t => (t.Name, new RsToneConverter().ConvertFilled(t.Tone, out _), t.Tone.VolumeDb))]);
            var intents = RsToneLoudnessPolicy.IntentOffsetsDb([.. tones.Select(t => t.Tone.VolumeDb)]);
            for (var i = 0; i < tones.Count; i++)
            {
                var render = _fx.RenderUncached(tones[i].Name + "-levelled", levelled[tones[i].Name]);
                var lufs = PlayingLufs(render);
                residuals.Add(lufs - intents[i]);
                lines.Add(string.Create(CultureInfo.InvariantCulture,
                    $"{tones[i].Name}: {lufs:F1} LUFS (intent {intents[i]:+0.0;-0.0}), peak {render.Measurement.PeakDbfs:F1} dBFS"));
            }
        }

        Assert.True(residuals.Max() - residuals.Min() <= 2 * LevelledToleranceLu,
            $"the levelled stock tones are not within ±{LevelledToleranceLu} LU of one loudness:\n" + string.Join("\n", lines));
    }

    /// <summary>Integrated loudness over the playing part of a render of ReferenceDi.</summary>
    public static double PlayingLufs(ToneRender render)
        => IntegratedLoudness.Measure(render.Output.AsSpan(0, ReferenceDi.PlayEndSample), ReferenceDi.SampleRate);

    [Fact]
    public void ARender_IsDeterministic_AndDoesNotHearThePreviousTone()
    {
        // The harness's numbers are only diffable if a render depends on its
        // preset alone. The trap is real: the rig lets a REMOVED reverb ring out,
        // and with a fixed pre-roll a dry tone rendered after a long hall measured
        // -27.9 dBFS RMS with a 2 s "tail" instead of -39.3 / 10 ms.
        RequireNative();
        var dry = RsToneFidelityTones.Convert("Amp_TW22", "Cab_TW112C_57_Cone");
        var hall = RsToneFidelityTones.Convert("Amp_MarshallJCM800", "Cab_GB412CMKIII_57_Cone",
            racks: [("Rack_StudioVerb", RsToneFidelityTones.Pots(("Time", 90), ("Mix", 60)))]);

        var first = _fx.RenderUncached("hist-dry-1", dry);
        _fx.RenderUncached("hist-hall", hall);
        var afterHall = _fx.RenderUncached("hist-dry-2", dry);

        Assert.Equal(first.Measurement.RmsDbfs, afterHall.Measurement.RmsDbfs, 6);
        Assert.Equal(first.Measurement.TailMs, afterHall.Measurement.TailMs, 6);
        Assert.Equal(first.Drive.ThdPercent, afterHall.Drive.ThdPercent, 6);
    }

    [Fact]
    public void AmpVoicingTrims_MatchTheRig()
    {
        // RsToneConverter.AmpVoicingTrimDb is a MEASUREMENT of the native amp
        // roster; this re-takes it the same way so a change to dcdsp_amp.h that
        // moves a voicing's level fails here instead of silently unlevelling
        // every converted tone on that voicing.
        RequireNative();
        var drift = new List<string>();
        var measuredAll = new List<string>();
        for (var v = 0; v < RsToneConverter.AmpVoicingTrimDb.Length; v++)
        {
            var measured = MeasureVoicingTrimDb(v);
            measuredAll.Add(measured.ToString("F1", CultureInfo.InvariantCulture));
            if (Math.Abs(measured - RsToneConverter.AmpVoicingTrimDb[v]) > 1.5)
            {
                drift.Add(string.Create(CultureInfo.InvariantCulture,
                    $"voicing {v}: committed {RsToneConverter.AmpVoicingTrimDb[v]:F1} dB, measured {measured:F1} dB"));
            }
        }

        Assert.True(drift.Count == 0,
            "re-measure RsToneConverter.AmpVoicingTrimDb:\n" + string.Join("\n", drift) +
            "\nmeasured now: " + string.Join(", ", measuredAll));
    }

    [Fact]
    public void JazzSolid120_SitsWithTheCleanRoster_AtEqualKnobs()
    {
        // #71: voicing 5 (Jazz Solid 120, what Amp_CS90/100/120 convert to) used to
        // ATTENUATE — it rendered ~15 LU under the Blackface Twin at equal knobs, its
        // fundamental 16 dB under the input at gain 0.2. dcdsp_amp.h now gives it the
        // Twin's gain staging. Measured with the walker out of the way (no trailing
        // gain), so this is the AMP's own level.
        RequireNative();
        var lines = new List<string>();
        var worst = 0.0;
        foreach (var gain in new[] { 20f, 50f, 80f })
        {
            var twin = PlayingLufs(_fx.RenderUncached($"twin-{gain}", AmpOnly(0, gain)));
            var jazz = PlayingLufs(_fx.RenderUncached($"jazz-{gain}", AmpOnly(5, gain)));
            worst = Math.Max(worst, Math.Abs(jazz - twin));
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"gain {gain}: Twin {twin:F1} LUFS, Jazz Solid {jazz:F1} LUFS"));
        }

        Assert.True(worst <= LevelledToleranceLu,
            $"Jazz Solid 120 is more than {LevelledToleranceLu} LU off the Blackface Twin at equal knobs:\n" + string.Join("\n", lines));
    }

    /// <summary>The JCM800 knob set on the guitar <c>amp</c> at <paramref name="voicing" />
    /// through a 1x12, with no trailing gain. RS conversion no longer lands on the
    /// roster (#88), so the amp node is built here exactly as the pre-#88
    /// converter built it for the JCM800: each knob (v - 1) / 99, every other
    /// param at its catalogue default.</summary>
    private static DcRigPreset AmpOnly(int voicing, float gain)
        => new([
            .. JcmOnRoster(voicing, gain, "Cab_TW112C_57_Cone", master: 0.5f)
        ]);

    /// <summary>The guitar <c>amp</c> node with the JCM800's knob set, then the
    /// cab and mic layer the converter puts behind a JCM800.</summary>
    private static List<DcRigModule> JcmOnRoster(int voicing, float gain, string cab, float? master = null)
    {
        var p = DcRigTargetCatalog.AllModules.Single(m => m.ModuleTypeId == DcRigTargetCatalog.Amp).Params
            .ToDictionary(s => s.Id, s => s.Default, StringComparer.Ordinal);
        p["voicing"] = voicing;
        p["gain"] = (gain - 1f) / 99f;
        p["bass"] = p["mid"] = p["treble"] = p["presence"] = 49f / 99f;
        if (master is { } m) p["master"] = m;
        var converted = RsToneFidelityTones.Convert("Amp_MarshallJCM800", cab);
        return [new DcRigModule(DcRigTargetCatalog.Amp, p), .. converted.Modules.Skip(1).Take(converted.Modules.Count - 2)];
    }

    /// <summary>
    ///     The trim procedure: the RS JCM800 knob set (Bass/Mid/Treble/Pres 50) at
    ///     Gain 20 and 80, through a 1x12 and a 4x12, with the amp switched to
    ///     <paramref name="voicing" /> and the plain walker's trailing gain; the trim
    ///     is -12 dBFS minus the mean rendered peak over the DI's playing part.
    /// </summary>
    private double MeasureVoicingTrimDb(int voicing)
    {
        var peaks = new List<double>();
        foreach (var cab in new[] { "Cab_TW112C_57_Cone", "Cab_GB412CMKIII_57_Cone" })
        {
            foreach (var gain in new[] { 20f, 80f })
            {
                var body = JcmOnRoster(voicing, gain, cab);
                body.Add(new DcRigModule("gain", new Dictionary<string, float>
                {
                    ["gainDb"] = DcRigLoudnessNormalizer.ComputeTrailingGainDb(body)
                }));
                peaks.Add(_fx.Harness.RenderPlayingPeakDbfs(new DcRigPreset(body)));
            }
        }

        return DcRigLoudnessNormalizer.TargetOutputPeakDb - peaks.Average();
    }

    [Fact]
    public void RsAmpModelTrims_MatchTheRig()
    {
        // RsAmpLevelTrims.ByKey is a MEASUREMENT of the game's own
        // chains (#88); this re-takes it so a change to dcdsp_rsamp.h or to the
        // generated table that moves a model's level fails here instead of
        // silently unlevelling every converted tone on that amp. The chains are the
        // player's install's (#755): owner-only.
        RequireNative();
        using var tables = TestRsGameTables.UseReal();
        var drift = new List<string>();
        var measuredAll = new List<string>();
        for (var m = 0; m < RsAmpChains.Models.Count; m++)
        {
            var measured = MeasureRsAmpTrimDb(m);
            measuredAll.Add(measured.ToString("F1", CultureInfo.InvariantCulture) + "f");
            var committed = RsAmpLevelTrims.ByKey[RsAmpChains.Models[m].Key];
            if (Math.Abs(measured - committed) > 1.5)
            {
                drift.Add(string.Create(CultureInfo.InvariantCulture,
                    $"{RsAmpChains.Models[m].Key}: committed {committed:F1} dB, measured {measured:F1} dB"));
            }
        }

        Assert.True(drift.Count == 0,
            "re-measure RsAmpLevelTrims.ByKey:\n" + string.Join("\n", drift) +
            "\nmeasured now: " + string.Join(", ", measuredAll));
    }

    /// <summary>
    ///     The <c>rsamp</c> trim procedure: the amp with EVERY knob at 20 % and then
    ///     at 80 % of its own travel, through a 4x12 (guitar amps) or an 8x10 (bass
    ///     heads and DI preamps) with its mic layer, and the plain walker's trailing
    ///     gain; the trim is -12 dBFS minus the mean rendered peak over the DI's
    ///     playing part.
    /// </summary>
    private double MeasureRsAmpTrimDb(int model)
    {
        var row = RsAmpChains.Models[model];
        var cab = row.Key.StartsWith("Amp_", StringComparison.Ordinal) ? "Cab_GB412CMKIII_57_Cone" : "Bass_Cab_AT810BC_57_Cone";
        var peaks = new List<double>();
        foreach (var at in new[] { 0.2f, 0.8f })
        {
            var pots = row.Knobs.ToDictionary(k => k.Key[(row.Key.Length + 1)..], k => k.Min + at * (k.Max - k.Min), StringComparer.Ordinal);
            var converted = RsToneFidelityTones.Convert(row.Key, cab, pots);
            var body = converted.Modules.Take(converted.Modules.Count - 1).ToList();
            body.Add(new DcRigModule("gain", new Dictionary<string, float>
            {
                ["gainDb"] = DcRigLoudnessNormalizer.ComputeTrailingGainDb(body)
            }));
            peaks.Add(_fx.Harness.RenderPlayingPeakDbfs(new DcRigPreset(body)));
        }

        return DcRigLoudnessNormalizer.TargetOutputPeakDb - peaks.Average();
    }

    [Fact]
    public async Task EvalReport_IsWrittenOnRequest()
    {
        var outDir = Environment.GetEnvironmentVariable(OutVariable);
        Assert.SkipWhen(string.IsNullOrWhiteSpace(outDir), $"set {OutVariable} to a directory OUTSIDE the repo to write the eval report");
        RequireNative();
        Directory.CreateDirectory(outDir!);

        var md = new StringBuilder();
        md.Append("# Rocksmith tone fidelity — calibration targets\n\n");
        md.Append(CultureInfo.InvariantCulture, $"Generated {DateTime.Now:yyyy-MM-dd HH:mm} by RsToneFidelityHarnessTests. ");
        md.Append("Reference DI: ReferenceDi (Karplus-Strong E2, E4, E5 power chord, -18 dBFS peak, 48 kHz). ");
        md.Append("No Rocksmith reference render exists; every target is relational, from documented gear character.\n\n");
        md.Append("| Target | Result | Measured | Claim | Source |\n|---|---|---|---|---|\n");
        foreach (var t in RsToneFidelityTargets.All)
        {
            var r = t.Evaluate(_fx.Render);
            md.Append(CultureInfo.InvariantCulture, $"| `{t.Id}` | {(r.Pass ? "PASS" : "FAIL")} | {r.Measured} | {t.Claim} | {t.Source} |\n");
        }

        foreach (var name in new[] { "default_lead", "default_rhythm", "default_bass" })
            _fx.Render(name, new RsToneConverter().ConvertFilled(ToneFixtures.ParseFixture(name + ".json"), out _));

        File.WriteAllText(Path.Combine(outDir!, "targets.md"), md.ToString());
        WriteCsv(Path.Combine(outDir!, "tones.csv"), _fx.AllRenders);
        var wavDir = Path.Combine(outDir!, "wav");
        Directory.CreateDirectory(wavDir);
        foreach (var r in _fx.AllRenders) WriteWav(Path.Combine(wavDir, SafeName(r.Name) + ".wav"), r.Output);
        WriteWav(Path.Combine(wavDir, "_reference-di.wav"), ReferenceDi.Samples);

        if (Environment.GetEnvironmentVariable(LibraryVariable) == "1") await WriteLibraryAsync(outDir!);
    }

    /// <summary>
    ///     Every distinct tone of the local library, rendered twice through
    ///     <see cref="ReferenceDi" />: with the converter's ESTIMATE, and LEVELLED the
    ///     way the product levels a song (<see cref="RsToneLevelling" /> over the real
    ///     <see cref="RenderedToneLoudnessMeter" />, per song, guitar and bass apart —
    ///     the product levels per arrangement). A song is the package, or for the RS1
    ///     compatibility disc the tone key's prefix.
    /// </summary>
    private async Task WriteLibraryAsync(string outDir)
    {
        var root = TestGameInstall.Directories
            .Select(d => Path.Combine(d, "dlc"))
            .FirstOrDefault(Directory.Exists);
        if (root is null) return;

        var converter = new RsToneConverter();
        var tones = new List<LibraryTone>();
        foreach (var (package, tone) in RsGearLibraryUsage.DistinctTones(root))
        {
            var preset = converter.ConvertFilled(tone, out var report);
            var name = Path.GetFileName(package);
            tones.Add(new LibraryTone(tones.Count, name, SongOf(name, tone), tone, preset, report.ApproximatedCount, report.SkippedCount));
        }

        var levelled = new Dictionary<int, (DcRigPreset Preset, float Intent)>();
        foreach (var group in tones.GroupBy(t => t.Song))
        {
            var members = group.ToList();
            var result = await _fx.LevelAsync([.. members.Select(m => (Key(m), m.Preset, m.Tone.VolumeDb))]);
            var intents = RsToneLoudnessPolicy.IntentOffsetsDb([.. members.Select(m => m.Tone.VolumeDb)]);
            for (var i = 0; i < members.Count; i++)
                levelled[members[i].Index] = (result[Key(members[i])], intents[i]);
        }

        var rows = new List<LibraryRow>();
        foreach (var t in tones)
        {
            var estimate = _fx.RenderUncached($"lib{t.Index:D3}-{t.Tone.Key}", t.Preset);
            var (preset, intent) = levelled[t.Index];
            var after = _fx.RenderUncached($"lib{t.Index:D3}-{t.Tone.Key}-levelled", preset);
            rows.Add(new LibraryRow(t, intent, estimate, after));
        }

        var csv = new StringBuilder("package,song,tone,volume_db,intent_db,modules,estimated_peak_dbfs,est_gain_db,levelled_gain_db," +
                                    "est_rendered_peak_dbfs,est_lufs,lev_rendered_peak_dbfs,lev_lufs,centroid_hz,tilt_db_oct,tail_ms,thd_pct,approximated,skipped\n");
        foreach (var r in rows)
        {
            csv.Append(CultureInfo.InvariantCulture,
                $"{Csv(r.Tone.Package)},{Csv(r.Tone.Song)},{Csv(r.Tone.Tone.Key)},{r.Tone.Tone.VolumeDb:F2},{r.Intent:F2}," +
                $"{Csv(string.Join(" > ", r.Estimate.Preset.Modules.Select(m => m.ModuleTypeId)))}," +
                $"{r.Estimate.EstimatedPeakDbfs:F2},{RsToneLevelling.TrailingGainDb(r.Estimate.Preset):F2},{RsToneLevelling.TrailingGainDb(r.Levelled.Preset):F2}," +
                $"{r.Estimate.Measurement.PeakDbfs:F2},{PlayingLufs(r.Estimate):F2},{r.Levelled.Measurement.PeakDbfs:F2},{PlayingLufs(r.Levelled):F2}," +
                $"{r.Levelled.Measurement.CentroidHz:F0},{r.Levelled.Measurement.TiltDbPerOctave:F2},{r.Levelled.Measurement.TailMs:F0}," +
                $"{r.Levelled.Drive.ThdPercent:F2},{r.Tone.Approximated},{r.Tone.Skipped}\n");
        }

        File.WriteAllText(Path.Combine(outDir, "library.csv"), csv.ToString());

        var md = new StringBuilder("# Library loudness\n\n");
        md.Append(CultureInfo.InvariantCulture,
            $"{rows.Count} distinct tones rendered through ReferenceDi (the product levels with CalibrationPhrase). " +
            $"In target = the tone's integrated loudness minus its Volume intent, within ±{LevelledToleranceLu} LU of the library median. " +
            $"Per-song spread = max - min over a song's tones of (loudness - intent), songs with 2+ tones, guitar and bass apart.\n\n");
        md.Append("| | estimate (walker + voicing trim) | levelled (rendered loudness) |\n|---|---|---|\n");
        Summarise(md, rows);
        await AppendStockTonesAsync(md);
        File.WriteAllText(Path.Combine(outDir, "library.md"), md.ToString());

        var wavDir = Path.Combine(outDir, "wav-library");
        Directory.CreateDirectory(wavDir);
        foreach (var r in rows.Take(40)) WriteWav(Path.Combine(wavDir, SafeName(r.Levelled.Name) + ".wav"), r.Levelled.Output);
    }

    /// <summary>The three stock tones, estimate against levelled (lead + rhythm as
    /// one song, the bass on its own).</summary>
    private async Task AppendStockTonesAsync(StringBuilder md)
    {
        md.Append("\n## Stock tones\n\n| tone | RS Volume | estimate: LUFS / peak | levelled: LUFS / peak |\n|---|---|---|---|\n");
        string[][] songs = [["default_lead", "default_rhythm"], ["default_bass"]];
        foreach (var song in songs)
        {
            var tones = song.Select(n => (Name: n, Tone: ToneFixtures.ParseFixture(n + ".json"))).ToList();
            var converted = tones.Select(t => (t.Name, Preset: new RsToneConverter().ConvertFilled(t.Tone, out _), t.Tone.VolumeDb)).ToList();
            var levelled = await _fx.LevelAsync([.. converted]);
            foreach (var t in converted)
            {
                var est = _fx.RenderUncached(t.Name + "-stock-est", t.Preset);
                var lev = _fx.RenderUncached(t.Name + "-stock-lev", levelled[t.Name]);
                md.Append(CultureInfo.InvariantCulture,
                    $"| {t.Name} | {t.VolumeDb:F1} dB | {PlayingLufs(est):F1} / {est.Measurement.PeakDbfs:F1} | {PlayingLufs(lev):F1} / {lev.Measurement.PeakDbfs:F1} |\n");
            }
        }
    }

    private static string Key(LibraryTone t) => t.Index.ToString(CultureInfo.InvariantCulture);

    private sealed record LibraryTone(
        int Index, string Package, string Song, RsTone Tone, DcRigPreset Preset, int Approximated, int Skipped);

    private sealed record LibraryRow(LibraryTone Tone, float Intent, ToneRender Estimate, ToneRender Levelled);

    private static void Summarise(StringBuilder md, List<LibraryRow> rows)
    {
        string[] Cells(Func<LibraryRow, ToneRender> pick)
        {
            var lufs = rows.Select(r => PlayingLufs(pick(r))).Order().ToArray();
            var resid = rows.Select(r => PlayingLufs(pick(r)) - r.Intent).ToArray();
            var median = Pct([.. resid.Order()], 50);
            var inTarget = resid.Count(v => Math.Abs(v - median) <= LevelledToleranceLu);
            var peaks = rows.Select(r => pick(r).Measurement.PeakDbfs).Order().ToArray();
            var peakOut = peaks.Count(p => Math.Abs(p - DcRigLoudnessNormalizer.TargetOutputPeakDb) > RsToneFidelityTargets.LoudnessToleranceDb);
            var spreads = rows.GroupBy(r => r.Tone.Song).Where(g => g.Count() >= 2)
                .Select(g => g.Max(r => PlayingLufs(pick(r)) - r.Intent) - g.Min(r => PlayingLufs(pick(r)) - r.Intent))
                .Order().ToArray();
            return
            [
                string.Create(CultureInfo.InvariantCulture, $"{inTarget} / {rows.Count}"),
                string.Create(CultureInfo.InvariantCulture,
                    $"p10 / p50 / p90 {Pct(lufs, 10):F1} / {Pct(lufs, 50):F1} / {Pct(lufs, 90):F1} LUFS (p90-p10 {Pct(lufs, 90) - Pct(lufs, 10):F1} LU)"),
                string.Create(CultureInfo.InvariantCulture,
                    $"p50 / p90 / max {Pct(spreads, 50):F1} / {Pct(spreads, 90):F1} / {spreads.LastOrDefault():F1} LU ({spreads.Length} songs)"),
                string.Create(CultureInfo.InvariantCulture,
                    $"{peakOut} outside -12 ± 6; p50 / p90 / max {Pct(peaks, 50):F1} / {Pct(peaks, 90):F1} / {peaks.LastOrDefault():F1} dBFS")
            ];
        }

        var est = Cells(r => r.Estimate);
        var lev = Cells(r => r.Levelled);
        string[] labels = ["in target (±3 LU)", "loudness", "per-song spread", "rendered peak"];
        for (var i = 0; i < labels.Length; i++) md.Append(CultureInfo.InvariantCulture, $"| {labels[i]} | {est[i]} | {lev[i]} |\n");
    }

    /// <summary>The song a library tone belongs to, guitar and bass apart: its package,
    /// or — on the RS1 compatibility disc, one package for hundreds of songs — its
    /// key's prefix.</summary>
    private static string SongOf(string package, RsTone tone)
    {
        var song = package.Contains("rs1compat", StringComparison.OrdinalIgnoreCase) && tone.Key.LastIndexOf('_') is > 0 and var cut
            ? tone.Key[..cut]
            : package;
        var bass = tone.Amp.GearKey.StartsWith("Bass_", StringComparison.Ordinal) ||
                   tone.ToneDescriptors.Any(d => d.Contains("BASS", StringComparison.Ordinal));
        return song + (bass ? "|bass" : "|guitar");
    }

    private static double Pct(double[] sorted, int p)
        => sorted.Length == 0 ? double.NaN : sorted[Math.Clamp((int)Math.Round(p / 100.0 * (sorted.Length - 1)), 0, sorted.Length - 1)];

    private static void WriteCsv(string path, IEnumerable<ToneRender> renders)
    {
        var sb = new StringBuilder("tone,modules,estimated_peak_dbfs,rendered_peak_dbfs,rendered_rms_dbfs,centroid_hz,tilt_db_oct,tail_ms,thd_pct,");
        sb.Append(string.Join(",", ToneMetrics.BandCentresHz.Select(f => "band_" + f.ToString(CultureInfo.InvariantCulture))));
        sb.Append('\n');
        foreach (var r in renders)
        {
            sb.Append(CultureInfo.InvariantCulture,
                $"{Csv(r.Name)},{Csv(string.Join(" > ", r.Preset.Modules.Select(m => m.ModuleTypeId)))},{r.EstimatedPeakDbfs:F2}," +
                $"{r.Measurement.PeakDbfs:F2},{r.Measurement.RmsDbfs:F2},{r.Measurement.CentroidHz:F0},{r.Measurement.TiltDbPerOctave:F2}," +
                $"{r.Measurement.TailMs:F0},{r.Drive.ThdPercent:F2},");
            sb.Append(string.Join(",", r.Measurement.BandDb.Select(b => b.ToString("F2", CultureInfo.InvariantCulture))));
            sb.Append('\n');
        }

        File.WriteAllText(path, sb.ToString());
    }

    private static string Csv(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";

    private static string SafeName(string s)
        => new(s.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray());

    /// <summary>16-bit mono PCM at the reference rate, peak-limited at full scale.</summary>
    private static void WriteWav(string path, float[] samples)
    {
        using var w = new BinaryWriter(File.Create(path));
        var dataBytes = samples.Length * 2;
        w.Write("RIFF"u8);
        w.Write(36 + dataBytes);
        w.Write("WAVEfmt "u8);
        w.Write(16);
        w.Write((short)1);
        w.Write((short)1);
        w.Write(ReferenceDi.SampleRate);
        w.Write(ReferenceDi.SampleRate * 2);
        w.Write((short)2);
        w.Write((short)16);
        w.Write("data"u8);
        w.Write(dataBytes);
        foreach (var s in samples) w.Write((short)Math.Round(Math.Clamp(s, -1f, 1f) * 32767));
    }

    private void RequireNative()
        => Assert.SkipWhen(!_fx.Harness.Available, "native DSP ABI (juce_sharp.dll) / audio engine not loadable in this test environment");

    /// <summary>One harness and one render cache for the class: several targets
    /// share a render (the 57/Cone tone is in three of them).</summary>
    public sealed class Fixture : IDisposable
    {
        private readonly Dictionary<string, ToneRender> _cache = new(StringComparer.Ordinal);

        public RsToneFidelityHarness Harness { get; } = new();

        public IEnumerable<ToneRender> AllRenders => _cache.Values.OrderBy(r => r.Name, StringComparer.Ordinal);

        public ToneRender Render(string name, DcRigPreset preset)
        {
            if (_cache.TryGetValue(name, out var cached)) return cached;
            var render = Harness.Render(name, preset);
            _cache[name] = render;
            return render;
        }

        public ToneRender RenderUncached(string name, DcRigPreset preset) => Harness.Render(name, preset);

        // The PRODUCT's meter on its own device-less engine, created on first use.
        private NullDeviceRigHost? _meterHost;
        private RsToneLevelling? _levelling;

        /// <summary>
        ///     Levels <paramref name="tones" /> (one song's) exactly as the product does:
        ///     <see cref="RsToneLevelling" /> over the real <see cref="RenderedToneLoudnessMeter" />,
        ///     measured, then levelled again from the cache. Keyed by the tuple's name.
        /// </summary>
        public async Task<Dictionary<string, DcRigPreset>> LevelAsync(IReadOnlyList<(string Name, DcRigPreset Preset, float VolumeDb)> tones)
        {
            _levelling ??= new RsToneLevelling(new RenderedToneLoudnessMeter(_meterHost = new NullDeviceRigHost()));
            var converted = tones.ToDictionary(t => t.Name, t => t.Preset, StringComparer.OrdinalIgnoreCase);
            var volumes = tones.ToDictionary(t => t.Name, t => t.VolumeDb, StringComparer.OrdinalIgnoreCase);
            _levelling.Level(converted, volumes, out var pending);
            await _levelling.MeasureAsync(pending, CancellationToken.None);
            return _levelling.Level(converted, volumes, out _);
        }

        public void Dispose()
        {
            Harness.Dispose();
            _meterHost?.Dispose();
        }
    }
}
