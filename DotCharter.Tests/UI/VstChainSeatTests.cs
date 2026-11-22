using DotCharter.Tone.Audio.Vst;
using DotCharter.Tone.UI.ViewModels.Editor;

namespace DotCharter.Tests.UI;

/// <summary>
///     <c>VstChainEntry.Player</c> is the co-op seat a guitar VST slot processes
///     (ABI v28). The service layer carries it everywhere and says why in its own
///     snapshot code: <i>"Dropping it here would silently re-seat every
///     per-player slot onto player 1 the moment anything re-applied the chain —
///     the same shape of bug as a model field with no writer."</i>
///     <para>
///         The UI did exactly that, three times: the persisted-chain read, the
///         persist-back and the engine push all built fresh entries WITHOUT
///         <c>Player</c>, and the slot view model had no such member at all. So a
///         persisted <c>"Player": 1</c> was destroyed on the first launch that
///         read it and could never come back.
///     </para>
///     <para>
///         Carrying the field was the fix; AUTHORING it shipped in 0.58 — see
///         <see cref="VstChainSeatPickerTests" /> for the picker. The two halves
///         stay separate tests on purpose: the carry rule below must hold for
///         every copy site whether or not anything can set the seat, and it is
///         the one that silently destroys data when it breaks.
///     </para>
/// </summary>
public sealed class VstChainSeatTests
{
    [Fact]
    public void TheSlotViewModelCarriesItsSeat()
    {
        var slot = new VstChainSlotViewModel(Guid.NewGuid(), "Amp", @"C:\vst3\Amp.vst3", bypass: false, player: 1);

        Assert.Equal(1, slot.Player);
        // …and the default is the single-player chain, so nothing that predates
        // per-player chains changes behaviour.
        Assert.Equal(0, new VstChainSlotViewModel(Guid.NewGuid(), "Amp", "p", false).Player);
        Assert.Equal(0, new VstChainEntry().Player);
    }

    /// <summary>
    ///     Every place a <c>VstChainEntry</c> is BUILT must name <c>Player</c> —
    ///     including the ones that mean seat 0, because "0 because I decided so"
    ///     and "0 because I forgot" are indistinguishable afterwards and only one
    ///     of them is correct. This is the cheap guard that catches the next copy
    ///     site; there are eleven of them today across the UI and the service.
    /// </summary>
    [Fact]
    public void EveryVstChainEntryInitializer_NamesThePlayerSeat()
    {
        var offenders = new List<string>();
        var found = 0;

        foreach (var file in ProductSources())
        {
            var code = UiSourceTree.ReadAllText(file);
            foreach (var marker in new[] { "new VstChainEntry", "new List<VstChainEntry>" })
            {
                var at = code.IndexOf(marker, StringComparison.Ordinal);
                while (at >= 0)
                {
                    var body = InitializerAfter(code, at);
                    if (body != null)
                    {
                        found++;
                        if (!body.Contains("Player", StringComparison.Ordinal))
                            offenders.Add($"{Path.GetFileName(file)} @ char {at}: {Summarize(body)}");
                    }

                    at = code.IndexOf(marker, at + marker.Length, StringComparison.Ordinal);
                }
            }
        }

        // A floor, so a rename or a moved file cannot make this pass by finding
        // nothing to check.
        Assert.True(found >= 10, $"only {found} VstChainEntry initializers found — the sweep went blind");
        Assert.True(offenders.Count == 0,
            "these VstChainEntry initializers drop the co-op seat, which silently re-seats the slot onto " +
            "player 1 the moment anything re-applies the chain:\n  " + string.Join("\n  ", offenders));
    }

    /// <summary>The three UI copy sites by name, because they are the round trip
    /// that DESTROYED the field rather than merely defaulting it: read from
    /// config → held in the view model → written back and pushed.</summary>
    [Fact]
    public void TheUiChainRoundTripCarriesTheSeatAtEveryHop()
    {
        var vst = Read("DotCharter.UI", "ViewModels", "Editor", "TransportViewModel.Vst.cs");

        // config → VstChainEntry, VstChainEntry → slot VM, slot VM → config,
        // slot VM → engine.
        Assert.Contains("Player = e.Player", vst);
        Assert.Equal(2, Occurrences(vst, "Player = s.Player"));
        Assert.Contains("entry.PluginPath, entry.Bypass, entry.Player", vst);
    }

    /// <summary>
    ///     <c>JuceAudioPlugin</c> learns its seat from <c>AttachToInput</c> and
    ///     from nothing else — the field behind it is private and its only writer
    ///     is that method. So a slot LOADED while bypassed (the load path sets
    ///     <c>Bypass</c> and never attaches) or RE-SEATED while bypassed (a
    ///     detached plugin cannot be re-attached without un-bypassing it) still
    ///     believes it is on seat 0, and the <c>Bypass</c> setter's own
    ///     re-attach then puts player 2's amp sim on player 1's input chain:
    ///     player 1 hears a doubled chain, player 2 hears nothing, and the next
    ///     diff never corrects it because <c>_guitarSlotPlayer</c> already
    ///     records the right seat.
    ///     <para>
    ///         The engine-replace rehydration carries <c>Player</c> faithfully
    ///         and re-enters the same load path, so a device change re-created
    ///         the fault every time.
    ///     </para>
    ///     <para>
    ///         Both un-bypass paths — the chain diff and the Bypass BUTTON —
    ///         must therefore say the seat before clearing the flag. Source-level
    ///         because <c>JuceAudioPlugin</c> is a sealed native wrapper with no
    ///         seam, and a behavioural test would have to open an audio device.
    ///     </para>
    /// </summary>
    [Fact]
    public void UnBypassingAGuitarVstSlot_AlwaysSaysItsSeatFirst()
    {
        var chain = Read("DotCharter.Tone", "Audio", "JuceAudioPlaybackService.VstChain.cs");

        // Existing slots record their requested seat and bypass before the
        // unlocked ordering pass applies both together.
        Assert.Contains("_guitarSlotPlayer[entry.Id] = entry.Player;", chain);
        Assert.Contains("_guitarSlotBypass[entry.Id] = entry.Bypass;", chain);
        var applyPhase = Between(chain, "private void ApplyGuitarVstChainCore", "private void ReapplyGuitarInputOrder");
        Assert.Contains("ReapplyGuitarInputOrder();", applyPhase);

        // The Bypass button uses the same ordered reattachment as a chain diff.
        var button = Between(chain, "public void SetGuitarVstSlotBypass", "public void LoadInstrumentVst");
        Assert.Contains("ReapplyGuitarInputOrder();", button);
        var order = Between(chain, "private void ReapplyGuitarInputOrder", "public void ToggleGuitarVstSlotEditor");
        Assert.Contains("_guitarSlotPlayer.GetValueOrDefault(id)", order);
        Assert.Contains("plugin.AttachToInput(player);", order);

        Assert.Contains("_guitarSlotBypass.GetValueOrDefault(id)", order);
    }

    // ---- helpers ------------------------------------------------------------

    private static string Between(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"marker not found: {startMarker}");
        var end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        Assert.True(end > start, $"end marker not found after {startMarker}: {endMarker}");
        return source[start..end];
    }

    /// <summary>The brace-balanced object/collection initializer that follows
    /// <paramref name="from" />, or null when the construction has none.</summary>
    private static string? InitializerAfter(string code, int from)
    {
        var open = code.IndexOf('{', from);
        if (open < 0) return null;
        // Anything other than whitespace, a newline or a type argument between
        // the `new` and the brace means this is not an initializer.
        var between = code[(from)..open];
        if (between.Contains(';') || between.Contains('(')) return null;

        var depth = 0;
        for (var i = open; i < code.Length; i++)
        {
            if (code[i] == '{') depth++;
            else if (code[i] == '}' && --depth == 0) return code[open..(i + 1)];
        }

        return null;
    }

    private static int Occurrences(string haystack, string needle)
    {
        var count = 0;
        var at = haystack.IndexOf(needle, StringComparison.Ordinal);
        while (at >= 0)
        {
            count++;
            at = haystack.IndexOf(needle, at + needle.Length, StringComparison.Ordinal);
        }

        return count;
    }

    private static string Summarize(string body)
        => string.Join(' ', body.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()));

    private static IEnumerable<string> ProductSources()
    {
        foreach (var project in new[] { "DotCharter.UI", "DotCharter.Services", "DotCharter.Tone" })
        {
            var root = Path.Combine(TestPaths.RepoRoot(), project);
            foreach (var file in UiSourceTree.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(root, file);
                if (relative.StartsWith("bin", StringComparison.OrdinalIgnoreCase) ||
                    relative.StartsWith("obj", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                yield return file;
            }
        }
    }

    private static string Read(params string[] relativeParts)
    {
        var path = Path.Combine(new[] { TestPaths.RepoRoot() }.Concat(relativeParts).ToArray());
        Assert.True(File.Exists(path), $"source file not found at {path}");
        return UiSourceTree.ReadAllText(path);
    }
}
