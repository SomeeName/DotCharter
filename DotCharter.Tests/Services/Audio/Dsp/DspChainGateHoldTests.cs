using System.Text;

namespace DotCharter.Tests.Services.Audio.Dsp;

/// <summary>
/// H10: <c>JuceDspChainService.ProcessOfflineBuffer</c> P/Invoked the bare
/// <c>dca_engine*</c> outside the class's own gate.
/// <para>
/// <c>docs/agents/services/audio-engine/threading-contracts.md</c> says the DSP rig's unpinned
/// engine handle is safe ONLY because <c>JuceDspChainService</c> holds
/// <c>_gate</c> across every P/Invoke AND across <c>AttachEngine</c> — a
/// convention held by one class rather than a property of the handle.
/// <c>ProcessOfflineBuffer</c> was expression-bodied straight into
/// <c>Safe(() =&gt; _native.ProcessBuffer(...))</c> with no lock, over a
/// non-volatile <c>EngineHandle</c>, so an <c>AttachEngine(nint.Zero)</c>
/// followed by <c>dca_engine_destroy</c> could complete inside the interop's
/// own null-check-then-call window. Masked only by its single caller owning a
/// private, device-less engine.
/// </para>
/// <para>
/// This lint is INVERTED on purpose: every <c>_native.</c> use must be under
/// <c>lock (_gate)</c> unless its member is on the exemption list below with a
/// reason, so a new engine-scoped member defaults to needing the gate.
/// </para>
/// </summary>
public sealed class DspChainGateHoldTests
{
    private const string ChainFile = "DotCharter.Tone/Audio/Dsp/JuceDspChainService.cs";

    /// <summary>
    /// The only members allowed to touch <c>_native</c> without the gate, and
    /// why. Every one of these is a STATIC-CATALOGUE call
    /// (<c>IDspNativeInterop</c> splits them out under its own header): they
    /// take no engine handle, so there is no freed pointer to race.
    /// </summary>
    private static readonly (string Member, string Reason)[] Exempt =
    [
        ("Player", "a pass-through of the interop's immutable seat index — no native call at all"),
        ("LoadCatalog", "static module-type catalogue; takes no engine handle"),
        ("LoadValueLabels", "static value-label catalogue; takes no engine handle"),
        ("LoadStep", "static quantization-step query; takes no engine handle"),
    ];

    [Fact]
    public void EveryEngineScopedNativeCallInTheChainServiceIsUnderItsGate()
    {
        var src = StripComments(File.ReadAllText(Path.Combine(TestPaths.RepoRoot(),
            ChainFile.Replace('/', Path.DirectorySeparatorChar))));
        var gated = LockGateRanges(src);

        var inside = 0;
        var offenders = new List<string>();
        for (var i = src.IndexOf("_native.", StringComparison.Ordinal); i >= 0;
             i = src.IndexOf("_native.", i + 1, StringComparison.Ordinal))
        {
            if (gated.Any(r => i >= r.Start && i < r.End)) { inside++; continue; }

            var member = EnclosingMember(src, i);
            if (Exempt.Any(e => e.Member == member)) continue;
            offenders.Add($"{member}: {LineAt(src, i)}");
        }

        Assert.True(offenders.Count == 0,
            "Every engine-scoped native call in JuceDspChainService must hold _gate — the rig's "
            + "dca_engine* is unpinned and AttachEngine(0) is the only barrier there is. Offenders:"
            + Environment.NewLine + string.Join(Environment.NewLine, offenders));

        // Non-vacuity floor: if the scan ever stops matching, this drops to 0
        // and the lint would pass while looking at nothing.
        Assert.True(inside >= 15,
            $"Expected the gated-call scan to still find the class's native calls; found {inside}.");
    }

    /// <summary>
    /// A rename must break the exemption rather than silently widen it — every
    /// exempt name still has to be declared in the file.
    /// </summary>
    [Fact]
    public void EveryExemptionStillNamesAMemberThatExists()
    {
        var src = File.ReadAllText(Path.Combine(TestPaths.RepoRoot(),
            ChainFile.Replace('/', Path.DirectorySeparatorChar)));
        foreach (var (member, reason) in Exempt)
        {
            Assert.True(src.Contains($" {member}(", StringComparison.Ordinal)
                        || src.Contains($" {member} =>", StringComparison.Ordinal),
                $"The gate exemption for '{member}' ({reason}) names a member that no longer exists.");
        }
    }

    /// <summary>
    /// The guide's safety claim and the code have to agree. The doc says the
    /// unpinned handle is safe because this class gates EVERY P/Invoke; it must
    /// also say which calls are exempt and why, or the next reader trusts a
    /// sentence that is only mostly true.
    /// </summary>
    [Fact]
    public void TheGuideSaysWhichCallsTheGateCovers()
    {
        var doc = File.ReadAllText(Path.Combine(TestPaths.RepoRoot(),
            Path.Combine("docs", "agents", "services", "audio-engine", "threading-contracts.md")));

        Assert.Contains("ProcessOfflineBuffer", doc, StringComparison.Ordinal);
        Assert.Contains("DspChainGateHoldTests", doc, StringComparison.Ordinal);
    }

    // ----- helpers -----------------------------------------------------------

    private static List<(int Start, int End)> LockGateRanges(string src)
    {
        var ranges = new List<(int, int)>();
        const string needle = "lock (_gate)";
        for (var i = src.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = src.IndexOf(needle, i + 1, StringComparison.Ordinal))
        {
            var open = src.IndexOf('{', i + needle.Length);
            var semi = src.IndexOf(';', i + needle.Length);
            if (open < 0 || (semi >= 0 && semi < open))
            {
                // `lock (_gate) return …;` — a single statement, the whole line.
                if (semi > 0) ranges.Add((i, semi + 1));
                continue;
            }

            var depth = 0;
            var j = open;
            for (; j < src.Length; j++)
            {
                if (src[j] == '{') depth++;
                else if (src[j] == '}' && --depth == 0) break;
            }
            ranges.Add((open, Math.Min(j + 1, src.Length)));
        }

        return ranges;
    }

    /// <summary>Nearest preceding member declaration — the last line before
    /// <paramref name="index"/> that opens a member with an access modifier.</summary>
    private static string EnclosingMember(string src, int index)
    {
        var head = src[..index];
        var lines = head.Split('\n');
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            var line = lines[i].Trim();
            if (!line.StartsWith("public ", StringComparison.Ordinal)
                && !line.StartsWith("private ", StringComparison.Ordinal)
                && !line.StartsWith("internal ", StringComparison.Ordinal))
            {
                continue;
            }

            if (line.Contains("class ", StringComparison.Ordinal)) continue;

            var paren = line.IndexOf('(');
            var arrow = line.IndexOf("=>", StringComparison.Ordinal);
            var cut = paren >= 0 && (arrow < 0 || paren < arrow) ? paren
                : arrow >= 0 ? arrow
                : line.Length;
            var name = line[..cut].TrimEnd();
            var space = name.LastIndexOf(' ');
            return space >= 0 ? name[(space + 1)..] : name;
        }

        return "<file scope>";
    }

    private static string LineAt(string src, int index)
    {
        var start = src.LastIndexOf('\n', Math.Max(0, index - 1)) + 1;
        var end = src.IndexOf('\n', index);
        return src[start..(end < 0 ? src.Length : end)].Trim();
    }

    /// <summary>Blanks out comments so prose naming <c>_native.</c> cannot be
    /// read as a call site (the mistake the reachability lints strip for).</summary>
    private static string StripComments(string src)
    {
        var sb = new StringBuilder(src.Length);
        var i = 0;
        while (i < src.Length)
        {
            if (src[i] == '"')
            {
                var verbatim = i > 0 && src[i - 1] == '@';
                sb.Append(src[i++]);
                while (i < src.Length)
                {
                    if (!verbatim && src[i] == '\\' && i + 1 < src.Length) { sb.Append(src[i++]); sb.Append(src[i++]); continue; }
                    if (src[i] == '"') { sb.Append(src[i++]); break; }
                    sb.Append(src[i++]);
                }
                continue;
            }

            if (src[i] == '/' && i + 1 < src.Length && src[i + 1] == '/')
            {
                while (i < src.Length && src[i] != '\n') { sb.Append(' '); i++; }
                continue;
            }

            if (src[i] == '/' && i + 1 < src.Length && src[i + 1] == '*')
            {
                while (i < src.Length && !(src[i] == '*' && i + 1 < src.Length && src[i + 1] == '/'))
                {
                    sb.Append(src[i] == '\n' ? '\n' : ' ');
                    i++;
                }

                if (i < src.Length) { sb.Append("  "); i += 2; }
                continue;
            }

            sb.Append(src[i++]);
        }

        return sb.ToString();
    }
}
