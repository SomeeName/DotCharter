using CommunityToolkit.Mvvm.Input;
using DotCharter.Core.Domain.Vocals;
using DotCharter.Localization;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Screens.Dialogs;
using DotCharter.Services.Infrastructure.Documents;
using DotCharter.Services.Session;
using DotCharter.Services.State.Checkpoints;

namespace DotCharter.Tests.Presentation.Dialogs;

/// <summary>
///     Edit ▸ Checkpoints. A restore REPLACES the open project and clears its
///     undo tree, so it asks first — the replace guard for a dirty project, and
///     its own question for a clean one that still has history (dirty and "has
///     history" are different questions). A "no" leaves the list where it was.
///     Delete takes two clicks: a checkpoint is the only copy of its snapshot.
/// </summary>
public sealed class CheckpointsViewModelTests
{
    private sealed class Harness
    {
        public Harness(ChartProject? restored = null)
        {
            Edit.State.CurrentProjectPath = @"C:\songs\hysteria.rscp";
            Service.RestoredProject = restored ?? new ChartProject();
            Vm = new CheckpointsViewModel(Service, new FakeDocumentService(), Edit.Services, Edit.Transport, Session,
                Prompt);
        }

        public ChartEditHarness Edit { get; } = new();

        public FakeCheckpointService Service { get; } = new();

        public FakeSession Session { get; } = new();

        public ScriptedPrompt Prompt { get; } = new();

        public CheckpointsViewModel Vm { get; }

        public ICheckpointRow Row(int index) => Vm.Checkpoints[index];

        public Task RestoreAsync(int index = 0) => ((IAsyncRelayCommand)Vm.RestoreCommand).ExecuteAsync(Row(index));

        public void PushAnEdit()
        {
            Edit.History.Do(new LambdaAction());
            Assert.True(Edit.History.CanUndo);
        }
    }

    // ---- restore: the two questions ------------------------------------------

    [Fact]
    public async Task On_a_clean_project_with_history_a_refused_prompt_restores_nothing_and_keeps_the_list()
    {
        var h = new Harness();
        h.PushAnEdit();
        h.Prompt.Answer = false;

        await h.RestoreAsync();

        var (_, _, _, confirmation) = Assert.Single(h.Prompt.Shown);
        Assert.True(confirmation);
        Assert.Empty(h.Service.Restored);
        Assert.True(h.Edit.History.CanUndo);
        Assert.False(h.Vm.Result.IsCompleted, "saying no must leave the user where they were");
        Assert.NotEmpty(h.Vm.Checkpoints);
    }

    [Fact]
    public async Task On_a_clean_project_with_history_an_accepted_prompt_restores()
    {
        var h = new Harness();
        h.PushAnEdit();

        await h.RestoreAsync();

        Assert.Equal(["a"], h.Service.Restored);
        Assert.False(h.Edit.History.CanUndo);
        Assert.True(ChartEditHarness.Answer(h.Vm.Result));
    }

    /// <summary>A restore that discards nothing interrupts nobody — the prompt must not become noise.</summary>
    [Fact]
    public async Task On_a_clean_project_with_no_history_nothing_is_asked()
    {
        var h = new Harness();
        h.Prompt.Answer = false;

        await h.RestoreAsync();

        Assert.Empty(h.Prompt.Shown);
        Assert.Equal(["a"], h.Service.Restored);
    }

    /// <summary>A DIRTY project already asks, through the replace guard — exactly one prompt, not two.</summary>
    [Fact]
    public async Task On_a_dirty_project_the_replace_guard_is_the_only_prompt()
    {
        var h = new Harness();
        h.PushAnEdit();
        h.Session.IsProjectDirty = true;

        await h.RestoreAsync();

        Assert.Equal(1, h.Session.ReplaceGuardCalls);
        Assert.Empty(h.Prompt.Shown);
        Assert.Equal(["a"], h.Service.Restored);
    }

    [Fact]
    public async Task A_refused_replace_guard_restores_nothing_and_keeps_the_dialog_open()
    {
        var h = new Harness();
        h.Session.IsProjectDirty = true;
        h.Session.Allow = false;

        await h.RestoreAsync();

        Assert.Empty(h.Service.Restored);
        Assert.False(h.Vm.Result.IsCompleted);
        Assert.NotEmpty(h.Vm.Checkpoints);
    }

    // ---- restore: what it puts back -------------------------------------------

    /// <summary>The audio reference is part of the checkpoint, and it is what the NEXT save writes.</summary>
    [Theory]
    [InlineData("original.wav")]
    [InlineData("")]
    public async Task A_restore_rolls_the_audio_reference_back_to_the_checkpoints_own(string checkpointAudio)
    {
        var h = new Harness(new ChartProject { AudioFileName = checkpointAudio, Bpm = 90 });
        h.Edit.State.CurrentAudioFileName = "attached-later.ogg";

        await h.RestoreAsync();

        Assert.Equal(checkpointAudio, h.Edit.State.CurrentAudioFileName);
        Assert.Equal(90, h.Edit.Transport.Bpm);
        Assert.Same(h.Service.RestoredProject, h.Edit.State.ActiveProject);
        Assert.Equal(1, h.Session.MarkedDirty);
    }

    // ---- delete: two clicks --------------------------------------------------

    [Fact]
    public void The_first_click_arms_the_row_and_deletes_nothing()
    {
        var h = new Harness();
        var row = h.Row(0);

        h.Vm.DeleteCommand.Execute(row);

        Assert.Empty(h.Service.Deleted);
        Assert.Equal(2, h.Vm.Checkpoints.Count);
        Assert.Equal(LocalizationService.Instance["Checkpoints_DeleteConfirm"], row.DeleteCaption);
    }

    [Fact]
    public void The_second_click_on_the_armed_row_deletes()
    {
        var h = new Harness();
        var row = h.Row(0);

        h.Vm.DeleteCommand.Execute(row);
        h.Vm.DeleteCommand.Execute(row);

        Assert.Equal(["a"], h.Service.Deleted);
        Assert.Equal("verse done", Assert.Single(h.Vm.Checkpoints).Name);
    }

    [Fact]
    public void Arming_another_row_moves_the_latch_without_deleting()
    {
        var h = new Harness();
        var (first, second) = (h.Row(0), h.Row(1));

        h.Vm.DeleteCommand.Execute(first);
        h.Vm.DeleteCommand.Execute(second);

        Assert.Equal(LocalizationService.Instance["Checkpoints_Delete"], first.DeleteCaption);
        Assert.Equal(LocalizationService.Instance["Checkpoints_DeleteConfirm"], second.DeleteCaption);
        Assert.Empty(h.Service.Deleted);
    }

    /// <summary>A fresh open is a fresh latch: a click on any row arms, never deletes.</summary>
    [Fact]
    public void A_new_open_starts_disarmed()
    {
        var h = new Harness();
        h.Vm.DeleteCommand.Execute(h.Row(0));
        h.Vm.CloseCommand.Execute(null);
        Assert.True(ChartEditHarness.Answer(h.Vm.Result));

        var reopened = new CheckpointsViewModel(h.Service, new FakeDocumentService(), h.Edit.Services, h.Edit.Transport,
            h.Session, h.Prompt);
        reopened.DeleteCommand.Execute(reopened.Checkpoints[0]);

        Assert.Empty(h.Service.Deleted);
    }

    /// <summary>The song and the time sit apart on the same "·" every other list uses; the
    /// row said "Hysteria • 22/09/2026 18:40", the only bullet in the app.</summary>
    [Fact]
    public void A_rows_song_and_time_are_separated_like_every_other_list()
    {
        var detail = new Harness().Row(0).Detail;

        Assert.StartsWith("Hysteria · ", detail);
        Assert.DoesNotContain("•", detail);
    }

    // ---- create -----------------------------------------------------------------

    [Fact]
    public async Task Create_snapshots_the_project_under_its_identity_and_clears_the_name()
    {
        var h = new Harness();
        h.Vm.NewCheckpointName = "chorus";

        await ((IAsyncRelayCommand)h.Vm.CreateCheckpointCommand).ExecuteAsync(null);

        var (identity, name, snapshot) = Assert.Single(h.Service.Created);
        Assert.Equal(@"C:\songs\hysteria.rscp", identity);
        Assert.Equal("chorus", name);
        Assert.NotSame(h.Edit.Project, snapshot); // a deep clone, never the live graph
        Assert.Equal("", h.Vm.NewCheckpointName);
        Assert.False(h.Vm.IsBusy);
    }

    [Fact]
    public void Conforms_to_its_contract() => Assert.Empty(ContractConformanceTests.Violations(typeof(ICheckpointsScreen), new Harness().Vm));

    // ---- fakes --------------------------------------------------------------------

    private sealed class LambdaAction : IChartAction
    {
        public bool HasEffect => true;

        public void Execute()
        {
        }

        public void Undo()
        {
        }
    }

    internal sealed class FakeCheckpointService : ICheckpointService
    {
        public List<CheckpointEntry> Entries { get; } =
        [
            new() { Id = "a", Name = "before autofix", SongName = "Hysteria",
                    CreatedUtc = new DateTimeOffset(2026, 9, 22, 18, 40, 0, TimeSpan.Zero) },
            new() { Id = "b", Name = "verse done" }
        ];

        public ChartProject RestoredProject { get; set; } = new();

        public List<string> Restored { get; } = [];

        public List<string> Deleted { get; } = [];

        public List<(string? Identity, string? Name, ChartProject Snapshot)> Created { get; } = [];

        public string RootDirectory => Path.GetTempPath();

        public IReadOnlyList<CheckpointEntry> List(string? projectIdentity) => [.. Entries];

        public CheckpointEntry Create(string? projectIdentity, string? name, ChartProject snapshot,
            string? songName = null, string? appVersion = null)
        {
            Created.Add((projectIdentity, name, snapshot));
            var entry = new CheckpointEntry { Id = "c", Name = name ?? "" };
            Entries.Add(entry);
            return entry;
        }

        public ChartProject Restore(string? projectIdentity, CheckpointEntry entry)
        {
            Restored.Add(entry.Id ?? "");
            return RestoredProject;
        }

        public int CopyTo(string? fromIdentity, string toIdentity) => 0;

        public bool Delete(string? projectIdentity, string? checkpointId)
        {
            Deleted.Add(checkpointId ?? "");
            return Entries.RemoveAll(e => e.Id == checkpointId) > 0;
        }
    }

    internal sealed class FakeDocumentService : IProjectDocumentService
    {
        public ChartProject LoadChart(string filePath) => throw new NotSupportedException();

        public void SaveChart(string filePath, ChartProject project, double? audioLengthMs = null) { }

        public void RefreshArrangementFileManifest(ChartProject project) { }

        public void SaveDlcBuilderXmlBundle(string projectPath, ChartProject project, double? audioLengthMs = null) { }

        public ChartProject AssembleProject(ChartProject? baseProject, double bpm, string audioFileName,
            IEnumerable<LyricPhrase> lyrics) => baseProject ?? new ChartProject();

        public ChartProject CreateNewProject(string title, string artist, string audioFileName) => new();
    }

    /// <summary>The production replace-guard rule, verbatim: a clean project never asks.</summary>
    internal sealed class FakeSession : IProjectSessionService
    {
        public bool Allow { get; set; } = true;

        public int ReplaceGuardCalls { get; private set; }

        public int MarkedDirty { get; private set; }

        public AppConfig AppConfig { get; } = new();

        public bool IsProjectDirty { get; set; }

        public Func<Task<bool>>? ConfirmDiscardAsync { get; set; }

        public event Action? RecentProjectsChanged { add { } remove { } }

        public event Action? ProjectDirtyChanged { add { } remove { } }

        public Task<bool> MayReplaceOpenProjectAsync()
        {
            ReplaceGuardCalls++;
            return Task.FromResult(!IsProjectDirty || Allow);
        }

        public void MarkProjectDirty() => MarkedDirty++;

        public void MarkProjectClean() { }

        public void Initialize() { }

        public void LoadAppConfig() { }

        public void SaveAppConfig() { }

        public void FreeAudio() { }

        public Task LoadFile(string filePath) => Task.CompletedTask;

        public Task LoadAudioFile(string filePath, bool generateBeats = true, bool preferMemoryPlayback = false) =>
            Task.CompletedTask;

        public Task<bool> AttachAudioAsync(string sourcePath) => Task.FromResult(true);

        public Task LoadSilentTransportAsync(double projectLengthMs) => Task.CompletedTask;

        public Task OpenProject() => Task.CompletedTask;

        public Task<bool> SaveProjectAsync() => Task.FromResult(true);

        public Task<bool> SaveProjectAsAsync() => Task.FromResult(true);

        public IReadOnlyList<RecentProjectEntry> GetRecentProjects() => [];

        public void Dispose() { }
    }
}
