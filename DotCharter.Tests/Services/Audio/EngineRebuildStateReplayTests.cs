using System.Reflection;
using System.Text.RegularExpressions;
using JuceSharp;

namespace DotCharter.Tests.Services.Audio;

/// <summary>
///     The engine-REBUILD replay contract. A fresh <c>JuceAudioEngine</c> starts
///     on the native defaults, so every piece of engine state that is not part of
///     <c>JuceEngineConfig</c> is silently gone after a device change, the NAM
///     48 kHz request or a watchdog recovery unless the rebuild replays it. This
///     project has shipped that bug twice already — the co-op input routing and
///     the six-band EQ — and both times it was invisible, because the UI kept
///     showing the setting the engine had forgotten.
///     <para>
///         Asserted on the SOURCE. Reaching any of it at runtime needs a real
///         audio device and a real device change; the invariant is structural,
///         which is the same argument <c>PlaybackGateHoldGuardTests</c> and
///         <c>EngineConfigPolicyTests</c> already make.
///     </para>
/// </summary>
public sealed class EngineRebuildStateReplayTests
{
    /// <summary>
    ///     THE ENUMERATION. Every engine setter the service calls anywhere must be
    ///     replayed by the rebuild, or be on the list below with a reason. This is
    ///     what catches the next `SetEqBand` — a new engine-state setter added
    ///     somewhere far from the rebuild path, which then works perfectly until
    ///     the user changes their interface.
    ///     <para>
    ///         The candidate list is DERIVED from <see cref="JuceAudioEngine" />
    ///         itself, not from a regex over one spelling of the receiver. Two
    ///         whole classes of engine state escaped the earlier
    ///         <c>_?engine.Set…(</c> scan and neither would ever have failed it:
    ///         a settable PROPERTY (<c>engine.StemGroupTempo = v</c> — a real
    ///         <c>dca_engine_set_stem_group_tempo</c> write, and there are two of
    ///         them in the service today), and a setter reached through any local
    ///         not named <c>engine</c> (<c>createdEngine</c>, <c>reloadEngine</c>
    ///         and <c>e</c> all exist in these files). Reflection cannot be
    ///         out-spelled.
    ///     </para>
    /// </summary>
    [Fact]
    public void EveryEngineSetterTheServiceCalls_IsReplayedByTheRebuild_OrIsExemptWithAReason()
    {
        var source = ServiceSource();
        var surface = EngineStateWriterNames();

        // The surface must be non-trivial, or every assertion below passes by
        // finding nothing — the exact vacuity this repo has shipped before.
        Assert.True(surface.Count >= 20,
            $"Only {surface.Count} engine-state writers were discovered on JuceAudioEngine; " +
            "the reflection filter has stopped seeing the ABI surface.");

        // Replayed by the rebuild: the master bus, the per-player surface, and
        // the EQ push the master-bus replay delegates to.
        var replayed = new[]
            {
                "private void ReapplyMasterBusStateLocked(JuceAudioEngine engine)",
                "private void AttachPerPlayerStateToEngine(JuceAudioEngine engine)",
                // …which delegates the per-seat write itself to WriteRoutingTo,
                // the ONE place the seat count / channels / trims are written —
                // live push and rebuild replay alike, so the two can never drift
                // into writing different surfaces. Listing the delegate is only
                // safe because PlayerInputRoutingStateTests asserts that
                // AttachPerPlayerStateToEngine still calls it; without that pair
                // this entry could excuse a helper the rebuild no longer reaches.
                "private void WriteRoutingTo(JuceAudioEngine engine, string reason)",
                "private static void PushEqTo(JuceAudioEngine engine, Equalizer.EqSection section, IAppLogger? logger)",
                // ABI v37 re-amp: the ONE write of an injected take, shared by a
                // live load and the rebuild replay (ReampTakes.ReplayTakesTo, called
                // from AttachPerPlayerStateToEngine). Safe to list only because
                // TheReampTake_IsReplayedByThePerPlayerAttach pins that call chain.
                "private bool PushTakeTo(JuceAudioEngine engine, int player, ReampTake take)",
                // ABI v38 chart synth: the ONE write of its engine state, shared by
                // every live change and the rebuild replay (ChartSynthEngineDriver.ReplayTo,
                // called from AttachPerPlayerStateToEngine). Safe to list only
                // because TheChartSynth_IsReplayedByThePerPlayerAttach pins that chain.
                "private void PushTo(JuceAudioEngine engine, string reason, bool includeNotes)",
            }
            .SelectMany(sig => EngineStateWritesIn(Body(source, sig), surface))
            .ToHashSet(StringComparer.Ordinal);

        // Not rebuild state. Each of these is re-derived by something the rebuild
        // already does; a new exemption needs the same kind of sentence.
        var exempt = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // The sandbox bridge is re-armed against the new engine by
            // RearmSandboxAfterEngineSwapLocked, which ReplaceEngineGated calls.
            ["SetRemoteBridge"] = "re-armed by RearmSandboxAfterEngineSwapLocked",
            // A function of "are stems loaded": LoadStemsCore sets faster, ClearStems
            // clears it, and the rebuild re-issues the stem set.
            ["SetStretchQuality"] = "re-derived by LoadStemsCore / ClearStems",
            // The shared stem-group stretcher only exists while stems are grouped
            // onto it, and LoadStemsCore is the one place a stem joins that bus —
            // it writes both from _playbackSpeed / _pitchShift immediately after
            // grouping. The rebuild re-issues the whole stem set through
            // RestoreStemsAfterEngineRebuild -> LoadStemsCore, so both come back
            // with the stems they belong to. Replaying them in
            // ReapplyMasterBusStateLocked instead would write a bus that does not
            // exist yet on a fresh engine.
            ["StemGroupTempo"] = "re-derived by LoadStemsCore, which the rebuild re-issues",
            ["StemGroupPitchSemitones"] = "re-derived by LoadStemsCore, which the rebuild re-issues",
            // ABI v37 re-amp: a take's loop flag travels INSIDE the load — PushTakeTo
            // passes take.Loop to SetInputInjection, and the live SetTakeLoop records
            // it on the take first — so the replay restores it without this setter.
            ["SetInputInjectionLoop"] = "carried by SetInputInjection's loop argument in PushTakeTo",
            // ABI v38: the song clock follows the SONG SOURCE, not the engine — it is
            // bound by BindSongClock wherever a source is published (SwapMasterSource
            // and the rebuild's re-open), which TheSongClock_IsBoundWhereverTheSongIsPublished pins.
            ["SetSongClockSource"] = "re-bound by BindSongClock wherever the song source is published",
        };

        var missing = EngineStateWritesIn(source, surface)
            .Where(name => !replayed.Contains(name) && !exempt.ContainsKey(name))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.True(missing.Count == 0,
            "These engine setters are engine STATE that no rebuild replays, so a device change / NAM 48 kHz " +
            "request / watchdog recovery drops them silently: " + string.Join(", ", missing) +
            ". Replay them in ReapplyMasterBusStateLocked (master bus) or AttachPerPlayerStateToEngine " +
            "(per seat), or add an exemption naming what re-derives them.");
    }

    /// <summary>
    ///     An exemption that names nothing is the list defeating itself, and a
    ///     STALE one is worse — it keeps excusing a member the service no longer
    ///     writes, so the day something re-introduces that write it is excused on
    ///     sight. Both halves are checked here.
    /// </summary>
    /// <summary>
    ///     The re-amp take (ABI v37) comes back with the rebuilt engine: the
    ///     per-seat attach calls the replay, and the replay writes through the
    ///     same <c>PushTakeTo</c> a live load does — which is what lets the test
    ///     above list <c>PushTakeTo</c> as replayed.
    /// </summary>
    [Fact]
    public void TheReampTake_IsReplayedByThePerPlayerAttach()
    {
        // Comments stripped: a commented-out call must not count as the call.
        var source = Regex.Replace(ServiceSource(), @"//[^\n]*", " ");
        Assert.Contains("_reampTakes.ReplayTakesTo(engine);",
            Body(source, "private void AttachPerPlayerStateToEngine(JuceAudioEngine engine)"), StringComparison.Ordinal);
        Assert.Contains("PushTakeTo(engine, player, take)",
            Body(source, "public void ReplayTakesTo(JuceAudioEngine engine)"), StringComparison.Ordinal);
        var push = Body(source, "private bool PushTakeTo(JuceAudioEngine engine, int player, ReampTake take)");
        Assert.Contains(".SetInputInjection(", push, StringComparison.Ordinal);
        Assert.Contains(".SetInputInjectionPlaying(", push, StringComparison.Ordinal);
        Assert.Contains(".SeekInputInjection(", push, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The external process insert (ABI v47) comes back with a REPLACED engine: the
    ///     per-seat attach calls the replay, and the replay re-opens a recorded seat the
    ///     engine reports closed (a reconfigured engine keeps its own session).
    /// </summary>
    [Fact]
    public void TheExternalProcessInsert_IsReplayedByThePerPlayerAttach()
    {
        var source = Regex.Replace(ServiceSource(), @"//[^\n]*", " ");
        Assert.Contains("_externalInserts.ReplayTo(new ExternalInsert.JuceExternalInsertEngine(engine));",
            Body(source, "private void AttachPerPlayerStateToEngine(JuceAudioEngine engine)"), StringComparison.Ordinal);
        var replay = Body(source, "public void ReplayTo(IExternalInsertEngine engine)");
        Assert.Contains("engine.Open(player, request.MappingName, request.Magic)", replay, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The chart synth (ABI v38) comes back with the rebuilt engine: the per-seat
    ///     attach calls the replay, the replay writes through the same
    ///     <c>ChartSynthEngineDriver.PushTo</c> a live change does, and that one helper writes
    ///     every piece of chart-source state — notes, voice, params, enabled, route,
    ///     and (#50) both instrument selections.
    /// </summary>
    [Fact]
    public void TheChartSynth_IsReplayedByThePerPlayerAttach()
    {
        var source = Regex.Replace(ServiceSource(), @"//[^\n]*", " ");
        Assert.Contains("_chartSynth.ReplayTo(engine);",
            Body(source, "private void AttachPerPlayerStateToEngine(JuceAudioEngine engine)"), StringComparison.Ordinal);
        // The replay re-uploads the notes (a live change does not: it would choke them).
        Assert.Contains("includeNotes: true",
            Body(source, "public void ReplayTo(JuceAudioEngine engine)"), StringComparison.Ordinal);
        var push = Body(source, "private void PushTo(JuceAudioEngine engine, string reason, bool includeNotes)");
        foreach (var write in new[] { ".SetChartNotes(", ".TrySetChartVoice(", ".SetChartSourceParams(",
                     ".SetChartSourceEnabled(", ".SetInstrumentRoute(", ".TrySetChartInstrument(" })
        {
            Assert.Contains(write, push, StringComparison.Ordinal);
        }
    }

    /// <summary>
    ///     #66: the chart synth's instrument follows the player profile and the
    ///     playing song tone, so the service's own request is what a rebuild
    ///     replays. A config apply — startup, the settings dialog and the
    ///     watchdog's recovery rebuild all run <c>ApplyAudioConfigurationCore</c> —
    ///     must not read the app-wide instrument fields back, or every rebuild
    ///     would swap the profile's (or the tone's) instrument for the legacy one.
    /// </summary>
    [Fact]
    public void AConfigApply_NeverResetsTheChartSynthInstrument()
    {
        var source = Regex.Replace(ServiceSource(), @"//[^\n]*", " ");
        var apply = Body(source, "private void ApplyAudioConfigurationCore(RigConfig config, bool forceEngineReplace)");
        Assert.True(apply.Length > 500, "ApplyAudioConfigurationCore not found — the lint would pass vacuously");
        foreach (var read in new[] { "ChartSynthInstrumentSelection", "ChartSynthGuitar", "ChartSynthBass", "_guitarInstrument", "_bassInstrument" })
            Assert.DoesNotContain(read, apply, StringComparison.Ordinal);
        foreach (var read in new[] { "ChartSynthInstrumentSelection.FromConfig", "config.ChartSynthGuitar", "config.ChartSynthBass" })
            Assert.DoesNotContain(read, source, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The song clock is bound to the SOURCE: every place the master source is
    ///     published binds it — a load and the rebuild's re-open — so a device change
    ///     cannot leave the chart synth following nothing.
    /// </summary>
    [Fact]
    public void TheSongClock_IsBoundWhereverTheSongIsPublished()
    {
        var source = Regex.Replace(ServiceSource(), @"//[^\n]*", " ");
        var publishes = Regex.Matches(source, @"_source = (\w+);").Select(m => m.Groups[1].Value)
            .Where(v => v != "null").ToList();
        Assert.True(publishes.Count >= 2, $"expected the load and the rebuild re-open to publish _source, found {publishes.Count}");
        // Within the three lines that follow the publish.
        foreach (var local in publishes)
        {
            Assert.Matches(@"_source = " + local + @";[^\n]*\n(?:[^\n]*\n){0,3}?[^\n]*BindSongClock\(\w+, " + local + @"\);", source);
        }
        Assert.Contains(".SetSongClockSource(", Body(source, "private void BindSongClock(JuceAudioEngine engine, JuceAudioSource song)"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void TheReplayExemptions_AreAllStillEngineStateTheServiceWrites()
    {
        var surface = EngineStateWriterNames();
        var used = EngineStateWritesIn(ServiceSource(), surface).ToHashSet(StringComparer.Ordinal);

        foreach (var name in new[]
                 {
                     "SetRemoteBridge", "SetStretchQuality",
                     "StemGroupTempo", "StemGroupPitchSemitones",
                     "SetInputInjectionLoop", "SetSongClockSource",
                 })
        {
            Assert.True(surface.Contains(name),
                $"'{name}' is exempted from the rebuild replay but is no longer an engine-state writer on " +
                "JuceAudioEngine. Delete the exemption.");
            Assert.True(used.Contains(name),
                $"'{name}' is exempted from the rebuild replay but the service no longer writes it. " +
                "Delete the exemption rather than leaving a standing excuse for the next author.");
        }
    }

    /// <summary>
    ///     A failed bring-up must DESTROY the engine it managed to create. Dropping
    ///     the managed reference does not close the device: the native engine stays
    ///     alive holding it, and an ASIO device is typically exclusive — so every
    ///     later rebuild, including the watchdog's own recovery, then fails to
    ///     reopen it for the rest of the process.
    /// </summary>
    [Fact]
    public void AFailedRebuild_DestroysTheEngineItManagedToCreate()
    {
        var body = Body(ServiceSource(), "private void ReplaceEngineGated(JuceEngineConfig cfg)");

        var catchAt = body.IndexOf("catch (Exception ex)", StringComparison.Ordinal);
        Assert.True(catchAt >= 0, "ReplaceEngineGated must still catch a failed bring-up.");
        var failurePath = body[catchAt..];

        Assert.Contains("createdEngine.Dispose()", failurePath, StringComparison.Ordinal);
        // …and the ordering rule the whole file turns on: the DSP rigs come off
        // the handle BEFORE the engine behind it dies.
        var detach = failurePath.IndexOf("AttachDspChainsToEngine(null)", StringComparison.Ordinal);
        var dispose = failurePath.IndexOf("createdEngine.Dispose()", StringComparison.Ordinal);
        Assert.True(detach >= 0 && detach < dispose,
            "The DSP rigs must be detached before the half-built engine is destroyed.");
    }

    /// <summary>
    ///     Losing one setting must cost the user that setting, never the engine.
    ///     The master-bus writes used to be bare calls inside the rebuild's one big
    ///     try, so one throwing setter was reported as "Failed to start JUCE
    ///     engine" and took the whole device down with it.
    /// </summary>
    [Fact]
    public void TheMasterBusReplay_CannotAbortTheRebuild()
    {
        var source = ServiceSource();

        // Every master-bus write lives in the helper, not in the rebuild body.
        var rebuild = Body(source, "private void ReplaceEngineGated(JuceEngineConfig cfg)");
        Assert.Empty(EngineStateWritesIn(rebuild, EngineStateWriterNames()));
        Assert.Contains("ReapplyMasterBusStateLocked(engine);", rebuild, StringComparison.Ordinal);

        // …and every write in the helper is INSIDE a Reapply(...) call. Blanking
        // out each guarded region must leave no engine write behind — that is the
        // difference between "there is a guard" and "everything uses it".
        var helper = Body(source, "private void ReapplyMasterBusStateLocked(JuceAudioEngine engine)");
        Assert.True(Regex.Matches(helper, @"Reapply\(""").Count >= 5,
            "The master-bus replay lost its guarded groups.");

        var unguarded = BlankOutGuardedRegions(helper);
        Assert.DoesNotContain("engine.Set", unguarded, StringComparison.Ordinal);
        Assert.DoesNotContain("PushEqTo(", unguarded, StringComparison.Ordinal);

        // The guard itself must swallow and log; a rethrow would put the whole
        // failure straight back into the rebuild's catch.
        Assert.Contains("catch (Exception ex)", helper, StringComparison.Ordinal);
        Assert.DoesNotContain("throw", helper, StringComparison.Ordinal);
    }

    // ----- helpers -----------------------------------------------------------

    /// <summary>Replaces every <c>Reapply(…)</c> argument list with whitespace, so
    /// what is left is exactly the code that is NOT behind the guard.</summary>
    private static string BlankOutGuardedRegions(string helper)
    {
        var buffer = helper.ToCharArray();
        foreach (Match m in Regex.Matches(helper, @"Reapply\("))
        {
            var open = m.Index + m.Length - 1;
            var depth = 0;
            for (var i = open; i < buffer.Length; i++)
            {
                if (buffer[i] == '(')
                {
                    depth++;
                }
                else if (buffer[i] == ')' && --depth == 0) { break; }

                if (buffer[i] != '\n') buffer[i] = ' ';
            }
        }

        return new string(buffer);
    }

    /// <summary>
    ///     Every member of <see cref="JuceAudioEngine" /> that WRITES engine state:
    ///     the <c>Set*</c> methods and the settable properties. Derived from the
    ///     type so a new ABI wrapper member is in the candidate set the day it is
    ///     written, whatever the call site chooses to call its local.
    ///     <para>
    ///         <c>SetDeviceType</c> is excluded on purpose and is the one member
    ///         that has to be: it is part of BUILDING an engine (it belongs to
    ///         <c>JuceEngineConfig</c> / <c>CreateEngineWithFallback</c>), not
    ///         state layered on top of one, so a rebuild re-establishes it by
    ///         definition.
    ///     </para>
    /// </summary>
    private static IReadOnlySet<string> EngineStateWriterNames()
    {
        const BindingFlags publicDeclared = BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly;

        var names = typeof(JuceAudioEngine)
            .GetMethods(publicDeclared)
            .Where(m => !m.IsSpecialName && m.Name.StartsWith("Set", StringComparison.Ordinal))
            .Select(m => m.Name)
            .Concat(typeof(JuceAudioEngine)
                .GetProperties(publicDeclared)
                .Where(p => p.CanWrite && p.GetIndexParameters().Length == 0)
                .Select(p => p.Name))
            .Where(n => !string.Equals(n, nameof(JuceAudioEngine.SetDeviceType), StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);

        return names;
    }

    /// <summary>
    ///     Which of <paramref name="surface" /> the given source WRITES — a call
    ///     (<c>.SetX(</c>) or an assignment (<c>.X =</c>, never <c>.X ==</c>).
    ///     Receiver-agnostic on purpose: the name is what identifies the ABI
    ///     write, and pinning the receiver is precisely how the earlier scan went
    ///     blind to the two stem-group properties.
    /// </summary>
    private static IEnumerable<string> EngineStateWritesIn(string source, IReadOnlySet<string> surface) =>
        surface
            .Where(name => Regex.IsMatch(source, $@"\.{Regex.Escape(name)}\s*(\(|=[^=])"))
            .Distinct(StringComparer.Ordinal);

    /// <summary>The whole partial type — the setters are spread over its files.</summary>
    private static string ServiceSource()
    {
        var files = PlaybackServiceSourceFiles.All();
        Assert.NotEmpty(files);
        return string.Join("\n", files.Select(File.ReadAllText));
    }

    private static string Body(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Could not find '{signature}' — has it been renamed?");
        var open = source.IndexOf('{', start + signature.Length);
        var depth = 0;
        var i = open;
        for (; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) break;
        }

        return source[open..Math.Min(i + 1, source.Length)];
    }
}
