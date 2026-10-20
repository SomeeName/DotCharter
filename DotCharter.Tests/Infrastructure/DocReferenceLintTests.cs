using System.Text.RegularExpressions;

namespace DotCharter.Tests.Infrastructure;

/// <summary>
///     Keeps the agent guides HONEST about the things a machine can check: the
///     files they name, the tests they cite as guards, and the links between
///     them.
/// </summary>
/// <remarks>
///     <para>
///         These guides exist so project context lives in the codebase rather
///         than in an agent's context window, and the root <c>AGENTS.md</c>
///         tells agents to trust them rather than re-derive. That makes a stale
///         guide WORSE than no guide: a 2026-08 sweep found
///         <c>docs/agents/ui/pedalboard/</c> describing five deleted controls and
///         eight nonexistent tests in the present tense, and a doc claiming a
///         feature was missing that had shipped a release earlier — "a note that
///         had rotted into a shield".
///     </para>
///     <para>
///         <b>What this can and cannot catch.</b> A dead file path and a dead
///         test name are mechanical, so they are gated here and can never rot
///         again. <b>Numbers and prose are not</b> — a size, a count, an "X is
///         the only place that does Y" or an "enforced by test T" whose T exists
///         but asserts something else all still need a human. Do not read a green
///         run here as "the guides are accurate".
///     </para>
///     <para>
///         Both lints allow an EXPLICIT exception list, because a guide
///         legitimately names things that are gone — that is often the most
///         valuable sentence in it ("this was deleted in v0.43, do not restore
///         it"). Every entry carries its reason inline. An empty-looking
///         allow-list is the failure mode to watch: adding a name here to make a
///         red build green, without checking whether the doc's CLAIM is still
///         true, defeats the whole test.
///     </para>
/// </remarks>
public sealed class DocReferenceLintTests
{
    /// <summary>
    ///     Test type names a guide cites deliberately although they no longer
    ///     exist — each one because the SENTENCE is about the past.
    /// </summary>
    private static readonly Dictionary<string, string> RetiredTestNames = new(StringComparer.Ordinal)
    {
        ["OnlineTextBoxAccessibleNameTests"] =
            "network-surfaces.md says so: it scanned Views/Online, which no longer exists — the TONE3000 tab " +
            "moved under Views/Settings. UI/AccessibleNameTests walks all of Views/ and covers TextBox, " +
            "so the folder-scoped lint was superseded rather than lost.",
        ["LanMultiplayerModalTests"] = RemovedLanSessions,
        ["NetworkScreenStructureTests"] = RemovedLanScoreboard,
        ["HeadlessTimelineParityTests"] =
            "testing-headless-rendering.md names it as the FORMER name of HeadlessTimelineGoldenTests; " +
            "the GL baseline went with the OpenGL path at v0.33.",
        ["PedalDragReflowTests"] = RackEra,
        ["BoardDragGeometryTests"] = RackEra,
        ["BoardArrangeMathTests"] = RackEra,
        ["PedalboardBoardLayoutUiPerfSmokeTests"] = RackEra,
        ["PedalboardBoardStaleLayoutUiPerfSmokeTests"] = RackEra,
        ["PedalboardMaterialTokenTests"] = RackEra,
        ["RigNoiseTests"] = RackEra,
        ["RigTextureBakeTests"] = RackEra,
        ["SignalFlowLayoutTests"] = RackEra,

        // NOT retired — a MEMBER whose name happens to end in "Tests". The scan
        // reads backticked identifiers out of prose and cannot tell a type from
        // a method, and a guide is entitled to name the helper its tests call.
        ["ResetGlobalGateForTests"] =
            "JuceSharp/src/JuceSharp.StemSeparation/Processing/StemSeparator.cs — a METHOD, not a "
            + "test type. stem-separation-processes.md names it because the collection rule is about "
            + "who resets the process-global gate.",
    };

    private const string RemovedLanSessions =
        "Named as history (deferred-screen-bodies.md). LAN multiplayer sessions and their tests were removed on 2026-09-19 (restore from tag " +
        "pre-cut-2026-09); the sentence keeps the lesson, not the test.";

    private const string RemovedLanScoreboard =
        "Named as history (deferred-screen-bodies.md). The LAN scoreboard, its Network screen and their tests were removed on " +
        "2026-09-19 (restore from tag pre-cut-2026-09).";

    private const string RackEra =
        "docs/agents/ui/pedalboard/ names it as a guard of the rack v0.43 deleted. The banner at the " +
        "top of each of those files says so; the name is kept so nobody re-derives the machinery.";

    /// <summary>
    ///     Paths a guide names deliberately although they are not in the tree.
    /// </summary>
    private static readonly Dictionary<string, string> RetiredPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        ["PedalboardView.axaml"] = RetiredRack,
        ["PedalboardView.axaml.cs"] = RetiredRack,
        ["docs/agents/ui/pedalboard-visual-language.md"] = RetiredRack,
        ["pedalboard-visual-language.md"] = RetiredRack,
        ["DotCharter.Tests/LocalOnly_ViewInflationProbe.cs"] = LocalOnlyProbe,
        ["LocalOnly_ShellStartupProbe.cs"] = LocalOnlyProbe,
        ["DotCharter.Tests/LocalOnly_WaveformAlignmentProbe.cs"] = LocalOnlyProbe,
        ["DspNativeMethods.cs"] = DeletedAbiCopy,
        ["DotCharter.Services/Audio/Dsp/DspNativeMethods.cs"] = DeletedAbiCopy,
        // The audio host and the tone rig moved to DotCharter.Tone (the standalone
        // rig's service layer, same namespaces). JuceSharp/AGENTS.md and
        // JuceSharp/docs/agents/native-engine-abi.md still cite the old path; they
        // live in the submodule, which an agent worktree cannot commit to, and are
        // repointed in the next JuceSharp commit. Delete this entry with that one.
        ["DotCharter.Services/Audio/Equalizer/EqBand.cs"] =
            "Moved to DotCharter.Tone/Audio/Equalizer/EqBand.cs with the audio host; the submodule guides follow in their own commit",

        // ── the .h/.cpp/.md/.py half (2026-09-12) ────────────────────────────
        // Before this, the C++-citing guides were barely linted at all: the
        // extension filter stopped at .cs/.axaml/.resx/.csproj, so the whole of
        // JuceSharp/AGENTS.md and JuceSharp/docs/agents/** — which cite headers
        // and cores almost exclusively — had every path claim unchecked.
        ["abi_internal.h"] =
            "native-engine-abi.md names it in the PAST tense: it was the header of the per-domain ABI "
            + "split (src/abi/abi_*.cpp) deleted 2026-08-28 because it was never in "
            + "JUCESHARP_CORE_SOURCES. The sentence is the warning not to restore it.",
        ["juce_ResamplingAudioSource.cpp"] =
            "JUCE's OWN source, pulled by FetchContent into build/_deps — never in this tree. "
            + "rt-safety-and-tests.md names it to say what the load-time resampler replaced.",
        ["juce_AudioIODevice.h"] =
            "JUCE's OWN header, pulled by FetchContent into build/_deps — never in this tree. "
            + "native-engine-abi.md names its callback doc as where the old one-pointer-per-physical-channel "
            + "claim came from, which no Windows device implements (JuceSharp #31).",
        ["juce_OggVorbisAudioFormat.cpp"] =
            "JUCE's OWN source, pulled by FetchContent into build/_deps — never in this tree. "
            + "native-engine-abi.md names it to say the WEM decoder reaches the ONE libvorbis it includes (#426).",
        ["wwriff.cpp"] = Ww2OggUpstream,
        ["codebook.cpp"] = Ww2OggUpstream,
        ["get-pip.py"] =
            "An EXTERNAL bootstrap script the stem-separation child fetches; not a repo file.",
        ["02-format-capability-matrix.md"] =
            "A section of the Rocksmith format spec kept outside the repo (DotCharter-local); the "
            + "string-edit guide cites it for the .rscp chordNote rule.",
    };

    private const string Ww2OggUpstream =
        "UPSTREAM ww2ogg (hcs64/ww2ogg) source, never in this tree: native-engine-abi.md names it as "
        + "what src/wem/WemVorbisDecoder.cpp was ported from (#426).";

    private const string DeletedAbiCopy =
        "Deleted 2026-09-11: it was the app's OWN P/Invoke copy of the dca_engine_dsp_* ABI, which "
        + "inherited neither JuceSharp's UTF-8 marshalling lint nor its handle pinning. The rig goes "
        + "through JuceDspChain now. The guides name it to say it is GONE and must not come back — "
        + "which Services/Audio/DspNativeBindingTests enforces mechanically.";

    private const string RetiredRack =
        "Deleted in v0.43 with the retired rack. The guides cite it so an agent reading old code or " +
        "old comments knows what it was; read it with `git show <sha>^:<path>`.";

    private const string LocalOnlyProbe =
        "A git-IGNORED local-only probe. The docs say so where they name it — it is meant to be written " +
        "by hand, run once and left out of the tree.";

    // File extensions worth checking. Deliberately excludes .json/.h/.cpp: guides
    // name plenty of RUNTIME artefacts (appconfig.json, player-profiles.json,
    // ar.json) and submodule headers a worktree may not have checked out, and a
    // lint that cries wolf on those is a lint people disable.
    private static readonly string[] CheckedExtensions = [".cs", ".axaml", ".resx", ".csproj"];

    private static readonly Regex BacktickedPath = new(
        @"`(?<p>[A-Za-z0-9_\-./]+\.(?:cs|axaml|resx|csproj))`", RegexOptions.Compiled);

    /// <summary>
    ///     The NATIVE + prose half, added 2026-09-12. The four extensions above
    ///     are what the PARENT is written in; the submodule's guides cite headers,
    ///     cores, the RT harness and each other, so for them the path lint was
    ///     close to a no-op — <c>JuceSharp/AGENTS.md</c> alone names ~30 `.h` /
    ///     `.cpp` files and not one of them was checked.
    ///     <para>
    ///         <c>.json</c> is still out, for the reason the comment above gives
    ///         (guides name runtime artefacts: appconfig.json, ar.json), and so
    ///         is anything whose path carries an <c>...</c> elision — a guide
    ///         writes <c>JuceSharp/.../dcdsp_cab_irs.h</c> as an ABBREVIATION,
    ///         never as a path.
    ///     </para>
    /// </summary>
    private static readonly string[] NativeCheckedExtensions = [".h", ".cpp", ".md", ".py"];

    private static readonly Regex BacktickedNativePath = new(
        @"`(?<p>[A-Za-z0-9_\-./]+\.(?:h|cpp|md|py))`", RegexOptions.Compiled);

    /// <summary>
    ///     An <c>Rt*</c> RT-gate scenario a guide cites. Names ending in
    ///     <c>Tests</c> are xunit types and belong to the other lint.
    /// </summary>
    private static readonly Regex BacktickedRtScenario = new(
        @"`(?<n>Rt[A-Z][A-Za-z0-9_]{3,})`", RegexOptions.Compiled);

    /// <summary>
    ///     A <c>beginScenario(…)</c> call, its argument whole: one scenario body may name its
    ///     variants in a conditional (<c>beginScenario(grouped ? "RtA" : faster ? "RtB" : "RtC")</c>,
    ///     JuceSharp #32), and every literal it can pass is declared (<see cref="RtScenarioLiteral" />).
    /// </summary>
    private static readonly Regex RtScenarioDeclaration = new(
        @"beginScenario\((?<args>[^;]*?)\);", RegexOptions.Compiled);

    private static readonly Regex RtScenarioLiteral = new(@"""(?<n>Rt[A-Za-z0-9_]+)""", RegexOptions.Compiled);

    /// <summary>
    ///     RT scenario names a guide cites although the harness no longer
    ///     declares them — each because the sentence is about the past.
    /// </summary>
    private static readonly Dictionary<string, string> RetiredRtScenarios = new(StringComparer.Ordinal);

    private static readonly Regex BacktickedTestName = new(
        @"`(?<n>[A-Za-z0-9_]*Tests)(?:\s*\r?\n?\s*\.[A-Za-z0-9_]+)?`", RegexOptions.Compiled);

    private static readonly Regex MarkdownLink = new(@"\]\((?<t>[^)\s]+)\)", RegexOptions.Compiled);

    /// <summary>
    ///     Every <c>*Tests</c> type a guide cites is a type that exists, or is
    ///     listed above with the reason it is named anyway.
    /// </summary>
    /// <remarks>
    ///     This is the class the 2026-08 sweep hit most: <c>docs/agents/ui/pedalboard/</c>
    ///     alone cited eight guards that had been deleted with the surface they
    ///     guarded, and three <c>InspectorConstructionUiPerfSmokeTests</c> methods
    ///     under their pre-v0.43 names. A reader who greps for the name and finds
    ///     nothing wastes a minute; a reader who does NOT grep believes the rule
    ///     is pinned when nothing pins it.
    /// </remarks>
    [Fact]
    public void EveryTestNamedInAGuide_ExistsOrIsListedAsRetired()
    {
        var declared = DeclaredTestTypes();
        Assert.True(declared.Count > 500,
            $"only found {declared.Count} test types — the source scan is broken, not the docs");

        var bad = new List<string>();
        foreach (var doc in Guides())
        {
            var text = UiSourceTree.ReadAllText(doc);
            foreach (Match m in BacktickedTestName.Matches(text))
            {
                var name = m.Groups["n"].Value;
                if (name.Length == 0 || name == "Tests") continue; // "DotCharter.Tests"
                if (declared.Contains(name)) continue;
                if (RetiredTestNames.ContainsKey(name)) continue;
                bad.Add($"{Rel(doc)} cites `{name}`, which is not a test type anywhere in the repo");
            }
        }

        Assert.True(bad.Count == 0,
            "A guide names a test that does not exist. Either the test was renamed (repoint the doc at " +
            "the real one) or it was deleted with what it guarded — in which case say so in the doc AND " +
            "add it to RetiredTestNames with the reason, so the sentence stays readable and the lint " +
            "stays green for the right reason:" + Environment.NewLine +
            string.Join(Environment.NewLine, bad));
    }

    /// <summary>
    ///     Every source file a guide names in backticks resolves in the tree, or
    ///     is listed above as deliberately gone.
    /// </summary>
    [Fact]
    public void EverySourcePathNamedInAGuide_ResolvesOrIsListedAsRetired()
    {
        var byRelative = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var byLeaf = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in SourceFiles())
        {
            var rel = Rel(f).Replace('\\', '/');
            byRelative.Add(rel);
            var leaf = Path.GetFileName(f);
            if (!byLeaf.TryGetValue(leaf, out var list)) byLeaf[leaf] = list = [];
            list.Add(rel);
        }

        Assert.True(byRelative.Count > 2000,
            $"only enumerated {byRelative.Count} source files — the scan is broken, not the docs");

        var bad = new List<string>();
        foreach (var doc in Guides())
        {
            foreach (var line in UiSourceTree.ReadAllLines(doc))
            {
                // Headings use `docs/agents/ui/Y.md`-style placeholders, not paths.
                if (line.TrimStart().StartsWith('#')) continue;
                foreach (Match m in BacktickedPath.Matches(line))
                {
                    var p = m.Groups["p"].Value;
                    // A bare ".Foo.cs" is a partial-file SUFFIX, not a path.
                    if (p.StartsWith('.')) continue;
                    if (RetiredPaths.ContainsKey(p)) continue;
                    if (Resolves(p, byRelative, byLeaf)) continue;
                    bad.Add($"{Rel(doc)} names `{p}`, which is not in the tree");
                }
            }
        }

        Assert.True(bad.Count == 0,
            "A guide names a source file that does not exist. If it moved, repoint the doc; if it was " +
            "deleted on purpose, say so in the doc AND add it to RetiredPaths with the reason:" +
            Environment.NewLine + string.Join(Environment.NewLine, bad));
    }

    /// <summary>
    ///     Every <c>.h</c> / <c>.cpp</c> / <c>.md</c> / <c>.py</c> path a guide
    ///     names in backticks resolves, or is listed as deliberately gone.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The sibling test above covers <c>.cs/.axaml/.resx/.csproj</c>,
    ///         which are the extensions the PARENT is written in. The submodule's
    ///         guides are the ones that rot unseen: they cite C++ headers, the
    ///         <c>dcdsp</c> cores, the RT harness and each other, so until this
    ///         existed a whole repo's worth of path claims were unchecked. A
    ///         submodule-less worktree contributes no native files and nothing to
    ///         check, so the case SKIPS rather than passing vacuously.
    ///     </para>
    ///     <para>
    ///         A submodule guide writes paths from the SUBMODULE root
    ///         (<c>src/JuceSharp.Native/src/dsp/cores/dcdsp_droptune.h</c>), so
    ///         that prefix is resolved too — see <see cref="Resolves" />.
    ///     </para>
    /// </remarks>
    [Fact]
    public void EveryNativePathNamedInAGuide_ResolvesOrIsListedAsRetired()
    {
        var submodule = Path.Combine(TestPaths.RepoRoot(), "JuceSharp", "src", "JuceSharp.Native");
        Assert.SkipUnless(Directory.Exists(submodule),
            "JuceSharp is not checked out in this worktree, so there are no native paths to resolve.");

        var byRelative = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var byLeaf = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in SourceFiles(extensions: NativeCheckedExtensions))
        {
            var rel = Rel(f).Replace('\\', '/');
            byRelative.Add(rel);
            var leaf = Path.GetFileName(f);
            if (!byLeaf.TryGetValue(leaf, out var list)) byLeaf[leaf] = list = [];
            list.Add(rel);
        }

        // Anti-vacuity, and it has to be high enough to prove the SUBMODULE's
        // headers are in the index: the cores alone are ~60 files.
        Assert.True(byRelative.Count > 300,
            $"only enumerated {byRelative.Count} native/doc files — the scan is broken, not the docs");
        Assert.Contains("JuceSharp/src/JuceSharp.Native/include/dotcharter_audio.h", byRelative);

        var bad = new List<string>();
        foreach (var doc in Guides())
        {
            foreach (var line in UiSourceTree.ReadAllLines(doc))
            {
                if (line.TrimStart().StartsWith('#')) continue;
                foreach (Match m in BacktickedNativePath.Matches(line))
                {
                    var p = m.Groups["p"].Value;
                    if (p.StartsWith('.')) continue;      // ".Foo.md" — a suffix, not a path
                    if (p.Contains("...")) continue;      // "JuceSharp/.../dcdsp_cab_irs.h" — an elision
                    if (RetiredPaths.ContainsKey(p)) continue;
                    if (Resolves(p, byRelative, byLeaf)) continue;
                    bad.Add($"{Rel(doc)} names `{p}`, which is not in the tree");
                }
            }
        }

        Assert.True(bad.Count == 0,
            "A guide names a native or markdown file that does not exist. If it moved, repoint the doc; " +
            "if it was deleted on purpose, say so in the doc AND add it to RetiredPaths with the reason:" +
            Environment.NewLine + string.Join(Environment.NewLine, bad));
    }

    /// <summary>
    ///     Every <c>Rt*</c> scenario a guide cites as the guard of a real-time
    ///     rule is really declared in the RT harness.
    /// </summary>
    /// <remarks>
    ///     These are the submodule's equivalent of a <c>*Tests</c> citation and
    ///     carry exactly the same weight — <c>JuceSharp/AGENTS.md</c> ends most
    ///     of its hard rules with "Pinned by <c>RtSomething</c>" — but they are
    ///     not xunit types, so the sibling lint never saw them. They are declared
    ///     ONE way, <c>beginScenario(…)</c> in <c>rt_main.cpp</c>, which is what
    ///     makes them checkable at all — every <c>"Rt…"</c> literal its argument can
    ///     pass, since one body may pick its variant's name in a conditional.
    /// </remarks>
    [Fact]
    public void EveryRtScenarioNamedInAGuide_IsDeclaredInTheRtHarness()
    {
        var harness = Path.Combine(TestPaths.RepoRoot(), "JuceSharp", "src", "JuceSharp.Native",
            "tests", "rt", "rt_main.cpp");
        Assert.SkipUnless(File.Exists(harness),
            "JuceSharp is not checked out in this worktree, so the RT harness cannot be read.");

        var declared = RtScenarioDeclaration.Matches(UiSourceTree.ReadAllText(harness))
            .SelectMany(m => RtScenarioLiteral.Matches(m.Groups["args"].Value))
            .Select(m => m.Groups["n"].Value)
            .ToHashSet(StringComparer.Ordinal);
        Assert.True(declared.Count > 50,
            $"only found {declared.Count} RT scenarios — the harness scan is broken, not the docs");

        var bad = new List<string>();
        var cited = 0;
        foreach (var doc in Guides())
        {
            foreach (Match m in BacktickedRtScenario.Matches(UiSourceTree.ReadAllText(doc)))
            {
                var name = m.Groups["n"].Value;
                // `RtlSupportTests` and friends: an xunit type, not a scenario.
                if (name.EndsWith("Tests", StringComparison.Ordinal)) continue;
                cited++;
                if (declared.Contains(name)) continue;
                if (RetiredRtScenarios.ContainsKey(name)) continue;
                bad.Add($"{Rel(doc)} cites `{name}`, which rt_main.cpp does not declare");
            }
        }

        Assert.True(cited > 20, $"only matched {cited} RT scenario citations — the doc scan is broken");
        Assert.True(bad.Count == 0,
            "A guide names an RT-gate scenario that does not exist. A rule whose named guard is not in " +
            "the harness is a rule nothing pins — repoint it at the real scenario, or say in the doc that " +
            "it is gone AND add it to RetiredRtScenarios with the reason:" + Environment.NewLine +
            string.Join(Environment.NewLine, bad));
    }

    /// <summary>Every relative markdown link between guides resolves.</summary>
    /// <remarks>
    ///     The routing tables are the entry point for every agent that opens a
    ///     file in a project, so a dead link there costs a search on every future
    ///     visit. Cheap to check, and it has to hold for the routers to be worth
    ///     having.
    /// </remarks>
    [Fact]
    public void EveryRelativeLinkBetweenGuides_Resolves()
    {
        var bad = new List<string>();
        var checkedLinks = 0;
        foreach (var doc in Guides())
        {
            var dir = Path.GetDirectoryName(doc)!;
            foreach (Match m in MarkdownLink.Matches(UiSourceTree.ReadAllText(doc)))
            {
                var target = m.Groups["t"].Value;
                if (target.StartsWith("http", StringComparison.OrdinalIgnoreCase) ||
                    target.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                target = target.Split('#')[0];
                if (target.Length == 0) continue;
                checkedLinks++;
                var resolved = Path.GetFullPath(Path.Combine(dir, target));
                if (!File.Exists(resolved) && !Directory.Exists(resolved))
                    bad.Add($"{Rel(doc)} links to `{target}`, which does not exist");
            }
        }

        Assert.True(checkedLinks > 100, $"only checked {checkedLinks} links — the scan is broken");
        Assert.True(bad.Count == 0, string.Join(Environment.NewLine, bad));
    }

    // ---- plumbing ------------------------------------------------------------

    private static bool Resolves(string p, HashSet<string> byRelative, Dictionary<string, List<string>> byLeaf)
    {
        var norm = p.TrimStart('.', '/');
        if (byRelative.Contains(norm)) return true;

        var leaf = norm.Split('/')[^1];
        if (!byLeaf.TryGetValue(leaf, out var candidates)) return false;
        if (!norm.Contains('/')) return true;

        // A guide may name a path relative to its PROJECT rather than to the repo
        // — "Views/Shell/MainMenuView.axaml" inside the UI guide — so any file
        // whose tail matches counts…
        if (candidates.Any(c => c.EndsWith('/' + norm, StringComparison.OrdinalIgnoreCase))) return true;

        // …and it may drop the "DotCharter." prefix from the project segment
        // itself ("Services/Export/GpExporter.cs" for DotCharter.Services/…).
        // Narrow on purpose: only the FIRST segment may be re-prefixed, so
        // "Export/GpExporter.cs" still does not match anything.
        if (candidates.Any(c => c.Equals("DotCharter." + norm, StringComparison.OrdinalIgnoreCase)))
            return true;

        // …and a SUBMODULE guide writes its paths from the submodule root
        // ("src/JuceSharp.Native/src/dsp/cores/dcdsp_droptune.h"), which is
        // where every `.h`/`.cpp` citation in JuceSharp/docs/agents/** lives.
        return candidates.Any(c => c.Equals("JuceSharp/" + norm, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    ///     Every guide: the root <c>AGENTS.md</c>, the public root documents
    ///     (<see cref="PublicDocs" />), each project's, and all of <c>docs/agents/**</c>.
    /// </summary>
    /// <remarks>
    ///     Globbed, never listed. The root guide's own hardest-won rule is that a
    ///     lint locating source BY PATH goes blind the moment a file moves — a
    ///     hardcoded list here would silently stop covering a guide the day
    ///     somebody adds one, which is exactly the failure this test exists to
    ///     prevent in the docs themselves.
    /// </remarks>
    /// <summary>
    ///     The repository-root documents written for users and contributors rather than
    ///     agents. Listed, not globbed: the root also holds <c>CLAUDE.md</c> (a one-line
    ///     import) and nothing else that is a document. <see cref="PublicDocs_AllExist" />
    ///     keeps the list from naming a file that has gone.
    /// </summary>
    private static readonly string[] PublicDocs =
        ["README.md", "ACKNOWLEDGEMENTS.md", "THIRD-PARTY-NOTICES.md", "CONTRIBUTING.md", "SECURITY.md", "CODE_OF_CONDUCT.md", "PRIVACY.md", "LEGAL.md"];

    /// <summary>
    ///     The lints above scan <see cref="PublicDocs" /> by path, so a renamed file would
    ///     silently stop being checked — fail instead.
    /// </summary>
    [Fact]
    public void PublicDocs_AllExist()
    {
        var missing = PublicDocs.Where(d => !File.Exists(Path.Combine(TestPaths.RepoRoot(), d))).ToList();
        Assert.True(missing.Count == 0,
            "DocReferenceLintTests.PublicDocs names a file that is not at the repository root: " +
            string.Join(", ", missing));
    }

    private static IEnumerable<string> Guides()
    {
        var root = TestPaths.RepoRoot();
        yield return Path.Combine(root, "AGENTS.md");

        // The public front door (#761): what a user or a first-time contributor reads
        // before any guide. A dead link there is the first thing a visitor sees.
        foreach (var publicDoc in PublicDocs)
            yield return Path.Combine(root, publicDoc);

        foreach (var dir in Directory.EnumerateDirectories(root, "DotCharter.*"))
        {
            var guide = Path.Combine(dir, "AGENTS.md");
            if (File.Exists(guide)) yield return guide;
        }

        foreach (var doc in UiSourceTree.EnumerateFiles(
                     Path.Combine(root, "docs", "agents"), "*.md", SearchOption.AllDirectories))
        {
            yield return doc;
        }

        // The human onboarding guides (docs/dev): a first-time contributor's path from
        // clone to running app, so a dead path in them is the first thing they hit.
        var devDocs = Path.Combine(root, "docs", "dev");
        if (Directory.Exists(devDocs))
        {
            foreach (var doc in UiSourceTree.EnumerateFiles(devDocs, "*.md", SearchOption.AllDirectories))
                yield return doc;
        }

        // The 2026-08-29 docs-truth audit found these were never scanned though
        // the root guide reads as if they were: the submodule's guides name tests
        // and paths too.
        var sub = Path.Combine(root, "JuceSharp");
        if (File.Exists(Path.Combine(sub, "AGENTS.md"))) yield return Path.Combine(sub, "AGENTS.md");
        var subDocs = Path.Combine(sub, "docs", "agents");
        if (Directory.Exists(subDocs))
        {
            foreach (var doc in UiSourceTree.EnumerateFiles(subDocs, "*.md", SearchOption.AllDirectories))
                yield return doc;
        }
    }

    /// <summary>
    ///     Every type declared in the test project(s) whose name ends in
    ///     <c>Tests</c>. Read from SOURCE rather than by reflection so the
    ///     JuceSharp suite counts too — a worktree may not have that submodule
    ///     checked out, and then it simply contributes nothing.
    /// </summary>
    private static HashSet<string> DeclaredTestTypes()
    {
        var declaration = new Regex(@"\b(?:class|record|struct)\s+([A-Za-z0-9_]*Tests)\b", RegexOptions.Compiled);
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var dir in new[]
                 {
                     Path.Combine(TestPaths.RepoRoot(), "DotCharter.Tests"),
                     Path.Combine(TestPaths.RepoRoot(), "JuceSharp", "tests")
                 })
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var f in UiSourceTree.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                if (f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") ||
                    f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
                {
                    continue;
                }

                foreach (Match m in declaration.Matches(UiSourceTree.ReadAllText(f)))
                    found.Add(m.Groups[1].Value);
            }
        }

        return found;
    }

    /// <summary>
    ///     Every checked-extension source file, walked EXPLICITLY so the build
    ///     output, the git dir and the git-ignored local-only checkouts are never
    ///     entered.
    /// </summary>
    /// <remarks>
    ///     A naive <c>EnumerateFiles(repoRoot, "*.*", AllDirectories)</c> here
    ///     crashed the testhost: this repo's root also carries `.git`, `TestData`,
    ///     several multi-gigabyte reference checkouts and every project's `bin`
    ///     and `obj`. Filtering AFTER enumerating still pays to walk them.
    /// </remarks>
    private static IEnumerable<string> SourceFiles(string? root = null, string[]? extensions = null)
    {
        // "build" and "_deps" are the CMake tree: a configured JuceSharp build
        // drops JUCE, Rubber Band and NeuralAudio sources in there, tens of
        // thousands of .h/.cpp files that are not this repo's and would make a
        // leaf match mean nothing.
        string[] skip =
        [
            "bin", "obj", ".git", ".vs", ".idea", ".claude", ".codex", "node_modules", "TestData",
            "packages", "build", "_deps"
        ];
        var wanted = extensions ?? CheckedExtensions;
        var stack = new Stack<string>();
        stack.Push(root ?? TestPaths.RepoRoot());
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            foreach (var child in Directory.EnumerateDirectories(dir))
            {
                if (!skip.Contains(Path.GetFileName(child), StringComparer.OrdinalIgnoreCase))
                    stack.Push(child);
            }

            foreach (var f in UiSourceTree.EnumerateFiles(dir))
            {
                if (wanted.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                    yield return f;
            }
        }
    }

    [Theory]
    [InlineData(".codex")]
    [InlineData(".claude")]
    public void SourceWalker_ExcludesNestedAgentWorktrees(string agentDirectory)
    {
        var root = Path.Combine(Path.GetTempPath(), "dotcharter-source-walk-" + Guid.NewGuid());
        try
        {
            var nested = Path.Combine(root, agentDirectory, "worktrees", "old", "DotCharter.UI");
            Directory.CreateDirectory(nested);
            File.WriteAllText(Path.Combine(nested, "Stale.cs"), "class Stale {}");
            var product = Path.Combine(root, "Current.cs");
            File.WriteAllText(product, "class Current {}");

            Assert.Equal([product], SourceFiles(root).ToArray());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static string Rel(string absolute) => Path.GetRelativePath(TestPaths.RepoRoot(), absolute);
}
