using System.Text.RegularExpressions;
using DotCharter.Core.Domain.Vocals;
using DotCharter.Services.Editing.Selection;
using DotCharter.Services.Export;
using DotCharter.Services.Infrastructure.Documents;
using DotCharter.Services.Infrastructure.Recovery;
using DotCharter.Services.Library;
using DotCharter.Services.State.Checkpoints;
using DotCharter.Tone.Infrastructure.Storage;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.ViewModels.Panels;
using DotCharter.Services.Editing.Tracks;
using DotCharter.Services.Session;

namespace DotCharter.Tests.Services.Session;

/// <summary>
///     <c>IProjectSessionService.MayReplaceOpenProjectAsync</c> exists because
///     <c>ApplyProject(resetHistory: true)</c> clears the undo history — the
///     previous project's unsaved edits become unreachable the instant it runs.
///     File → Open was wired to it first; the remaining replace paths (New
///     Project, the song browser / Play Mode song select, EOF import, Songsterr
///     import, checkpoint restore) went straight through.
///     <para>
///         <see cref="ProjectReplaceGuardTests" /> covers the RULE. This file
///         covers the WIRING: that each call site actually asks, and abandons
///         when the answer is no.
///     </para>
/// </summary>
public sealed class ProjectReplaceGuardWiringTests
{
    /// <summary>
    ///     Creating a project replaces the open one. The guard sits ahead of the
    ///     folder picker, so a refusal must leave the picker unshown and the
    ///     session untouched — nothing has been written at that point.
    /// </summary>
    private static readonly NewProjectDetails Battery =
        new("Metallica", "Battery", "", "", "", "", "", "", null, null);

    [Fact]
    public async Task NewProject_Refused_NeverReachesTheFolderPicker()
    {
        var h = new Harness();
        h.Session.Allow = false;
        await h.NewProjectCreator().CreateAsync(Battery);

        Assert.Equal(1, h.Session.AskedCount);
        Assert.Empty(h.Storage.RequestedTitles);
        Assert.Equal("", h.State.CurrentProjectPath);
        Assert.Empty(h.Editor.Beats);
    }

    /// <summary>The same gesture allowed still creates the project, so the guard
    /// is a question and not a new refusal.</summary>
    [Fact]
    public async Task NewProject_Allowed_StillCreates()
    {
        var h = new Harness();
        h.Storage.NextFolder = h.TempDir;
        await h.NewProjectCreator().CreateAsync(Battery);

        Assert.Equal(1, h.Session.AskedCount);
        Assert.NotEqual("", h.State.CurrentProjectPath);
    }

    /// <summary>
    ///     Guarded inside <c>LoadSongItem</c> rather than in the browser's
    ///     command, because Play Mode's song select calls this exact method — a
    ///     guard on the command would leave the game shell's path open.
    /// </summary>
    [Fact]
    public async Task LibrarySongLoad_Refused_LoadsNothing()
    {
        var h = new Harness();
        h.Session.Allow = false;
        var vm = h.SongBrowser();

        var loaded = await vm.LoadSongItem(new PsarcSongListItem(new LibrarySongEntry
        {
            SongKey = "BatCountry",
            Title = "Bat Country",
            PackagePath = @"E:\Rocksmith2014\dlc\bat_country_p.psarc",
            Arrangements = [new LibraryArrangementEntry { ArrangementName = "Combo", SngPath = "songs/bin/x.sng" }]
        }, FakeLocalization.English()));

        Assert.False(loaded);
        Assert.Equal(1, h.Session.AskedCount);
        Assert.Equal(0, h.Library.LoadCalls);
        Assert.Equal("", h.State.CurrentAudioFileName);
    }

    /// <summary>
    ///     Declaring the dirty state is not a step of the HAPPY path — it belongs
    ///     to every exit. The chart is applied first and the audio is loaded after
    ///     it, and this is the method Play Mode's song select calls directly (where
    ///     <c>preferMemoryPlayback</c> reads the whole song through
    ///     <c>File.ReadAllBytesAsync</c>), so a song whose audio will not decode
    ///     used to skip the declaration entirely.
    ///     <para>
    ///         What that left behind: <c>ApplyProject(resetHistory: true)</c> had
    ///         already minted a new root node id, and <c>IsDirty</c> is "current
    ///         node id ≠ baseline id" with ids that only ever increase — so the
    ///         baseline could never match again. A chart nobody had edited showed
    ///         `*` forever, closing raised "unsaved changes", and the recovery
    ///         snapshotter (same flag) offered to restore it on every launch.
    ///     </para>
    /// </summary>
    [Fact]
    public async Task LibrarySongLoad_WhoseAudioThrows_StillDeclaresTheSessionClean()
    {
        var h = new Harness();
        h.Library.PlayableAudioPath = @"C:\library\bat_country.wem";
        h.Session.ThrowOnLoadAudio = true;
        var vm = h.SongBrowser();

        var loaded = await vm.LoadSongItem(BatCountry());

        // The load itself failed — but the CHART is on screen and is the on-disk
        // chart, so the session has to say so.
        Assert.False(loaded);
        Assert.Equal(1, h.Library.LoadCalls);
        Assert.Equal(1, h.Session.MarkedCleanCount);
        Assert.Equal(0, h.Session.MarkedDirtyCount);
    }

    /// <summary>
    ///     The other side of the same rule: a failure BEFORE the swap must declare
    ///     nothing. The previous project is still open with its own unsaved edits,
    ///     and calling <c>MarkProjectClean</c> for it would drop the close prompt
    ///     that protects them.
    /// </summary>
    [Fact]
    public async Task LibrarySongLoad_ThatFailsBeforeTheSwap_DeclaresNothing()
    {
        var h = new Harness();
        h.Library.ThrowOnLoad = true;
        var vm = h.SongBrowser();

        var loaded = await vm.LoadSongItem(BatCountry());

        Assert.False(loaded);
        Assert.Equal(0, h.Session.MarkedCleanCount);
        Assert.Equal(0, h.Session.MarkedDirtyCount);
    }

    /// <summary>
    ///     Crash recovery loads the SNAPSHOT and then re-points Save at the
    ///     ORIGINAL <c>.rscp</c> — a file that does not contain the recovered work.
    ///     <c>LoadFile</c> ends in <c>MarkClean</c>, which was true of the snapshot
    ///     and false the moment the path moved, and nothing said so: no `*` in the
    ///     title, <c>MainWindow.OnClosing</c> read the session as clean and went
    ///     straight to <c>TerminateNow()</c> with no prompt. Recover a crash, close
    ///     the app, lose exactly the work the recovery had just restored.
    ///     <para>
    ///         The existing source lint cannot see this: it greps the enclosing
    ///         method for a declaration, and <c>LoadFile</c>'s clean one lives in
    ///         another type.
    ///     </para>
    /// </summary>
    [Fact]
    public async Task CrashRecovery_AfterRecovering_LeavesTheSessionDirty()
    {
        var h = new Harness();
        var snapshotPath = Path.Combine(h.TempDir, "recovered.rscp");
        var originalPath = Path.Combine(h.TempDir, "battery.rscp");
        h.Recovery.Sessions =
        [
            new RecoverableSession
            {
                SnapshotPath = snapshotPath,
                SidecarPath = snapshotPath + ".json",
                Metadata = new RecoverySnapshotMetadata
                {
                    OriginalProjectPath = originalPath,
                    SongName = "Battery"
                }
            }
        ];

        var vm = h.RecoveryDialog();
        // Opening pre-selects the (single) session; the footer primary acts on it.
        Assert.Same(Assert.Single(vm.Sessions), vm.SelectedSession);
        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)vm.RecoverSelectedCommand).ExecuteAsync(null);

        // Save points at the original file again…
        Assert.Equal(originalPath, h.State.CurrentProjectPath);
        // …which is precisely why the session is dirty.
        Assert.Equal(1, h.Session.MarkedDirtyCount);
    }

    /// <summary>
    ///     The same rule at the OTHER site, and the worse one: File → Open.
    ///     <c>NotifyProjectSwitched()</c> and <c>MarkClean()</c> both used to sit at
    ///     the end of the try, after the audio load — so opening a project whose
    ///     audio will not decode left the chart on screen, correct and untouched,
    ///     wearing a `*` that no amount of undoing could clear, while the PREVIOUS
    ///     session's crash snapshot survived and kept being offered for restore.
    /// </summary>
    [Fact]
    public async Task ProjectOpen_WhoseAudioThrows_StillDeclaresTheSessionClean()
    {
        using var h = new SessionHarness();
        h.Audio.LoadAudioFailure = new InvalidDataException("song.ogg could not be decoded.");

        await h.Session.LoadFile(h.ProjectPath);

        // The chart loaded — it is the file's own content, so it is not dirty.
        Assert.Single(h.State.ActiveProject!.Arrangements);
        Assert.False(h.Session.IsProjectDirty);
    }

    /// <summary>The happy path still declares exactly once, so the move into the
    /// finally is not a behaviour change for it.</summary>
    [Fact]
    public async Task ProjectOpen_ThatSucceeds_IsStillClean()
    {
        using var h = new SessionHarness();

        await h.Session.LoadFile(h.ProjectPath);

        Assert.False(h.Session.IsProjectDirty);
    }

    private static PsarcSongListItem BatCountry() => new(new LibrarySongEntry
    {
        SongKey = "BatCountry",
        Title = "Bat Country",
        PackagePath = @"E:\Rocksmith2014\dlc\bat_country_p.psarc",
        Arrangements = [new LibraryArrangementEntry { ArrangementName = "Combo", SngPath = "songs/bin/x.sng" }]
    }, FakeLocalization.English());

    /// <summary>
    ///     A checkpoint restore is the one replace path where the work at risk is
    ///     guaranteed to exist: it is the dialog a charter opens while unsure, and
    ///     everything done since the checkpoint is what the restore discards.
    /// </summary>
    [Fact]
    public async Task CheckpointRestore_Refused_NeverReadsTheSnapshot()
    {
        var h = new Harness();
        h.Session.Allow = false;
        var vm = h.Checkpoints();

        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)vm.RestoreCommand).ExecuteAsync(vm.Checkpoints[0]);

        Assert.Equal(1, h.Session.AskedCount);
        Assert.Equal(0, h.Checkpointer.RestoreCalls);
    }

    /// <summary>
    ///     The source lint that keeps the set closed. Every method in
    ///     <c>DotCharter.UI</c> that calls <c>ApplyProject(…, resetHistory: true)</c>
    ///     must also ask the guard — a seventh replace path added later cannot
    ///     silently skip it, which is exactly how these six accumulated.
    ///     <para>
    ///         Two methods are exempt, each for a stated reason. Both are cases
    ///         where there provably is no user project to lose or the caller has
    ///         already asked.
    ///     </para>
    /// </summary>
    [Fact]
    public void EveryReplacePathInTheUi_AsksTheGuard()
    {
        // method name -> why it does not ask for itself
        var exempt = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // Startup only: there is no open project yet, so nothing can be lost.
            ["InitializeDefaultProject"] = "startup — no project is open yet",
            // Its ONLY caller is NewProjectCreator.CreateAsync, which asks once for
            // both of its branches before either can run.
            ["CreateEmptyProject"] = "guarded by its only caller, NewProjectCreator.CreateAsync"
        };

        var offenders = new List<string>();
        var seen = 0;

        foreach (var file in ReplacePathSources())
        {

            // Comments are stripped on BOTH sides: half the mentions of
            // "resetHistory: true" in this codebase are the doc comments
            // explaining why the guard exists, and a comment naming the guard
            // must not satisfy it either.
            var lines = UiSourceTree.ReadAllLines(file)
                .Select(line => IsComment(line) ? string.Empty : line)
                .ToArray();

            for (var i = 0; i < lines.Length; i++)
            {
                if (!lines[i].Contains("resetHistory: true", StringComparison.Ordinal)) continue;
                seen++;

                var (name, start) = EnclosingMethod(lines, i);
                if (exempt.ContainsKey(name)) continue;

                var body = string.Join('\n', lines[start..i]);
                if (!body.Contains("MayReplaceOpenProjectAsync", StringComparison.Ordinal))
                    offenders.Add($"{Path.GetFileName(file)}:{i + 1} ({name})");
            }
        }

        // If this drops to zero the lint has stopped looking at anything.
        Assert.True(seen >= 6, $"expected the known replace paths to be found, saw {seen}");
        Assert.True(offenders.Count == 0,
            "these replace the open project without asking MayReplaceOpenProjectAsync first: " +
            string.Join(", ", offenders));
    }

    /// <summary>
    ///     The sibling lint, and the one that would have caught a shipped bug.
    ///     Every replace path must also DECLARE what the new session state is
    ///     relative to disk — <c>MarkProjectClean</c> or <c>MarkProjectDirty</c>.
    ///     <para>
    ///         Silence is not a neutral default, it is a broken one.
    ///         <c>ApplyProject(resetHistory: true)</c> calls
    ///         <c>HistoryService.Clear</c>, which mints a NEW root node id, and
    ///         <c>ProjectDirtyTracker.IsDirty</c> is "current node id ≠ the
    ///         baseline id" — so a path that says nothing leaves a baseline no node
    ///         can ever match again and the session reads dirty until the next
    ///         save. FIVE of the six paths were silent: loading a library song and
    ///         closing raised "The project has unsaved changes" over a chart nobody
    ///         had touched, and the recovery snapshotter (same flag) then offered
    ///         to restore it on the next launch.
    ///     </para>
    ///     <para>
    ///         Deliberately indifferent to WHICH of the two a path picks — that is
    ///         a judgement per path (an importer that just wrote the file is clean;
    ///         a new project that has not been written, and a checkpoint restore
    ///         that deliberately differs from the file, are dirty). What the lint
    ///         enforces is that the judgement was made at all.
    ///     </para>
    /// </summary>
    [Fact]
    public void EveryReplacePathInTheUi_DeclaresItsDirtyState()
    {
        // NO EXEMPTIONS, deliberately.
        //
        // `InitializeDefaultProject` used to have one, on the reasoning that the
        // tracker is baselined when ProjectSessionService is constructed. That was
        // WRONG, and the exemption hid a live bug: the method runs later, from
        // Initialize(), so its Clear() minted a root id the construction-time
        // baseline could never match and the app was dirty from LAUNCH — "*" in
        // the title of a freshly opened window, and an unsaved-changes prompt on
        // closing something nobody had touched. It was found by smoke-running the
        // release build, not by reasoning, which is the point: an exemption here
        // needs evidence, not an argument.
        //
        // The two spellings are equivalent — MarkProjectClean/Dirty is the public
        // wrapper, MarkClean/MarkDirty the tracker's own method, which
        // ProjectSessionService uses because it owns the tracker.
        string[] declarations = ["MarkProjectClean", "MarkProjectDirty", "MarkClean(", "MarkDirty("];

        var offenders = new List<string>();
        var seen = 0;

        foreach (var file in ReplacePathSources())
        {

            // Comments stripped on both sides, same reason as the guard lint: the
            // doc comments explaining this rule must neither trip it nor satisfy it.
            var lines = UiSourceTree.ReadAllLines(file)
                .Select(line => IsComment(line) ? string.Empty : line)
                .ToArray();

            for (var i = 0; i < lines.Length; i++)
            {
                if (!lines[i].Contains("resetHistory: true", StringComparison.Ordinal)) continue;
                seen++;

                var (name, start) = EnclosingMethod(lines, i);

                // The declaration comes AFTER the swap (the state has to exist
                // before you can describe it), so scan the rest of the method.
                var end = MethodEnd(lines, start);
                var body = string.Join('\n', lines[i..end]);
                if (!declarations.Any(d => body.Contains(d, StringComparison.Ordinal)))
                    offenders.Add($"{Path.GetFileName(file)}:{i + 1} ({name})");
            }
        }

        Assert.True(seen >= 6, $"expected the known replace paths to be found, saw {seen}");
        Assert.True(offenders.Count == 0,
            "these replace the open project without declaring MarkProjectClean/MarkProjectDirty, " +
            "so the dirty flag keeps a baseline no history node can ever match: " +
            string.Join(", ", offenders));
    }

    /// <summary>
    ///     The third lint on the same call sites: a replace path's spacing pass
    ///     must run on a WORKER.
    ///     <para>
    ///         <c>ChartSpacingEnforcer.EnforceProject</c> sweeps every level of
    ///         every arrangement, and on a dense multi-arrangement song that is
    ///         hundreds of milliseconds. On a LOAD path it is pure CPU over a
    ///         project the UI has not applied yet — nothing else can see that
    ///         graph, so there is no reason for it to sit on the dispatcher, and
    ///         <c>ProjectSessionService.LoadFile</c> has run it inside its
    ///         <c>Task.Run</c> for exactly that reason since the load pill landed.
    ///         <c>SongBrowserViewModel</c>'s PSARC/library load did the identical
    ///         pass INLINE, between the click and the first frame of the chart it
    ///         had just opened.
    ///     </para>
    ///     <para>
    ///         Deliberately scoped to the replace paths, and that scope is the
    ///         whole point: the other six call sites in <c>DotCharter.UI</c> (the
    ///         beat drag, the event tool, the beat/BPM dialogs' undo callbacks, the
    ///         Songsterr resync, the settings write-back) enforce over the LIVE
    ///         <c>ProjectState.ActiveProject</c> and MUST stay on the UI thread —
    ///         moving one of those to a worker would race the renderer's snapshot.
    ///         A path is a replace path here when its own method also calls
    ///         <c>ApplyProject(</c>, which is the same signal the two lints above
    ///         key on.
    ///     </para>
    /// </summary>
    [Fact]
    public void EveryReplacePathsSpacingEnforce_RunsOnAWorker()
    {
        var offenders = new List<string>();
        var seen = 0;

        foreach (var file in ReplacePathSources())
        {

            // Same masking as the other two: a comment naming Task.Run must not
            // satisfy the lint, and the comment explaining THIS rule sits right
            // above one of the call sites.
            var lines = UiSourceTree.ReadAllLines(file)
                .Select(line => IsComment(line) ? string.Empty : line)
                .ToArray();

            for (var i = 0; i < lines.Length; i++)
            {
                var call = Array.Find(SpacingPassCalls, c => lines[i].Contains(c, StringComparison.Ordinal));
                if (call == null) continue;

                var (name, start) = EnclosingMethod(lines, i);
                var end = MethodEnd(lines, start);
                var isReplacePath = string.Join('\n', lines[start..end])
                    .Contains("ApplyProject(", StringComparison.Ordinal);
                if (!isReplacePath) continue;

                seen++;
                if (!IsInsideATaskRun(lines, start, end, i, call))
                    offenders.Add($"{Path.GetFileName(file)}:{i + 1} ({name})");
            }
        }

        // Both known replace paths, or the lint has stopped looking. It found
        // exactly two when it was written (ProjectSessionService.LoadFile and
        // SongBrowserViewModel's library/PSARC load).
        Assert.True(seen >= 2,
            $"expected the replace paths' spacing passes to be found, saw {seen}");
        Assert.True(offenders.Count == 0,
            "these enforce chart spacing over a not-yet-applied project on the UI thread, " +
            "which lands the whole sweep between the click and the first frame: " +
            string.Join(", ", offenders));
    }

    /// <summary>
    ///     A replace path's spacing pass: the enforcer itself, or the library open's
    ///     worker pass that runs it per arrangement (#436,
    ///     <c>LibraryLevelImport.PrepareForEditor</c>).
    /// </summary>
    private static readonly string[] SpacingPassCalls =
        ["ChartSpacingEnforcer.EnforceProject(", "LibraryLevelImport.PrepareForEditor("];

    /// <summary>
    ///     Where replace paths live: the UI (song browser, dialogs, the shell) and
    ///     the Services-side coordinators that moved out of it (the project
    ///     session's open/new/default paths, chart maintenance).
    /// </summary>
    private static IEnumerable<string> ReplacePathSources()
    {
        // Presentation too: a hosted dialog's view model (Checkpoints' restore) is a replace path.
        foreach (var project in new[] { "DotCharter.UI", "DotCharter.Services", "DotCharter.Tone", "DotCharter.Presentation" })
        {
            foreach (var file in UiSourceTree.EnumerateFiles(Path.Combine(TestPaths.RepoRoot(), project), "*.cs",
                     SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
                    file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                {
                    continue;
                }

                yield return file;
            }
        }
    }

    /// <summary>
    ///     Whether the spacing-pass <paramref name="call" /> on line
    ///     <paramref name="index" /> sits inside the ARGUMENT of a
    ///     <c>Task.Run(</c> somewhere in the enclosing method — the block form and
    ///     the one-line expression lambda alike, since the two call sites this
    ///     lint covers use one each.
    ///     <para>
    ///         Paren matching over the method text with string and char literals
    ///         blanked first. Without the masking a single <c>$"…({x})…"</c> in a
    ///         log line — and these methods are full of log lines — moves the
    ///         closing paren and the answer flips.
    ///     </para>
    /// </summary>
    private static bool IsInsideATaskRun(IReadOnlyList<string> lines, int start, int end, int index, string call)
    {
        var text = new System.Text.StringBuilder();
        var offset = -1;

        for (var i = start; i < end; i++)
        {
            if (i == index) offset = text.Length + lines[i].IndexOf(call, StringComparison.Ordinal);
            text.Append(lines[i]).Append('\n');
        }

        if (offset < 0) return false;
        var masked = MaskLiterals(text.ToString());

        var search = 0;
        while (true)
        {
            var run = masked.IndexOf("Task.Run(", search, StringComparison.Ordinal);
            if (run < 0 || run > offset) return false;

            var openParen = run + "Task.Run".Length;
            var close = MatchingParen(masked, openParen);
            if (openParen < offset && (close < 0 || close > offset)) return true;
            search = run + 1;
        }
    }

    /// <summary>Index of the ')' closing the '(' at <paramref name="open" />, or -1.</summary>
    private static int MatchingParen(string text, int open)
    {
        var depth = 0;
        for (var i = open; i < text.Length; i++)
        {
            if (text[i] == '(') depth++;
            else if (text[i] == ')' && --depth == 0) return i;
        }

        return -1;
    }

    /// <summary>
    ///     Blanks the contents of string and char literals (newlines kept, so
    ///     offsets and line breaks survive) so a brace or paren inside one cannot
    ///     be counted.
    /// </summary>
    private static string MaskLiterals(string text)
    {
        var chars = text.ToCharArray();
        var i = 0;

        while (i < chars.Length)
        {
            if (chars[i] == '@' && i + 1 < chars.Length && chars[i + 1] == '"')
            {
                chars[i] = ' ';
                chars[i + 1] = ' ';
                i += 2;
                while (i < chars.Length)
                {
                    // "" is an escaped quote inside a verbatim string.
                    if (chars[i] == '"' && i + 1 < chars.Length && chars[i + 1] == '"')
                    {
                        chars[i] = ' ';
                        chars[i + 1] = ' ';
                        i += 2;
                        continue;
                    }

                    var done = chars[i] == '"';
                    if (chars[i] != '\n') chars[i] = ' ';
                    i++;
                    if (done) break;
                }

                continue;
            }

            if (chars[i] is '"' or '\'')
            {
                var quote = chars[i];
                chars[i] = ' ';
                i++;
                while (i < chars.Length && chars[i] != '\n')
                {
                    if (chars[i] == '\\')
                    {
                        chars[i] = ' ';
                        if (i + 1 < chars.Length && chars[i + 1] != '\n') chars[i + 1] = ' ';
                        i += 2;
                        continue;
                    }

                    var done = chars[i] == quote;
                    chars[i] = ' ';
                    i++;
                    if (done) break;
                }

                continue;
            }

            i++;
        }

        return new string(chars);
    }

    /// <summary>
    ///     Exclusive end line of the method whose signature is on
    ///     <paramref name="start" />, by brace depth. Falls back to end-of-file so
    ///     a shape the counter cannot parse over-scans (a false PASS on an exotic
    ///     method, never a false failure on a correct one).
    /// </summary>
    private static int MethodEnd(IReadOnlyList<string> lines, int start)
    {
        var depth = 0;
        var opened = false;

        for (var i = start; i < lines.Count; i++)
        {
            foreach (var c in lines[i])
            {
                if (c == '{')
                {
                    depth++;
                    opened = true;
                }
                else if (c == '}')
                {
                    depth--;
                }
            }

            if (opened && depth <= 0) return i + 1;
        }

        return lines.Count;
    }

    private static bool IsComment(string line)
    {
        var trimmed = line.TrimStart();
        return trimmed.StartsWith("//", StringComparison.Ordinal) ||
               trimmed.StartsWith("*", StringComparison.Ordinal);
    }

    /// <summary>Nearest preceding method signature and the line it starts on.</summary>
    private static (string Name, int Start) EnclosingMethod(IReadOnlyList<string> lines, int index)
    {
        var signature = new Regex(
            @"^\s*(?:\[[^\]]*\]\s*)*(?:public|private|protected|internal)\s[^=;]*?\b(\w+)\s*\(",
            RegexOptions.Compiled);

        for (var i = index; i >= 0; i--)
        {
            var match = signature.Match(lines[i]);
            if (match.Success) return (match.Groups[1].Value, i);
        }

        return ("<file>", 0);
    }

    // ---- harness --------------------------------------------------------------

    private sealed class Harness
    {
        private readonly FakeAppLogger _logger = new();

        public Harness()
        {
            TempDir = Directory.CreateTempSubdirectory("dotcharter-replaceguard-").FullName;
            Transport = new TransportViewModel(new FakeAudioPlaybackService(), State, Config, FakeLocalization.KeysOnly());
            var selection = new SelectionService();
            Editor = new ChartEditorViewModel(
                State, Transport, new HistoryService(_logger), selection,
                new TempoService(_logger), new TrackContextRegistry(State, selection), FakeLocalization.KeysOnly());
            // LoadFile's only observable success signal is CurrentProjectPath
            // landing on the snapshot, which is what RecoveryViewModel keys
            // on before it re-points Save at the original file.
            Session.State = State;
        }

        public string TempDir { get; }
        public ProjectState State { get; } = new();
        public FakeConfigurationService Config { get; } = new();
        public FakeFileStorageService Storage { get; } = new();
        public RefusingProjectSessionService Session { get; } = new();
        public FakeLibraryService Library { get; } = new();
        public FakeCheckpointService Checkpointer { get; } = new();
        public FakeCrashRecoveryService Recovery { get; } = new();
        public TransportViewModel Transport { get; }
        public ChartEditorViewModel Editor { get; }

        public NewProjectCreator NewProjectCreator() => new(
            Editor, Transport, State, Session, Storage, Config, new FakeDocumentService(),
            new TempoService(_logger), new DotCharter.Tone.Infrastructure.Storage.ExportTargetWriter(), _logger,
            (_, _, _, _) => throw new InvalidOperationException("no audio in these tests"),
            FakeLocalization.English());

        public SongBrowserViewModel SongBrowser() => new(
            Storage, Config, Library, _logger, Session, State, Editor, Transport,
            new DotCharter.Presentation.Dialogs.DialogsSlot(), FakeLocalization.English());

        public DotCharter.Presentation.Screens.Dialogs.CheckpointsViewModel Checkpoints() => new(
            Checkpointer, new FakeDocumentService(),
            new DotCharter.Presentation.Screens.Dialogs.ChartEditDialogServices(Editor, new HistoryService(_logger),
                State, Config, FakeLocalization.English(), _logger),
            Transport, Session, new DotCharter.Tests.Presentation.Dialogs.ScriptedPrompt());

        /// <summary>Opened the way the startup launcher opens it: over what the crash check finds.</summary>
        public DotCharter.Presentation.Screens.Dialogs.RecoveryViewModel RecoveryDialog()
        {
            var vm = new DotCharter.Presentation.Screens.Dialogs.RecoveryViewModel(Recovery, Session, State,
                FakeLocalization.English(), _logger);
            vm.Configure(new DotCharter.Presentation.Contracts.Dialogs.RecoveryRequest(Recovery.FindRecoverableSessions()));
            return vm;
        }
    }

    private sealed class RefusingProjectSessionService : IProjectSessionService
    {
        public bool Allow { get; set; } = true;
        public int AskedCount { get; private set; }

        /// <summary>Counts the dirty-state DECLARATIONS, which is the whole point:
        /// the shipped bug was a path that made none.</summary>
        public int MarkedCleanCount { get; private set; }

        public int MarkedDirtyCount { get; private set; }

        /// <summary>An audio file the engine cannot decode — the failure that used
        /// to skip the declaration at the end of the try block.</summary>
        public bool ThrowOnLoadAudio { get; set; }

        /// <summary>Set by the harness so <see cref="LoadFile" /> can report the
        /// success signal the recovery dialog reads.</summary>
        public ProjectState? State { get; set; }

        public Task<bool> MayReplaceOpenProjectAsync()
        {
            AskedCount++;
            return Task.FromResult(Allow);
        }

        public void MarkProjectClean() => MarkedCleanCount++;
        public void MarkProjectDirty() => MarkedDirtyCount++;

        public AppConfig AppConfig { get; } = new();
        public bool IsProjectDirty => true;
        public Func<Task<bool>>? ConfirmDiscardAsync { get; set; }
        public event Action? ProjectDirtyChanged { add { } remove { } }

        public void Initialize() { }
        public void LoadAppConfig() { }
        public void SaveAppConfig() { }
        public void FreeAudio() { }

        public Task LoadFile(string filePath)
        {
            State?.CurrentProjectPath = filePath;
            return Task.CompletedTask;
        }

        public Task LoadAudioFile(string filePath, bool generateBeats = true, bool preferMemoryPlayback = false)
            => ThrowOnLoadAudio
                ? Task.FromException(new InvalidDataException($"'{filePath}' could not be decoded."))
                : Task.CompletedTask;

        public Task<bool> AttachAudioAsync(string sourcePath) => Task.FromResult(true);
        public Task LoadSilentTransportAsync(double projectLengthMs) => Task.CompletedTask;
        public Task OpenProject() => Task.CompletedTask;
        public Task<bool> SaveProjectAsync() => Task.FromResult(true);
        public Task<bool> SaveProjectAsAsync() => Task.FromResult(true);
        public IReadOnlyList<RecentProjectEntry> GetRecentProjects() => [];
        public event Action? RecentProjectsChanged { add { } remove { } }
        public void Dispose() { }
    }

    private sealed class FakeCheckpointService : ICheckpointService
    {
        public int RestoreCalls { get; private set; }

        public string RootDirectory => "";
        public IReadOnlyList<CheckpointEntry> List(string? projectIdentity) =>
            [new CheckpointEntry { Id = "cp1", Name = "before the solo" }];

        public CheckpointEntry Create(string? projectIdentity, string? name, ChartProject snapshot,
            string? songName = null, string? appVersion = null) => new();

        public ChartProject Restore(string? projectIdentity, CheckpointEntry entry)
        {
            RestoreCalls++;
            return new ChartProject();
        }

        public bool Delete(string? projectIdentity, string? checkpointId) => true;

        public int CopyTo(string? fromIdentity, string toIdentity) => 0;
    }

    /// <summary>
    ///     The REAL <see cref="ProjectSessionService" /> over fakes, laid out like a
    ///     project on disk: a <c>.rscp</c> and the <c>song.ogg</c> it references
    ///     (the audio branch is gated on <c>File.Exists</c>).
    /// </summary>
    private sealed class SessionHarness : IDisposable
    {
        private readonly string _tempDir =
            Directory.CreateTempSubdirectory("dotcharter-loaddirty-").FullName;

        public SessionHarness()
        {
            var logger = new FakeAppLogger();
            ProjectPath = Path.Combine(_tempDir, "project.rscp");
            File.WriteAllText(ProjectPath, "<project/>");
            File.WriteAllBytes(Path.Combine(_tempDir, "song.ogg"), [0x4F, 0x67, 0x67, 0x53]);

            var transport = new TransportViewModel(Audio, State, Config, FakeLocalization.KeysOnly());
            var selection = new SelectionService();
            var history = new HistoryService(logger);
            var editor = new ChartEditorViewModel(
                State, transport, history, selection,
                new TempoService(logger), new TrackContextRegistry(State, selection), FakeLocalization.KeysOnly());

            Session = new ProjectSessionService(
                Audio, Config, logger, new FakeFileStorageService(),
                new AudiolessNameDocumentService(), State, transport, editor, new TempoService(logger),
                new FakeCrashRecoveryService(), history, new NoOpSaveCompanionService(),
                uiDispatcher: new TestUiDispatcher(),
                localization: FakeLocalization.English(),
                pathRekey: new FakeProjectPathRekey());
        }

        public string ProjectPath { get; }
        public ProjectState State { get; } = new();
        public FakeConfigurationService Config { get; } = new();
        public FakeAudioPlaybackService Audio { get; } = new();
        public ProjectSessionService Session { get; }

        public void Dispose()
        {
            Session.Dispose();
            BestEffortDelete.Directory(_tempDir);
        }
    }

    private sealed class AudiolessNameDocumentService : IProjectDocumentService
    {
        public ChartProject LoadChart(string filePath)
        {
            var project = new ChartProject { AudioFileName = "song.ogg" };
            var arr = new Arrangement { Name = "Lead Guitar" };
            for (var i = 0; i < 6; i++) arr.TuningPitches.Add(new TuningOffset(0));
            arr.Levels.Add(new ChartLevel { DifficultyIndex = 0 });
            project.Arrangements.Add(arr);
            return project;
        }

        public void SaveChart(string filePath, ChartProject project, double? audioLengthMs = null) { }
        public void RefreshArrangementFileManifest(ChartProject project) { }

        public void SaveDlcBuilderXmlBundle(string projectPath, ChartProject project,
            double? audioLengthMs = null)
        { }

        public ChartProject AssembleProject(ChartProject? baseProject, double bpm, string audioFileName,
            IEnumerable<LyricPhrase> lyrics) => baseProject ?? new ChartProject();

        public ChartProject CreateNewProject(string title, string artist, string audioFileName)
            => new() { AudioFileName = audioFileName };
    }

    private sealed class NoOpSaveCompanionService : ISaveCompanionService
    {
        public string? WriteEof(string projectPath, ChartProject snapshot) => null;

        public string? WriteDlcBuilderProject(
            string projectPath, ChartProject snapshot, double? audioLengthMs) => null;

        public Task<string?> BuildPsarcAsync(string projectPath, ChartProject snapshot,
            double? audioLengthMs, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
    }

    private sealed class FakeCrashRecoveryService : ICrashRecoveryService
    {
        /// <summary>No store behind this fake, so nothing counts as an internal
        /// snapshot. Settable for the test that needs a real recovery root.</summary>
        public string SnapshotRootDirectory { get; set; } = string.Empty;

        public IReadOnlyList<RecoverableSession> Sessions { get; set; } = [];

        public void Start() { }
        public IReadOnlyList<RecoverableSession> FindRecoverableSessions() => Sessions;
        public void DiscardSession(RecoverableSession session) { }
        public void NotifyProjectSaved(string projectPath) { }
        public void NotifyProjectSwitched() { }
        public void NotifyCleanShutdown() { }
        public Func<bool>? HasUnsavedChanges { get; set; }
        public void NotifyProjectChanged() { }
        public void Dispose() { }
    }

    private sealed class FakeLibraryService : ILibraryService
    {
        public int LoadCalls { get; private set; }

        /// <summary>Non-empty makes the loaded song take the audio branch —
        /// the one whose failure used to skip the dirty-state declaration.</summary>
        public string PlayableAudioPath { get; set; } = "";

        /// <summary>A failure BEFORE ApplyProject: nothing has been replaced, so
        /// nothing may be declared.</summary>
        public bool ThrowOnLoad { get; set; }

        public Task<LibraryLoadedSong> LoadSongAsync(LibrarySongEntry song)
        {
            LoadCalls++;
            if (ThrowOnLoad) throw new InvalidDataException("the psarc is corrupt.");
            var project = new ChartProject();
            var arr = new Arrangement { Name = "Combo" };
            arr.Levels.Add(new ChartLevel { DifficultyIndex = 0 });
            project.Arrangements.Add(arr);
            return Task.FromResult(new LibraryLoadedSong
            {
                Project = project, PlayableAudioPath = PlayableAudioPath, ExtractedAudioPath = ""
            });
        }

        public Task<IReadOnlyList<LibrarySongEntry>> ScanAsync(string rootPath) => throw new NotSupportedException();

        public Task<IReadOnlyList<LibrarySongEntry>> ScanAsync(string rootPath,
            IProgress<LibraryScanProgress>? progress, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<LibrarySongEntry>> ScanAsync(string rootPath,
            IProgress<LibraryScanProgress>? progress, IProgress<IReadOnlyList<LibrarySongEntry>>? songFound,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<string> LoadCoverImageAsync(LibrarySongEntry song) => Task.FromResult("");
    }

    private sealed class FakeDocumentService : IProjectDocumentService
    {
        public ChartProject LoadChart(string filePath) => throw new NotSupportedException();
        public void SaveChart(string filePath, ChartProject project, double? audioLengthMs = null) { }
        public void RefreshArrangementFileManifest(ChartProject project) { }

        public void SaveDlcBuilderXmlBundle(string projectPath, ChartProject project,
            double? audioLengthMs = null)
        { }

        public ChartProject AssembleProject(ChartProject? baseProject, double bpm, string audioFileName,
            IEnumerable<LyricPhrase> lyrics) => baseProject ?? new ChartProject();

        public ChartProject CreateNewProject(string title, string artist, string audioFileName)
        {
            var project = new ChartProject();
            var arr = new Arrangement { Name = "Lead Guitar" };
            for (var i = 0; i < 6; i++) arr.TuningPitches.Add(new TuningOffset(0));
            arr.Levels.Add(new ChartLevel { DifficultyIndex = 0 });
            project.Arrangements.Add(arr);
            project.Chart = new ChartData { SongName = title, Artist = artist };
            project.AudioFileName = audioFileName;
            return project;
        }
    }
}
