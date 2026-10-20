using System.Text.RegularExpressions;

namespace DotCharter.Tests.Services.Audio;

/// <summary>
///     The app owns its slots' BYPASS, and never asks the plugin for it.
///     <para>
///         Since the wave-4 wrapper audit <c>JuceAudioPlugin.Bypass</c> is no
///         longer a cached flag: it asks the engine (<c>InputPlayer &lt; 0</c>),
///         so it means "not in any seat's input chain". A plugin that has been
///         LOADED but never attached — an instrument, a slot mid-load, one whose
///         attach threw — therefore reads <c>true</c>. The service used that
///         property as the app's INTENT at four sites and each one turned the new
///         reading into a real fault:
///     </para>
///     <list type="bullet">
///         <item>the engine-rebuild snapshot (<c>.EngineLifecycle.cs</c>) and the
///         sandbox mode-switch snapshot (<c>.Sandbox.cs</c>) wrote
///         <c>Bypass = true</c> into the rehydration DTO, so an ACTIVE plugin came
///         back bypassed after a device change — the replay-on-rebuild shape the
///         root AGENTS.md calls out, and invisible because the UI kept drawing the
///         slot lit;</item>
///         <item>the chain differ read it twice (<c>.VstChain.cs</c>): a needed
///         re-seat was SKIPPED, and a phantom bypass op was emitted for a toggle
///         nobody asked for.</item>
///     </list>
///     <para>
///         A source lint because the fault needs a real VST3, a real engine and a
///         real device change to reach, while the invariant is structural: the
///         intent lives in <c>_guitarSlotBypass</c>, the twin of
///         <c>_guitarSlotPlayer</c>, which is kept managed-side for this exact
///         reason and has been since ABI v28.
///     </para>
/// </summary>
public sealed class VstChainBypassIntentTests
{
    /// <summary>EVERY partial of the service, derived rather than listed — the
    /// class is split across a dozen files and a hand list stops looking at
    /// whichever partial a snapshot moves into next.</summary>
    private static readonly string[] ServiceFiles =
        PlaybackServiceSourceFiles.All();

    /// <summary>Anti-vacuity for the sweep below: the derived file set must be
    /// non-trivial, or every assertion here passes by finding nothing.</summary>
    [Fact]
    public void TheServicePartials_AreDiscovered()
    {
        Assert.True(ServiceFiles.Length >= 5,
            $"Only {ServiceFiles.Length} JuceAudioPlaybackService partial(s) found; the glob has gone stale.");
        Assert.Contains(ServiceFiles, f => f.EndsWith("JuceAudioPlaybackService.VstChain.cs", StringComparison.Ordinal));
        Assert.Contains(ServiceFiles, f => f.EndsWith("JuceAudioPlaybackService.EngineLifecycle.cs", StringComparison.Ordinal));
        Assert.Contains(ServiceFiles, f => f.EndsWith("JuceAudioPlaybackService.Sandbox.cs", StringComparison.Ordinal));
    }

    /// <summary>
    ///     Nothing in the service READS <c>.Bypass</c> off a plugin. Assignments
    ///     (<c>plugin.Bypass = …</c>) are the WRITE side and are the sanctioned
    ///     way to drive the native attach/detach, so they are excluded — the same
    ///     split <c>PlaybackGateHoldGuardTests</c> makes from the other direction.
    /// </summary>
    [Fact]
    public void NoServiceCodeReadsBypassOffAPlugin()
    {
        // `x.Bypass` not followed by an `=` that is not `==`/`!=`. The app's own
        // DTO field (`entry.Bypass`, `e.Bypass`, `desired.Bypass`) and the
        // sandbox's own record (`live.Bypass`) are app-owned state, not the
        // wrapper's property, so they are named explicitly rather than guessed at.
        // Deliberately SHORT. The rebuild snapshot's tuple field is named
        // BypassIntent rather than Bypass precisely so it does not need to be on
        // this list — a name that reads as the app's intent cannot be confused
        // with the wrapper's property by the next reader either.
        var appOwned = new[] { "entry", "e", "desired", "live" };
        var offenders = new List<string>();

        foreach (var file in ServiceFiles)
        {
            var lines = SourceText.StripComments(File.ReadAllText(file)).Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                foreach (Match m in Regex.Matches(lines[i], @"(?<recv>[A-Za-z_][A-Za-z0-9_\.]*)\.Bypass\b(?!\s*=[^=])"))
                {
                    var receiver = m.Groups["recv"].Value;
                    var head = receiver.Split('.')[0];
                    if (appOwned.Contains(head, StringComparer.Ordinal)) continue;
                    offenders.Add($"{Path.GetFileName(file)}:{i + 1}: {receiver}.Bypass");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "The service must read its slots' bypass from _guitarSlotBypass, never from a plugin:\n  "
            + string.Join("\n  ", offenders));
    }

    /// <summary>
    ///     The intent dictionary is MAINTAINED, not merely declared. A field
    ///     nothing writes is a dead feature that reads as a fix — the shape this
    ///     repo has shipped more than once — so every place the seat map is
    ///     cleared, removed from or assigned must move the bypass map too.
    /// </summary>
    [Fact]
    public void TheBypassIntentMap_IsMaintainedWhereverTheSeatMapIs()
    {
        var text = string.Concat(ServiceFiles.Select(f => SourceText.StripComments(File.ReadAllText(f))));

        foreach (var op in new[] { ".Clear();", ".Remove(" })
        {
            var seats = Regex.Matches(text, Regex.Escape("_guitarSlotPlayer") + Regex.Escape(op)).Count;
            var bypasses = Regex.Matches(text, Regex.Escape("_guitarSlotBypass") + Regex.Escape(op)).Count;
            Assert.True(seats > 0, $"No _guitarSlotPlayer{op} site found — this lint has gone blind.");
            Assert.True(bypasses >= seats,
                $"_guitarSlotPlayer{op} appears {seats}× but _guitarSlotBypass{op} only {bypasses}× — "
                + "a slot's bypass intent outlives the slot.");
        }

        var seatWrites = Regex.Matches(text, @"_guitarSlotPlayer\[[^\]]+\]\s*=").Count;
        var bypassWrites = Regex.Matches(text, @"_guitarSlotBypass\[[^\]]+\]\s*=").Count;
        Assert.True(seatWrites > 0, "No _guitarSlotPlayer[...] assignment found — this lint has gone blind.");
        Assert.True(bypassWrites >= seatWrites,
            $"_guitarSlotPlayer is assigned {seatWrites}× but _guitarSlotBypass only {bypassWrites}× — "
            + "some path decides a slot's seat without recording its bypass.");
    }

    /// <summary>Both rehydration snapshots carry the RECORDED intent. Named
    /// individually because these two are the ones that shipped the fault.</summary>
    [Fact]
    public void BothRehydrationSnapshots_CarryTheRecordedIntent()
    {
        foreach (var name in new[] { "EngineLifecycle", "Sandbox" })
        {
            var file = ServiceFiles.Single(f =>
                f.EndsWith($"JuceAudioPlaybackService.{name}.cs", StringComparison.Ordinal));
            var text = SourceText.StripComments(File.ReadAllText(file));
            Assert.Contains("_guitarSlotBypass", text, StringComparison.Ordinal);
        }
    }
}
