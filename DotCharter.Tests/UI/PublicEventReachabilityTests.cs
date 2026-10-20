using System.Text.RegularExpressions;

namespace DotCharter.Tests.UI;

/// <summary>
///     A <c>public event</c> that nothing ever writes <c>+=</c> against is a
///     notification with no listener: the raise site runs, the null-conditional
///     invoke does nothing, and the declaration reads as a wired extension point
///     for as long as nobody checks.
///
///     <para>
///         This is the event-shaped twin of <see cref="RelayCommandReachabilityTests" />,
///         written for the same reason: the hand-rolled per-feature guards
///         (<c>CoopReachabilityTests</c>, <c>PowerToolReachabilityTests</c>,
///         <c>TonePreviewReachabilityTests</c>) each caught ONE instance of a
///         general shape. The 0.60 sweep found five dead events at once, and two
///         of them were reported as bugs by hand months apart:
///         <c>ITrackContextRegistry.FocusedTrackChanged</c> (focus moved without
///         <c>ActiveArrangement</c>, so View ▸ Timeline Rows went on writing the
///         previous track's ribbon) and <c>MultiplayerViewModel.SongInstalled</c>
///         (whose SIBLING hooks are all wired, which is exactly why it read as
///         wired too). Both were invisible to every existing lint.
///     </para>
///     <para>
///         <b>It is a source lint, deliberately.</b> Reflection would happily hand
///         back an <c>EventInfo</c> for an event with no subscribers — that is the
///         state being looked for. What matters is whether a human-authored
///         PRODUCT file subscribes. Tests are not scanned: an event whose only
///         <c>+=</c> is in <c>DotCharter.Tests</c> is dead in the product, and
///         <c>TuningDriftWatchdog.VerdictChanged</c> is exactly that.
///     </para>
///     <para>
///         <b>It matches on the bare NAME, so it under-reports</b> — the same
///         caveat as the command lint. A dead <c>Changed</c> on one service is
///         satisfied by a live <c>Changed</c> on another. It therefore proves an
///         event IS dead, never that one is alive.
///     </para>
/// </summary>
public sealed class PublicEventReachabilityTests
{
    /// <summary>
    ///     Events nothing subscribes to TODAY, anywhere in the product. **Do not
    ///     add to this list to make a new event pass** — that is the failure mode
    ///     it exists to stop. Remove an entry by wiring the event up or by
    ///     deleting it. The list only ever shrinks.
    ///     <para>
    ///         All three below are the SAME shape, and it is worth naming because
    ///         it is the one that survives review: <b>a consumer that POLLS the
    ///         state the event announces.</b> That is not a reason to keep the
    ///         event — it is a second source of truth that can disagree with the
    ///         first — but it does mean deleting one is a behaviour question for
    ///         whoever owns the surface, not a cleanup.
    ///     </para>
    ///     <para>
    ///         <c>Failed</c> is <c>RockMeter</c>'s, raised exactly once when the
    ///         meter latches. <c>LivePlayViewModel.Meters</c> hosts the meter and
    ///         instead reads <c>HasFailed</c> on its own pump, raising its own
    ///         <c>RockMeterFailed</c> (which IS subscribed). <c>VerdictChanged</c>
    ///         is <c>TuningDriftWatchdog</c>'s, whose only live holder
    ///         (<c>LiveTuningDriftMonitor</c>'s <c>_drift</c>) likewise reads the
    ///         verdict rather than subscribing; its only <c>+=</c> is in its own
    ///         tests. (<c>ProfileRequested</c> was the third until v0.60 deleted
    ///         <c>OnlineHubViewModel</c> with the online backend — the list only
    ///         ever shrinks, and that was a shrink.)
    ///     </para>
    /// </summary>
    private static readonly HashSet<string> KnownUnsubscribed = new(StringComparer.Ordinal)
    {
        // "Failed" (RockMeter) came off this list the day LivePlayViewModel.Meters
        // started subscribing it — the list only ever shrinks, and this is the shrink.
        "VerdictChanged"
    };

    /// <summary>
    ///     The lenient half: SOMETHING in the product subscribes. An event's own
    ///     declaration can never satisfy this (a declaration is not a <c>+=</c>),
    ///     so there is no self-reference escape hatch to exclude.
    /// </summary>
    [Fact]
    public void EveryPublicEvent_IsSubscribedByProductCode()
    {
        var texts = ProductTexts();

        var dead = new List<string>();
        var deadNames = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (name, declaredIn) in EventsByName(texts))
        {
            if (texts.Any(kv => Subscribes(kv.Value, name))) continue;
            dead.Add($"{name}  ({FileList(declaredIn)})");
            deadNames.Add(name);
        }

        var fresh = dead.Where(d => !KnownUnsubscribed.Contains(Name(d))).ToList();
        Assert.True(fresh.Count == 0,
            "These public events are raised but NOTHING in DotCharter.UI, DotCharter.Services or " +
            "DotCharter.Presentation ever " +
            "writes `+=` against them — a notification with no listener, which reads as a wired " +
            "extension point until someone checks:\n  " + string.Join("\n  ", fresh) +
            "\n\nSubscribe from the surface that should react, or delete the event. Do NOT add it to " +
            nameof(KnownUnsubscribed) + " — that list only ever shrinks.");

        var stale = KnownUnsubscribed.Where(k => !deadNames.Contains(k)).ToList();
        Assert.True(stale.Count == 0,
            "These are listed as unsubscribed but something subscribes now — delete them from " +
            nameof(KnownUnsubscribed) + ":\n  " + string.Join("\n  ", stale));
    }

    /// <summary>
    ///     Events whose ONLY subscriber lives in the very file that declares them.
    ///     Every entry here is the same legitimate shape — an item view model or a
    ///     transport abstraction declared beside the single owner that consumes
    ///     it — and each was checked by hand:
    ///     <list type="bullet">
    ///         <item><c>RequestRefresh</c>: <c>MultiTrackEntry</c>'s, consumed by
    ///         <c>MultiTrackOverlayViewModel.RebuildEntries</c> in the same
    ///         file.</item>
    ///     </list>
    ///     <para>
    ///         <b>This list only ever shrinks.</b> A NEW same-file-only event is a
    ///         question worth asking (is the consumer really the declaring file,
    ///         or did someone forget the surface?), which is why it fails here
    ///         instead of passing silently.
    ///     </para>
    /// </summary>
    private static readonly HashSet<string> KnownSubscribedOnlyInItsOwnFile = new(StringComparer.Ordinal)
    {
        "RequestRefresh"
    };

    /// <summary>
    ///     The strict half. A cross-FILE subscriber is what proves an event is a
    ///     real seam rather than an in-file callback that could have been a
    ///     method call — and it is the rule that would have caught 0.60's two
    ///     wired-looking dead events on the day they landed.
    /// </summary>
    [Fact]
    public void EveryPublicEvent_IsSubscribedOutsideTheFileThatDeclaresIt()
    {
        var texts = ProductTexts();

        var inFileOnly = new List<string>();
        var inFileOnlyNames = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (name, declaredIn) in EventsByName(texts))
        {
            // Already reported as fully dead by the lenient fact — do not
            // double-list it here.
            if (KnownUnsubscribed.Contains(name)) continue;

            var subscribedElsewhere = texts.Any(kv =>
                !declaredIn.Contains(kv.Key, StringComparer.OrdinalIgnoreCase) &&
                Subscribes(kv.Value, name));
            if (subscribedElsewhere) continue;

            inFileOnly.Add($"{name}  ({FileList(declaredIn)})");
            inFileOnlyNames.Add(name);
        }

        var fresh = inFileOnly
            .Where(e => !KnownSubscribedOnlyInItsOwnFile.Contains(Name(e)))
            .ToList();
        Assert.True(fresh.Count == 0,
            "These public events are subscribed ONLY inside the file that declares them. That is " +
            "sometimes right (an item view model consumed by the owner declared beside it) — but it " +
            "is also what a forgotten surface looks like, so it is a decision, not a default:\n  " +
            string.Join("\n  ", fresh) +
            "\n\nEither subscribe from the surface that should react, make it a plain method call, or " +
            "add it to " + nameof(KnownSubscribedOnlyInItsOwnFile) + " WITH the evidence.");

        var stale = KnownSubscribedOnlyInItsOwnFile.Where(k => !inFileOnlyNames.Contains(k)).ToList();
        Assert.True(stale.Count == 0,
            "These are listed as same-file-only but are subscribed across files now — delete them " +
            "from " + nameof(KnownSubscribedOnlyInItsOwnFile) + ":\n  " + string.Join("\n  ", stale));
    }

    // ---- mechanics -----------------------------------------------------------

    private static string Name(string entry) => entry.Split("  ", StringSplitOptions.None)[0];

    private static string FileList(IReadOnlyCollection<string> paths) =>
        string.Join(", ", paths.Select(p => Path.GetFileName(p) ?? p)
            .Distinct(StringComparer.OrdinalIgnoreCase));

    private static bool Subscribes(string text, string eventName) =>
        Regex.IsMatch(text, $@"\b{Regex.Escape(eventName)}\s*\+=");

    /// <summary>Event name → every product file that declares it (an interface
    /// and its implementation are the common pair; a name declared on two
    /// unrelated types collapses here, which is the bare-name under-report the
    /// summary warns about).</summary>
    private static IEnumerable<(string Name, List<string> DeclaredIn)> EventsByName(
        Dictionary<string, string> texts)
    {
        var byName = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (path, text) in texts)
        {
            foreach (Match m in Regex.Matches(text, @"(?m)^[ \t]*public[ \t]+(?:(?:static|virtual|override|abstract|sealed|new)[ \t]+)*event[ \t]+[\w\.<>,\?\[\] \t]+?[ \t](\w+)[ \t]*(?:;|=)"))
            {
                if (!byName.TryGetValue(m.Groups[1].Value, out var files))
                    byName[m.Groups[1].Value] = files = [];
                if (!files.Contains(path, StringComparer.OrdinalIgnoreCase)) files.Add(path);
            }
        }

        return byName
            .Select(kv => (Name: kv.Key, DeclaredIn: kv.Value))
            .OrderBy(t => t.Name, StringComparer.Ordinal);
    }

    /// <summary>
    ///     Every product .cs file, comments stripped. Stripping matters both ways:
    ///     a <c>see cref</c> is exactly as inert as a deleted call (the lesson
    ///     <c>CoopReachabilityTests</c> shipped with), and this repo's guides
    ///     routinely spell out a wiring in prose — a comment reading
    ///     "subscribe with <c>Foo +=</c>" must not count as subscribing.
    /// </summary>
    private static Dictionary<string, string> ProductTexts()
    {
        var repo = TestPaths.RepoRoot();
        var texts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in new[] { "DotCharter.UI", "DotCharter.Services", "DotCharter.Tone", "DotCharter.Presentation" })
        {
            foreach (var file in UiSourceTree.EnumerateFiles(
                     Path.Combine(repo, project), "*.cs", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(Path.Combine(repo, project), file);
                if (relative.StartsWith("bin", StringComparison.OrdinalIgnoreCase) ||
                    relative.StartsWith("obj", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                texts[file] = SourceText.StripComments(UiSourceTree.ReadAllText(file));
            }
        }

        return texts;
    }
}
