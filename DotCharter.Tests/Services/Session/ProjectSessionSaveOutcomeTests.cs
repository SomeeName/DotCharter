using System.Collections.ObjectModel;
using DotCharter.Core.Domain.Vocals;
using DotCharter.Services.Ai.Chat;
using DotCharter.Services.Diagnostics;
using DotCharter.Services.Editing.Selection;
using DotCharter.Services.Export;
using DotCharter.Services.Infrastructure.Documents;
using DotCharter.Services.Infrastructure.Recovery;
using DotCharter.Services.Infrastructure.Storage;
using DotCharter.Services.Parsing.Rscp;
using DotCharter.Tone.Diagnostics;
using DotCharter.Tone.Infrastructure.Storage;
using DotCharter.UI.ViewModels.Dialogs;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.Services.Editing.Tracks;
using DotCharter.Services.Session;
using DotCharter.Services.State.Checkpoints;

namespace DotCharter.Tests.Services.Session;

/// <summary>
///     SaveProjectCoreAsync's guarantee: once the <c>.rscp</c> has been swapped
///     atomically into place the save HAS succeeded, and nothing derived may
///     report otherwise. <c>DeleteLegacyProjectXmlIfPresent</c> — a bare
///     <c>File.Delete</c> of the legacy sibling <c>.xml</c> — sat one line ABOVE
///     the two try/catch blocks that exist for exactly this, so a locked or
///     read-only sibling threw into the outer catch and produced
///     <c>Status_SaveError</c>, a still-dirty project, no
///     <c>NotifyProjectSaved</c> (a later hard kill then re-offered a stale crash
///     snapshot) and no psarc companion — for a project that was already safely
///     on disk.
///     <para>
///         Driven through the <c>internal</c> <c>SaveProjectCoreAsync</c> rather
///         than the public entry point: the latter hops through
///         <c>Dispatcher.UIThread.InvokeAsync</c>, and Avalonia's UI dispatcher
///         binds to whichever thread touches it first in the process — so from a
///         plain xunit thread the hop simply never completes.
///     </para>
/// </summary>
public sealed class ProjectSessionSaveOutcomeTests : IDisposable
{
    private readonly string _tempDir =
        Directory.CreateTempSubdirectory("dotcharter-saveoutcome-").FullName;

    public void Dispose() => BestEffortDelete.Directory(_tempDir);

    [Fact]
    public async Task SaveProjectCore_WithALockedLegacySiblingXml_StillReportsSuccess()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Relies on Windows file locking.");

        var h = CreateHarness(out var projectPath);
        var legacyXmlPath = Path.ChangeExtension(projectPath, ".xml");
        File.WriteAllText(legacyXmlPath, "<song id=\"legacy\" />");

        bool saved;
        using (new FileStream(legacyXmlPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            saved = await h.Session.Saver.SaveProjectCoreAsync(projectPath);
        }

        Assert.True(saved);
        // The .rscp really is on disk, and the session agrees the work is saved.
        Assert.True(File.Exists(projectPath));
        Assert.False(h.Session.IsProjectDirty);
        Assert.Equal(projectPath, Assert.Single(h.Recovery.SavedProjectPaths));
        Assert.False(File.Exists(projectPath + ".saving"));
    }

    /// <summary>The cleanup itself must still happen when nothing blocks it.</summary>
    [Fact]
    public async Task SaveProjectCore_DeletesAnUnlockedLegacySiblingXml()
    {
        var h = CreateHarness(out var projectPath);
        var legacyXmlPath = Path.ChangeExtension(projectPath, ".xml");
        File.WriteAllText(legacyXmlPath, "<song id=\"legacy\" />");

        Assert.True(await h.Session.Saver.SaveProjectCoreAsync(projectPath));

        Assert.False(File.Exists(legacyXmlPath));
    }

    /// <summary>
    ///     Every companion is DERIVED, and the <c>.rscp</c> is already on disk by the
    ///     time one runs — so a companion that throws must not turn a successful save
    ///     into "project save failed", leave the session dirty, or skip
    ///     <c>NotifyProjectSaved</c>.
    /// </summary>
    [Theory]
    [InlineData(true, false)]   // .rs2dlc throws
    [InlineData(false, true)]   // .eof throws
    [InlineData(true, true)]    // both throw
    public async Task SaveProjectCore_WithAThrowingCompanion_StillReportsSuccess(
        bool throwOnRs2Dlc, bool throwOnEof)
    {
        var h = CreateHarness(out var projectPath,
            new ThrowingSaveCompanionService(throwOnRs2Dlc, throwOnEof));

        Assert.True(await h.Session.Saver.SaveProjectCoreAsync(projectPath));

        Assert.True(File.Exists(projectPath));
        Assert.False(h.Session.IsProjectDirty);
        Assert.Equal(projectPath, Assert.Single(h.Recovery.SavedProjectPaths));
    }

    /// <summary>
    ///     The save funnel is one of the two writers of the Open Recent list
    ///     (the open funnel is the other): a successful save records the path,
    ///     raises the change event, and <c>GetRecentProjects</c> serves it back
    ///     because the file really exists on disk.
    /// </summary>
    [Fact]
    public async Task SaveProjectCore_RecordsTheProjectInOpenRecent()
    {
        var h = CreateHarness(out var projectPath);
        var changed = 0;
        h.Session.RecentProjectsChanged += () => changed++;

        Assert.True(await h.Session.Saver.SaveProjectCoreAsync(projectPath));

        var entry = Assert.Single(h.Config.AppConfig.RecentProjects);
        Assert.Equal(Path.GetFullPath(projectPath), entry.Path);
        Assert.True(changed >= 1, "RecentProjectsChanged never fired");
        Assert.Single(h.Session.GetRecentProjects());
    }

    /// <summary>…and the served list drops an entry whose file is gone.</summary>
    [Fact]
    public async Task GetRecentProjects_DropsAnEntryWhoseFileWasDeleted()
    {
        var h = CreateHarness(out var projectPath);
        Assert.True(await h.Session.Saver.SaveProjectCoreAsync(projectPath));
        Assert.Single(h.Session.GetRecentProjects());

        File.Delete(projectPath);

        Assert.Empty(h.Session.GetRecentProjects());
    }

    // ---- feedback: the pill, and the failure the user must not miss -----------

    /// <summary>
    ///     Ctrl+S has an unindicated UI-thread stall (the enforce, the assemble
    ///     and the deep clone all run before the first await, by contract). The
    ///     save now carries a status-bar task pill across the whole of it —
    ///     begun BEFORE the stall so it describes it, and completed whatever the
    ///     outcome.
    /// </summary>
    [Fact]
    public async Task SaveProjectCore_CarriesAStatusBarTaskAcrossTheWholeSave()
    {
        var h = CreateHarness(out var projectPath);

        Assert.True(await h.Session.Saver.SaveProjectCoreAsync(projectPath));

        Assert.Equal(["project-save"], h.StatusBar.Begun);
        Assert.Equal(["project-save"], h.StatusBar.Completed);
        // Begun before the work: the pill exists to explain a stall it must not
        // arrive after.
        Assert.True(h.StatusBar.WasIndeterminate);
    }

    /// <summary>
    ///     A failed save leaves the session DIRTY and the user believing their
    ///     work is on disk — the one outcome that may not be a faint line in the
    ///     status rail. It goes to the app-wide error dialog (already this
    ///     service's dependency), and the pill is still cleared.
    /// </summary>
    [Fact]
    public async Task SaveProjectCore_WhenTheWriteFails_SurfacesAnErrorDialog_AndStillClearsThePill()
    {
        var h = CreateHarness(out var projectPath, documentService: new ThrowingDocumentService());

        Assert.False(await h.Session.Saver.SaveProjectCoreAsync(projectPath));

        Assert.True(h.Session.IsProjectDirty || h.Recovery.SavedProjectPaths.Count == 0);
        Assert.Equal(["project-save"], h.StatusBar.Completed);

        var shown = Assert.Single(h.Messages.Shown);
        Assert.Equal(MessageDialogKind.Error, shown.Kind);
        Assert.Contains(projectPath, shown.Message);
        // The exception text rides along as the dialog's DETAILS, so the report
        // is actionable without opening the log.
        Assert.Contains("disk full", shown.Details);
    }

    /// <summary>
    ///     The pill EXISTING is not the same as the pill being SEEN. Everything
    ///     before the first await runs straight through on the UI thread, so
    ///     without a deliberate yield the dispatcher's first chance to draw
    ///     anything is after the stall the pill exists to explain. The save
    ///     therefore waits one Background-priority turn (below Render, so a
    ///     frame really has gone out) before it starts.
    ///     <para>
    ///         Proved by holding that post: nothing on disk, and the save has not
    ///         returned, until it is released.
    ///     </para>
    /// </summary>
    [Fact]
    public async Task SaveProjectCore_WaitsForAFrameBeforeItStartsTheUiThreadWork()
    {
        var dispatcher = new HeldUiDispatcher();
        var h = CreateHarness(out var projectPath, uiDispatcher: dispatcher);

        var save = h.Session.Saver.SaveProjectCoreAsync(projectPath);

        Assert.True(dispatcher.Posted.Count >= 1, "the save never yielded to the dispatcher");
        Assert.False(save.IsCompleted, "the save ran the UI-thread work before a frame could go out");
        Assert.False(File.Exists(projectPath));

        dispatcher.Release();
        Assert.True(await save);
        Assert.True(File.Exists(projectPath));
    }

    /// <summary>Holds every post until told, so the yield can be observed.</summary>
    private sealed class HeldUiDispatcher : IUiDispatcher
    {
        public List<Action> Posted { get; } = [];

        public void Release()
        {
            var pending = Posted.ToArray();
            Posted.Clear();
            foreach (var action in pending) action();
        }

        public bool CheckAccess() => true;
        public void Post(Action action) => Posted.Add(action);
        public void PostNormal(Action action) => Posted.Add(action);
        public Task InvokeAsync(Action action) { action(); return Task.CompletedTask; }
        public Task<T> InvokeAsync<T>(Func<T> func) => Task.FromResult(func());
        public Task<T> InvokeAsync<T>(Func<Task<T>> func) => func();
    }

    /// <summary>A save that worked must not raise an error dialog.</summary>
    [Fact]
    public async Task SaveProjectCore_OnSuccess_ShowsNoErrorDialog()
    {
        var h = CreateHarness(out var projectPath);

        Assert.True(await h.Session.Saver.SaveProjectCoreAsync(projectPath));

        Assert.Empty(h.Messages.Shown);
    }

    // ---- harness --------------------------------------------------------------

    /// <summary>
    ///     Save As assigned <c>CurrentProjectPath</c> and <c>LastRscpDir</c> the
    ///     moment the picker returned, BEFORE the write was attempted. A failed
    ///     Save As therefore repointed the whole session at a file that does not
    ///     exist — the title bar named it, the next plain Ctrl+S wrote there
    ///     without asking, and the project the user actually had open was no
    ///     longer reachable from anywhere in the app.
    /// </summary>
    [Fact]
    public async Task SaveAs_ThatFailsToWrite_LeavesTheSessionOnTheProjectItHadOpen()
    {
        var h = CreateHarness(out var projectPath);
        h.Config.AppConfig.LastRscpDir = _tempDir;

        // A FILE where the picked path needs a DIRECTORY: every write under it
        // throws, and no permission fiddling or locking is involved.
        var blocker = Path.Combine(_tempDir, "blocker");
        await File.WriteAllTextAsync(blocker, "not a directory", TestContext.Current.CancellationToken);
        var picked = Path.Combine(blocker, "elsewhere.rscp");

        Assert.False(await h.Session.Saver.SaveToPickedPathAsync(picked));

        Assert.Equal(projectPath, h.State.CurrentProjectPath);
        Assert.Equal(_tempDir, h.Config.AppConfig.LastRscpDir);
    }

    /// <summary>…and a Save As that SUCCEEDS still moves both, or the feature is
    /// gone rather than fixed.</summary>
    [Fact]
    public async Task SaveAs_ThatSucceeds_MovesTheSessionOntoTheNewPath()
    {
        var h = CreateHarness(out _);
        var targetDir = Path.Combine(_tempDir, "Band - Other Song");
        Directory.CreateDirectory(targetDir);
        var picked = Path.Combine(targetDir, "other.rscp");

        Assert.True(await h.Session.Saver.SaveToPickedPathAsync(picked));

        Assert.True(File.Exists(picked));
        Assert.Equal(picked, h.State.CurrentProjectPath);
        Assert.Equal(targetDir, h.Config.AppConfig.LastRscpDir);
    }

    /// <summary>
    ///     #287: checkpoints are keyed by the project's path, so Save As used to open the new file
    ///     with an empty Checkpoints list. They are copied over; the old path keeps its own.
    /// </summary>
    [Fact]
    public async Task SaveAs_CarriesTheProjectsCheckpointsToTheNewPath()
    {
        var h = CreateHarness(out var projectPath);
        var first = h.Checkpoints.Create(projectPath, "Before the solo", new ChartProject());
        h.Checkpoints.Create(projectPath, "After the solo", new ChartProject());
        var picked = Path.Combine(_tempDir, "variant.rscp");

        Assert.True(await h.Session.Saver.SaveToPickedPathAsync(picked));

        var carried = h.Checkpoints.List(picked);
        Assert.Equal(["After the solo", "Before the solo"], carried.Select(c => c.Name).Order());
        Assert.NotNull(h.Checkpoints.Restore(picked, carried.Single(c => c.Id == first.Id)));
        Assert.Equal(2, h.Checkpoints.List(projectPath).Count);

        // A second Save As back onto a path that already holds them copies nothing twice.
        Assert.Equal(0, h.Checkpoints.CopyTo(projectPath, picked));
    }

    /// <summary>
    ///     #633: the AI chats live beside the project (<c>ai-chats/&lt;chart id&gt;/</c>) and Save As
    ///     keeps the chart id, so a Save As into another folder opened with an empty chat history.
    ///     They are COPIED: the chat history, now reading the new folder, lists both, and the old
    ///     project's files are byte-for-byte what they were.
    /// </summary>
    [Fact]
    public async Task SaveAs_IntoAnotherFolder_CarriesTheChartsAiChats_AndLeavesTheOriginals()
    {
        var h = CreateHarness(out var projectPath);
        var chats = new AiChatHistory(new ProjectStateAiChatChartSource(h.State));
        var first = await SaveChat(chats, "Mark the sections");
        var second = await SaveChat(chats, "Finger the chorus");
        var oldFolder = ChatFolder(h, projectPath);
        var before = Directory.GetFiles(oldFolder).ToDictionary(f => Path.GetFileName(f), File.ReadAllBytes);
        Assert.Equal(2, before.Count);
        var picked = Path.Combine(_tempDir, "Somewhere Else", "copy.rscp");
        Directory.CreateDirectory(Path.GetDirectoryName(picked)!);

        Assert.True(await h.Session.Saver.SaveToPickedPathAsync(picked));

        var listing = await chats.ListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(AiChatStatus.Ok, listing.Status);
        Assert.Empty(listing.Refused);
        Assert.Equal(new[] { first.Id, second.Id }.Order(), listing.Conversations.Select(c => c.Id).Order());
        foreach (var (name, bytes) in before) Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(oldFolder, name)));
        Assert.Empty(Directory.GetFiles(ChatFolder(h, picked), "*.carrying"));
    }

    /// <summary>#633: a conversation already at the target is never replaced; the others still come over.</summary>
    [Fact]
    public async Task SaveAs_LeavesAChatAlreadyAtTheTargetAlone_AndCarriesTheRest()
    {
        var h = CreateHarness(out _);
        var chats = new AiChatHistory(new ProjectStateAiChatChartSource(h.State));
        var first = await SaveChat(chats, "Mark the sections");
        var second = await SaveChat(chats, "Finger the chorus");
        var picked = Path.Combine(_tempDir, "Somewhere Else", "copy.rscp");
        var targetFolder = ChatFolder(h, picked);
        Directory.CreateDirectory(targetFolder);
        var theirs = AiChatJson.Serialize(first with { Title = "Already here" });
        await File.WriteAllBytesAsync(Path.Combine(targetFolder, first.Id + ".json"), theirs, TestContext.Current.CancellationToken);

        Assert.True(await h.Session.Saver.SaveToPickedPathAsync(picked));

        Assert.Equal(theirs, File.ReadAllBytes(Path.Combine(targetFolder, first.Id + ".json")));
        var listing = await chats.ListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(["Already here", "Finger the chorus"], listing.Conversations.Select(c => c.Title).Order());
        Assert.Contains(listing.Conversations, c => c.Id == second.Id);
        Assert.Contains(h.Logger.Entries, e => e.Message.Contains("carried 1 AI chat(s)", StringComparison.Ordinal) &&
                                               e.Message.Contains("1 already there", StringComparison.Ordinal));
    }

    /// <summary>#633: a copy that fails never fails the save, never loses a chat, and is logged.</summary>
    [Fact]
    public async Task SaveAs_WhoseChatCopyFails_StillSaves_KeepsTheOriginals_AndLogsIt()
    {
        var h = CreateHarness(out var projectPath);
        var chats = new AiChatHistory(new ProjectStateAiChatChartSource(h.State));
        await SaveChat(chats, "Mark the sections");
        var oldFolder = ChatFolder(h, projectPath);
        var before = Directory.GetFiles(oldFolder).ToDictionary(f => Path.GetFileName(f), File.ReadAllBytes);
        var targetDir = Path.Combine(_tempDir, "Somewhere Else");
        Directory.CreateDirectory(targetDir);
        // A FILE where the chats' folder has to go: every copy under it throws.
        await File.WriteAllTextAsync(Path.Combine(targetDir, "ai-chats"), "not a folder", TestContext.Current.CancellationToken);

        Assert.True(await h.Session.Saver.SaveToPickedPathAsync(Path.Combine(targetDir, "copy.rscp")));

        foreach (var (name, bytes) in before) Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(oldFolder, name)));
        Assert.Contains(h.Logger.Entries, e => e.Level == "WARN" &&
                                               e.Message.Contains("could not carry an AI chat", StringComparison.Ordinal));
    }

    private static async Task<AiChatConversation> SaveChat(AiChatHistory chats, string text)
    {
        var conversation = chats.StartNew("claude-sonnet-5-5")!
            .Append(new AiChatTurn(AiChatRole.User, text, DateTimeOffset.UtcNow));
        Assert.Equal(AiChatStatus.Ok, (await chats.SaveAsync(conversation, TestContext.Current.CancellationToken)).Status);
        return conversation;
    }

    private static string ChatFolder(Harness h, string projectPath) =>
        Path.Combine(Path.GetDirectoryName(projectPath)!, "ai-chats", h.State.ActiveProject!.ProjectId);

    /// <summary>The untitled bucket is shared by every unsaved session, so a first save claims none of it.</summary>
    [Fact]
    public void The_shared_untitled_bucket_is_never_carried()
    {
        var store = new CheckpointService(Path.Combine(_tempDir, "checkpoints-untitled"));
        store.Create(CheckpointIdentity.For(null, null), "someone else's", new ChartProject());

        Assert.Equal(0, store.CopyTo(CheckpointIdentity.For(null, null), Path.Combine(_tempDir, "new.rscp")));
        Assert.Empty(store.List(Path.Combine(_tempDir, "new.rscp")));
    }

    /// <summary>
    ///     …and it must PERSIST that directory, not merely assign it. The line was
    ///     a bare <c>AppConfig.LastRscpDir = dir</c> on the object
    ///     <c>LoadAppConfig</c> hands back — which is the CACHED instance, so
    ///     against the real <c>JsonConfigurationService</c> nothing was written
    ///     unless some later, unrelated config write happened to be armed. File ▸
    ///     Open persists its own directory (<c>SaveAppConfig</c>); Save As did
    ///     not, so a Save As into a new folder was forgotten by the next launch.
    ///     Worse than a plain omission: <c>SaveProjectCoreAsync</c> has just
    ///     REPLACED <c>this.AppConfig</c> with a fresh load, so the assignment
    ///     landed on an object nothing was going to write.
    ///     <para>
    ///         Reading <c>AppConfig</c> afterwards cannot see this — the fake
    ///         serves one mutable instance, which is exactly why the case above
    ///         passed throughout — so this reads the fake's record of what each
    ///         persisting call actually carried.
    ///     </para>
    /// </summary>
    [Fact]
    public async Task SaveAs_PersistsTheNewFolderAsTheNextPickersStartingPoint()
    {
        var h = CreateHarness(out _);
        var targetDir = Path.Combine(_tempDir, "Band - Third Song");
        Directory.CreateDirectory(targetDir);

        Assert.True(await h.Session.Saver.SaveToPickedPathAsync(Path.Combine(targetDir, "third.rscp")));

        Assert.Contains(h.Config.Writes, w => w.LastRscpDir == targetDir);
    }

    /// <summary>
    ///     A project's audio is a BARE FILE NAME resolved against the project's
    ///     own folder, and Save As is the one operation that changes that folder.
    ///     The new <c>.rscp</c> carried the same bare name while the audio stayed
    ///     behind in the old folder, so the saved copy opened with no waveform,
    ///     no playback, no stems and nothing at all saying why. The chart
    ///     survived; everything that makes it playable did not.
    /// </summary>
    [Fact]
    public async Task SaveAs_IntoAFolderTheAudioIsNotUnder_KeepsTheAudioReachable()
    {
        var h = CreateHarness(out var projectPath);
        var audioPath = Path.Combine(Path.GetDirectoryName(projectPath)!, "song.ogg");
        await File.WriteAllTextAsync(audioPath, "not really audio", TestContext.Current.CancellationToken);

        var targetDir = Path.Combine(_tempDir, "Somewhere Else");
        Directory.CreateDirectory(targetDir);
        var picked = Path.Combine(targetDir, "copy.rscp");

        // Before: the bare name is unreachable from the folder we are about to
        // save into — the whole reason this needs doing.
        Assert.Null(ProjectAudioLocator.TryResolve(picked, "song.ogg"));

        Assert.True(await h.Session.Saver.SaveToPickedPathAsync(picked));

        Assert.Equal(audioPath, ProjectAudioLocator.TryResolve(picked, h.State.CurrentAudioFileName));
    }

    /// <summary>
    ///     …and the ordinary Save As — a second name for the same song, in the
    ///     same folder — keeps its PORTABLE relative reference. Promoting every
    ///     save to an absolute path would break the folder-move case this format
    ///     is relative for.
    /// </summary>
    [Fact]
    public async Task SaveAs_NextToTheAudio_KeepsTheRelativeReference()
    {
        var h = CreateHarness(out var projectPath);
        var projectDir = Path.GetDirectoryName(projectPath)!;
        await File.WriteAllTextAsync(Path.Combine(projectDir, "song.ogg"), "not really audio",
            TestContext.Current.CancellationToken);

        Assert.True(await h.Session.Saver.SaveToPickedPathAsync(Path.Combine(projectDir, "variant.rscp")));

        Assert.Equal("song.ogg", h.State.CurrentAudioFileName);
    }

    /// <summary>An audio reference that was ALREADY unresolvable is left alone —
    /// there is nothing to promote it to, and inventing a path bakes in a
    /// lie.</summary>
    [Fact]
    public async Task SaveAs_WithAnAudioReferenceThatResolvesNowhere_ChangesNothing()
    {
        var h = CreateHarness(out _);
        var targetDir = Path.Combine(_tempDir, "No Audio Anywhere");
        Directory.CreateDirectory(targetDir);

        Assert.True(await h.Session.Saver.SaveToPickedPathAsync(Path.Combine(targetDir, "copy.rscp")));

        Assert.Equal("song.ogg", h.State.CurrentAudioFileName);
    }

    /// <summary>
    ///     Crash recovery armed its snapshot off the undo history alone, so
    ///     project-level state the history cannot represent — attaching or
    ///     replacing the song audio is the case <c>MarkDirty</c> exists for — was
    ///     never snapshotted at all.
    /// </summary>
    [Fact]
    public void MarkProjectDirty_ArmsACrashRecoverySnapshot_EveryTime()
    {
        var h = CreateHarness(out _);
        Assert.Equal(0, h.Recovery.ProjectChangedCount);

        h.Session.MarkProjectDirty();
        Assert.Equal(1, h.Recovery.ProjectChangedCount);

        // Already dirty, so the edge-triggered IsDirtyChanged stays silent — this
        // must not, or "edit, autosave, then attach audio" loses the attach.
        h.Session.MarkProjectDirty();
        Assert.Equal(2, h.Recovery.ProjectChangedCount);
    }

    private Harness CreateHarness(out string projectPath, ISaveCompanionService? companions = null,
        IProjectDocumentService? documentService = null, IUiDispatcher? uiDispatcher = null)
    {
        var projectDir = Path.Combine(_tempDir, "Band - Song");
        Directory.CreateDirectory(projectDir);
        projectPath = Path.Combine(projectDir, "project.rscp");

        var project = new ChartProject { AudioFileName = "song.ogg" };
        project.Arrangements.Add(new Arrangement { Name = "Lead Guitar" });
        return new Harness(project, projectPath, companions ?? new NoOpSaveCompanionService(),
            documentService ?? new RealRscpDocumentService(), uiDispatcher,
            new CheckpointService(Path.Combine(_tempDir, "checkpoints")));
    }

    /// <summary>Fails the .rscp write itself — the only failure the outer catch
    /// is allowed to report, everything after it being derived.</summary>
    private sealed class ThrowingDocumentService : IProjectDocumentService
    {
        private readonly RealRscpDocumentService _real = new();

        public ChartProject LoadChart(string filePath) => _real.LoadChart(filePath);

        public void SaveChart(string filePath, ChartProject project, double? audioLengthMs = null)
            => throw new IOException("disk full");

        public void RefreshArrangementFileManifest(ChartProject project) { }

        public void SaveDlcBuilderXmlBundle(string projectPath, ChartProject project,
            double? audioLengthMs = null)
        { }

        public ChartProject AssembleProject(ChartProject? baseProject, double bpm, string audioFileName,
            IEnumerable<LyricPhrase> lyrics) => _real.AssembleProject(baseProject, bpm, audioFileName, lyrics);

        public ChartProject CreateNewProject(string title, string artist, string audioFileName)
            => _real.CreateNewProject(title, artist, audioFileName);
    }

    private sealed record ShownMessage(string Title, string Message, MessageDialogKind Kind, string? Details);

    /// <summary>Records instead of showing: the real ShowAsync hops
    /// Dispatcher.UIThread, which never completes on a plain test thread.</summary>
    private sealed class RecordingMessageDialog : MessageDialogViewModel
    {
        public List<ShownMessage> Shown { get; } = [];

        public override Task<bool> ShowAsync(string title, string message,
            MessageDialogKind kind = MessageDialogKind.Info, string? details = null,
            bool confirmation = false)
        {
            Shown.Add(new ShownMessage(title, message, kind, details));
            return Task.FromResult(true);
        }
    }

    private sealed class RecordingStatusBar : IStatusBarService
    {
        public List<string> Begun { get; } = [];
        public List<string> Completed { get; } = [];
        public bool WasIndeterminate { get; private set; }

        public ObservableCollection<StatusBarMessage> RecentMessages { get; } = [];
        public ObservableCollection<StatusBarTask> ActiveTasks { get; } = [];

        public StatusBarTask BeginTask(string id, string label, bool indeterminate = true, Action? cancel = null)
        {
            Begun.Add(id);
            WasIndeterminate = indeterminate;
            return new StatusBarTask(id, label, indeterminate);
        }

        public void ReportProgress(string id, double progress, string? newLabel = null) { }
        public void CompleteTask(string id) => Completed.Add(id);
        public void ClearMessages() { }
    }

    private sealed class ThrowingSaveCompanionService(bool throwOnRs2Dlc, bool throwOnEof)
        : ISaveCompanionService
    {
        public string? WriteEof(string projectPath, ChartProject snapshot) =>
            throwOnEof ? throw new IOException("eof companion exploded") : null;

        public string? WriteDlcBuilderProject(
            string projectPath, ChartProject snapshot, double? audioLengthMs) =>
            throwOnRs2Dlc ? throw new IOException(".rs2dlc companion exploded") : null;

        public Task<string?> BuildPsarcAsync(string projectPath, ChartProject snapshot,
            double? audioLengthMs, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
    }

    private sealed class RecordingCrashRecoveryService : ICrashRecoveryService
    {
        /// <summary>No store behind this fake, so nothing counts as an internal
        /// snapshot. Settable for the test that needs a real recovery root.</summary>
        public string SnapshotRootDirectory { get; set; } = string.Empty;

        public List<string> SavedProjectPaths { get; } = [];
        public int ProjectChangedCount { get; private set; }

        public void Start() { }
        public IReadOnlyList<RecoverableSession> FindRecoverableSessions() => [];
        public void DiscardSession(RecoverableSession session) { }
        public void NotifyProjectSaved(string projectPath) => SavedProjectPaths.Add(projectPath);
        public void NotifyProjectSwitched() { }
        public void NotifyCleanShutdown() { }
        public Func<bool>? HasUnsavedChanges { get; set; }
        public void NotifyProjectChanged() => ProjectChangedCount++;
        public void Dispose() { }
    }

    private sealed class NoOpSaveCompanionService : ISaveCompanionService
    {
        public string? WriteEof(string projectPath, ChartProject snapshot) => null;

        public string? WriteDlcBuilderProject(
            string projectPath, ChartProject snapshot, double? audioLengthMs) => null;

        public Task<string?> BuildPsarcAsync(string projectPath, ChartProject snapshot,
            double? audioLengthMs, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
    }

    /// <summary>
    ///     Writes a real <c>.rscp</c> (so the atomic swap is genuine) but skips the
    ///     DTRS bundle — this test is about the save OUTCOME, not the exports.
    /// </summary>
    private sealed class RealRscpDocumentService : IProjectDocumentService
    {
        public ChartProject LoadChart(string filePath) => new RscpChartSerializer().Load(filePath);

        public void SaveChart(string filePath, ChartProject project, double? audioLengthMs = null)
            => new RscpChartSerializer().Save(filePath, project);

        public void RefreshArrangementFileManifest(ChartProject project) { }

        public void SaveDlcBuilderXmlBundle(string projectPath, ChartProject project,
            double? audioLengthMs = null)
        { }

        public ChartProject AssembleProject(ChartProject? baseProject, double bpm, string audioFileName,
            IEnumerable<LyricPhrase> lyrics) => baseProject ?? new ChartProject();

        public ChartProject CreateNewProject(string title, string artist, string audioFileName)
            => new() { AudioFileName = audioFileName };
    }

    private sealed class Harness
    {
        public Harness(ChartProject project, string projectPath, ISaveCompanionService companions,
            IProjectDocumentService documentService, IUiDispatcher? uiDispatcher, CheckpointService checkpoints)
        {
            Checkpoints = checkpoints;
            var logger = Logger;
            var transport = new TransportViewModel(new FakeAudioPlaybackService(), State, Config, FakeLocalization.KeysOnly());
            var selection = new SelectionService();
            var history = new HistoryService(logger);
            var editor = new ChartEditorViewModel(
                State, transport, history, selection,
                new TempoService(logger), new TrackContextRegistry(State, selection), FakeLocalization.KeysOnly());

            editor.ApplyProject(project, resetHistory: true);
            State.CurrentProjectPath = projectPath;
            State.CurrentAudioFileName = project.AudioFileName;

            Session = new ProjectSessionService(
                new FakeAudioPlaybackService(), Config, logger, new FakeFileStorageService(),
                documentService, State, transport, editor, new TempoService(logger),
                Recovery, history, companions,
                messageDialog: Messages, statusBar: StatusBar,
                uiDispatcher: uiDispatcher ?? new TestUiDispatcher(),
                localization: FakeLocalization.English(),
                checkpoints: checkpoints);
        }

        public CheckpointService Checkpoints { get; }

        public FakeAppLogger Logger { get; } = new();

        public ProjectState State { get; } = new();
        public FakeConfigurationService Config { get; } = new();
        public RecordingCrashRecoveryService Recovery { get; } = new();
        public RecordingMessageDialog Messages { get; } = new();
        public RecordingStatusBar StatusBar { get; } = new();
        public ProjectSessionService Session { get; }
    }
}
