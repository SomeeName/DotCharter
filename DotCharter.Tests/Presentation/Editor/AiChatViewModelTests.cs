using System.Globalization;
using System.Resources;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Localization;
using DotCharter.Presentation.Contracts.Editor;
using DotCharter.Presentation.Screens.Editor;
using DotCharter.Tone.Ai;
using DotCharter.Services.Ai.Chart;
using DotCharter.Services.Ai.Chat;
using DotCharter.Services.Editing.Selection;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Presentation.Editor;

/// <summary>
///     The AI chat pane's view model (#559) over the REAL per-chart history (#562) in a temp
///     project folder: the provider's models, the no-provider reason, Send running the instruction
///     through the chart actions (#560) on the chosen scope and storing both turns, a selection
///     scope over nothing refused before any run, Stop, the searchable dropdown, delete that asks
///     first, rename, refusals in words, chart switches. The chart actions are a fake: no model.
/// </summary>
public sealed class AiChatViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "DotCharterTests", "ai-chat-vm-" + Guid.NewGuid().ToString("N"));
    private readonly FakeConfigurationService _config = new();
    private readonly FakeLocalization _loc = FakeLocalization.English();
    private readonly ManualTime _time = new();
    private readonly SelectionService _selection = new();
    private readonly ProjectState _state = new();
    private readonly AiChatHistory _store;
    private readonly FakeChartActions _actions = new();

    public AiChatViewModelTests()
    {
        Directory.CreateDirectory(_root);
        _store = new AiChatHistory(new ProjectStateAiChatChartSource(_state), _time);
        _config.AppConfig.Ai = new AiProviderSettings(AiProviderKind.ClaudeCli, "claude-sonnet-5-5", null);
    }

    public void Dispose() => BestEffortDelete.Directory(_root);

    private static readonly ResourceManager Translations =
        new("DotCharter.Localization.Resources", typeof(LocalizationService).Assembly);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void WithoutAProvider_ThePickerIsEmpty_SaysWhy_AndSendIsOff()
    {
        _config.AppConfig.Ai = AiProviderSettings.Disabled;
        var vm = Build(OpenProject("a"));
        vm.InputText = "Why 5th position?";

        Assert.False(vm.HasProvider);
        Assert.Empty(vm.Models);
        Assert.Equal(_loc["AiChat_NoProvider"], vm.NoProviderText);
        Assert.DoesNotContain("AiChat_", vm.NoProviderText, StringComparison.Ordinal);
        Assert.False(vm.SendCommand.CanExecute(null));
    }

    [Fact]
    public void TheModels_AreTheProvidersPresets_PlusATypedOne_WithTheConfiguredOneSelected()
    {
        var claude = Build(OpenProject("a"));
        Assert.Equal(["claude-haiku-5-5", "claude-sonnet-5-5", "claude-opus-5-5"], claude.Models.Select(m => m.Detail));
        Assert.Equal("Claude Sonnet 5.5", claude.SelectedModel?.Label);
        Assert.Equal(_loc["Ai_Provider_ClaudeCli"], claude.ProviderText);

        _config.AppConfig.Ai = new AiProviderSettings(AiProviderKind.OpenAiCompatible, "qwen3-coder", "http://localhost:1234");
        claude.RefreshProvider();
        Assert.Equal("qwen3-coder", Assert.Single(claude.Models).Label);
        Assert.Equal("qwen3-coder", claude.SelectedModel?.Label);
    }

    /// <summary>#561: the task is the pane's explicit choice, never guessed from the words ("verse"
    /// and "section" turn up in fingering instructions too). Fingering only by default (#602).</summary>
    [Fact]
    public async Task Send_RunsTheTaskThePickerNames_SectionsAndPhrasesOnlyWhenPicked()
    {
        var vm = Build(OpenProject("a"));
        Assert.Equal(5, vm.Jobs.Count);
        Assert.Same(vm.Jobs[0], vm.SelectedJob);
        Assert.Equal([_loc["AiChat_Job_FingeringOnly"], _loc["AiChat_Job_Fingering"], _loc["AiChat_Job_Structure"],
            _loc["AiChat_Job_Explain"], _loc["AiChat_Job_Tones"]], vm.Jobs.Select(j => j.Label));

        vm.InputText = "Use 1-3 on the power chords";
        await ((IAsyncRelayCommand)vm.SendCommand).ExecuteAsync(null);
        vm.SelectedJob = vm.Jobs[1];
        vm.InputText = "Keep the hand in 5th position in this section";
        await ((IAsyncRelayCommand)vm.SendCommand).ExecuteAsync(null);
        vm.SelectedJob = vm.Jobs[2];
        vm.InputText = "Mark the sections";
        await ((IAsyncRelayCommand)vm.SendCommand).ExecuteAsync(null);
        // #563: Ask / explain, a question answered in text.
        vm.SelectedJob = vm.Jobs[3];
        vm.InputText = "Why 1-3 here?";
        await ((IAsyncRelayCommand)vm.SendCommand).ExecuteAsync(null);

        Assert.Equal([AiChartJob.FingeringOnly, AiChartJob.FhpAndFingering, AiChartJob.SectionsAndPhrases, AiChartJob.Explain],
            _actions.Requests.Select(r => r.Job));
    }

    /// <summary>
    ///     #614: the Tones task reads the same three scopes as TONES — the selected tone changes, the one in
    ///     force at the playhead, every placed one — says which in the scope line, sends them on the request,
    ///     and refuses a scope that covers none BEFORE any run.
    /// </summary>
    [Fact]
    public async Task Send_OnTheTonesTask_SendsTheScopesToneChanges_AndRefusesOneThatCoversNone()
    {
        var project = OpenProject("a");
        var beats = new List<BeatMarker>();
        for (var i = 0; i <= 16; i++) beats.Add(new BeatMarker { Time = i * 500, IsMeasure = i % 4 == 0, BeatsInMeasure = 4 });
        foreach (var beat in beats) project.Beats.Add(beat);
        var arrangement = project.Arrangements[0];
        var clean = new ToneChange { Position = TimelineMath.PositionFromMs(1000, beats), ToneName = "Clean" };
        var lead = new ToneChange { Position = TimelineMath.PositionFromMs(4000, beats), ToneName = "Lead" };
        arrangement.ToneChanges.Add(lead);
        arrangement.ToneChanges.Add(clean);
        var vm = Build(project);
        vm.SelectedJob = vm.Jobs.Single(j => j is AiChatOptionItem { Key: AiChartJob.Tones });

        // Whole arrangement: every placed change, in time order.
        Assert.Equal(_loc.Format("AiChat_Summary_Tones", "Clean, Lead"), vm.ScopeSummary);
        vm.InputText = "the lead tone is a high-gain Mesa, darker";
        await ((IAsyncRelayCommand)vm.SendCommand).ExecuteAsync(null);
        var all = Assert.Single(_actions.Requests);
        Assert.Equal(AiChartJob.Tones, all.Job);
        Assert.Equal([clean, lead], all.Scope.ToneChanges!);

        // Selection: nothing selected is refused with the scope line, before any run.
        vm.SelectedScope = vm.Scopes[0];
        Assert.Equal(_loc["AiChat_Summary_NoToneSelected"], vm.ScopeSummary);
        vm.InputText = "darker";
        await ((IAsyncRelayCommand)vm.SendCommand).ExecuteAsync(null);
        Assert.Single(_actions.Requests);
        Assert.Equal(_loc["AiChat_Summary_NoToneSelected"], vm.ErrorText);
        _selection.SelectedTones.Add(lead);
        Assert.Equal(_loc.Format("AiChat_Summary_Tones", "Lead"), vm.ScopeSummary);

        // The playhead: the change in force there.
        vm.SelectedScope = vm.Scopes[1];
        _state.CurrentTimeMs = 2500;
        Assert.Equal(_loc.Format("AiChat_Summary_Section", "Clean"), vm.ScopeSummary);
        vm.InputText = "cleaner";
        await ((IAsyncRelayCommand)vm.SendCommand).ExecuteAsync(null);
        Assert.Equal([clean], _actions.Requests[^1].Scope.ToneChanges!);
        _state.CurrentTimeMs = 500;
        Assert.Equal(_loc["AiChat_Summary_NoToneAtPlayhead"], vm.ScopeSummary);
    }

    /// <summary>
    ///     #602: what measured best is the default - fingering only on guitar AND bass, the anchors
    ///     staying the algorithm's - and the line under the pickers says why, with the numbers,
    ///     differently on bass (where the algorithm beat every model).
    /// </summary>
    [Fact]
    public void TheFingeringTask_DefaultsToFingeringOnly_WithTheMeasuredReason_PerArrangementType()
    {
        var (project, bass) = OpenGuitarAndBass();
        var vm = Build(project);

        Assert.Equal(AiChartJob.FingeringOnly, JobOf(vm.SelectedJob));
        Assert.Equal(_loc["AiChat_JobNote_Guitar"], vm.JobNote);
        Assert.True(vm.HasJobNote);

        vm.SelectedArrangement = vm.Arrangements.Single(a => a.Label == bass.Name);
        Assert.Equal(AiChartJob.FingeringOnly, JobOf(vm.SelectedJob));
        Assert.Equal(_loc["AiChat_JobNote_Bass"], vm.JobNote);
        // Nothing was PICKED, so nothing is remembered.
        Assert.Null(_config.AppConfig.AiFingeringJobGuitar);
        Assert.Null(_config.AppConfig.AiFingeringJobBass);
    }

    /// <summary>#602: the charter's explicit pick wins, is remembered for that arrangement type only,
    /// and is what the next pane starts on; the opt-in's own trade-off is printed.</summary>
    [Fact]
    public void AnExplicitPick_Wins_IsRememberedPerArrangementType_AndSurvivesANewPane()
    {
        var (project, bass) = OpenGuitarAndBass();
        var vm = Build(project);

        vm.SelectedJob = vm.Jobs.Single(j => JobOf(j) == AiChartJob.FhpAndFingering);
        Assert.Equal(AiChartJob.FhpAndFingering, _config.AppConfig.AiFingeringJobGuitar);
        Assert.Null(_config.AppConfig.AiFingeringJobBass);
        Assert.Equal(_loc["AiChat_JobNote_GuitarFhp"], vm.JobNote);

        // Bass keeps its own default: the guitar pick does not leak across.
        vm.SelectedArrangement = vm.Arrangements.Single(a => a.Label == bass.Name);
        Assert.Equal(AiChartJob.FingeringOnly, JobOf(vm.SelectedJob));
        vm.SelectedJob = vm.Jobs.Single(j => JobOf(j) == AiChartJob.FhpAndFingering);
        Assert.Equal(_loc["AiChat_JobNote_BassFhp"], vm.JobNote);
        Assert.Equal(AiChartJob.FhpAndFingering, _config.AppConfig.AiFingeringJobBass);

        // Back on guitar, and in a brand-new pane: the remembered pick, not the default.
        vm.SelectedArrangement = vm.Arrangements.Single(a => a.Label == project.Arrangements[0].Name);
        Assert.Equal(AiChartJob.FhpAndFingering, JobOf(vm.SelectedJob));
        Assert.Equal(AiChartJob.FhpAndFingering, JobOf(Build(project).SelectedJob));
    }

    /// <summary>A switch AWAY from fingering is the charter's too: changing arrangement keeps it.</summary>
    [Fact]
    public void ANonFingeringTask_IsKept_AcrossArrangements_AndHasNoNote()
    {
        var (project, bass) = OpenGuitarAndBass();
        var vm = Build(project);

        vm.SelectedJob = vm.Jobs.Single(j => JobOf(j) == AiChartJob.SectionsAndPhrases);
        vm.SelectedArrangement = vm.Arrangements.Single(a => a.Label == bass.Name);

        Assert.Equal(AiChartJob.SectionsAndPhrases, JobOf(vm.SelectedJob));
        Assert.Equal("", vm.JobNote);
        Assert.False(vm.HasJobNote);
        Assert.Null(_config.AppConfig.AiFingeringJobBass);
    }

    /// <summary>#603: the reasoning level beside the model - Settings > AI's, or Default for a model
    /// that cannot take one (the request would not carry it either).</summary>
    [Fact]
    public void TheEffortLabel_ShowsTheSettingsLevel_OrDefaultWhereTheModelCannotTakeOne()
    {
        _config.AppConfig.Ai = new AiProviderSettings(AiProviderKind.Claude, "claude-opus-5-5", null, Effort: AiReasoningEffort.High);
        var vm = Build(OpenProject("a"));
        Assert.Equal(_loc.Format("AiChat_Effort", _loc["Ai_Effort_High"]), vm.EffortText);

        _config.AppConfig.Ai = new AiProviderSettings(AiProviderKind.Claude, "claude-haiku-4-5", null, Effort: AiReasoningEffort.High);
        vm.RefreshProvider();
        vm.SelectedModel = vm.Models.Single(m => m.Detail == "claude-haiku-4-5");
        Assert.Equal(_loc.Format("AiChat_Effort", _loc["Ai_Effort_Default"]), vm.EffortText);

        _config.AppConfig.Ai = AiProviderSettings.Disabled;
        vm.RefreshProvider();
        Assert.Equal("", vm.EffortText);
    }

    [Fact]
    public async Task Send_RunsTheInstruction_OnTheChosenScope_AndStoresBothTurns_WithoutTouchingTheChart()
    {
        var project = OpenProject("a");
        var notesBefore = project.Arrangements[0].Levels[0].Notes.Count;
        var vm = Build(project);
        vm.SelectedScope = vm.Scopes[2];
        vm.InputText = "Use 1-3 for the two-fret octaves";

        await ((IAsyncRelayCommand)vm.SendCommand).ExecuteAsync(null);

        var request = Assert.Single(_actions.Requests);
        Assert.Equal("Use 1-3 for the two-fret octaves", request.Instruction);
        Assert.Equal(AiChartJob.FingeringOnly, request.Job);
        Assert.Equal(AiChatScopeKind.Arrangement, request.Scope.Kind);
        Assert.Same(project.Arrangements[0], request.Scope.Arrangement);
        Assert.Equal("claude-sonnet-5-5", request.ModelId);
        Assert.Equal("", vm.ErrorText);
        Assert.False(vm.IsBusy);
        Assert.Collection(vm.Turns,
            t => Assert.True(t.IsUser),
            t =>
            {
                Assert.False(t.IsUser);
                Assert.Equal(FakeChartActions.ReplyText, t.Text);
                Assert.Equal("Claude Sonnet 5.5", t.RoleText);
                // Numbers follow the Windows region (CurrentCulture), not the UI language.
                Assert.Equal(_loc.Format("AiChat_Tokens", 1200.ToString("N0", CultureInfo.CurrentCulture), "340"), t.CostText);
            });

        var summary = Assert.Single((await _store.ListAsync(Ct)).Conversations);
        var stored = (await _store.LoadAsync(summary.Id, Ct)).Conversation!;
        Assert.Equal([AiChatRole.User, AiChatRole.Assistant], stored.Turns.Select(t => t.Role));
        Assert.Equal(new AiChatUsage(1200, 340), stored.Turns[1].Usage);
        Assert.Equal(notesBefore, project.Arrangements[0].Levels[0].Notes.Count);
    }

    [Fact]
    public async Task ASelectionScopeOverNothing_IsRefusedInWords_BeforeAnyRun()
    {
        var vm = Build(OpenProject("a"));
        vm.SelectedScope = vm.Scopes[0];
        vm.InputText = "Finger these chords";

        await ((IAsyncRelayCommand)vm.SendCommand).ExecuteAsync(null);

        Assert.Empty(_actions.Requests);
        Assert.Equal(_loc["AiChat_Summary_NothingSelected"], vm.ErrorText);
        Assert.Equal("Finger these chords", vm.InputText);
    }

    [Fact]
    public async Task Stop_CancelsTheRun_AndSaysNothingChanged()
    {
        _actions.Hold = true;
        var vm = Build(OpenProject("a"));
        vm.SelectedScope = vm.Scopes[2];
        vm.InputText = "Keep the hand in 5th position";

        var sending = ((IAsyncRelayCommand)vm.SendCommand).ExecuteAsync(null);
        Assert.True(vm.IsBusy);
        vm.CancelCommand.Execute(null);
        await sending;

        Assert.False(vm.IsBusy);
        Assert.Equal(_loc["AiChat_Stopped"], vm.ErrorText);
    }

    [Fact]
    public async Task AChartWithNoProjectFolder_RefusesTheSave_InWords()
    {
        var vm = Build(OpenProject("a", saved: false));
        vm.InputText = "Mark the sections";

        await ((IAsyncRelayCommand)vm.SendCommand).ExecuteAsync(null);

        Assert.True(vm.HasError);
        Assert.Contains(_loc["AiChat_Status_ChartNotSaved"], vm.ErrorText, StringComparison.Ordinal);
        Assert.DoesNotContain("AiChat_", vm.ErrorText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheDropdown_ListsThisChartsChats_AndTheSearchNarrowsThem()
    {
        var vm = Build(OpenProject("a"));
        await Send(vm, "Finger the chorus barre chords");
        vm.NewChatCommand.Execute(null);
        await Send(vm, "What is wrong in bar 34?");
        vm.NewChatCommand.Execute(null);
        await Send(vm, "Mark the sections");

        await Open(vm);
        Assert.Equal(["Mark the sections", "What is wrong in bar 34?", "Finger the chorus barre chords"],
            vm.History.Entries.Select(e => e.Title));
        Assert.Equal(_loc.Format("AiChat_Caption", "Song a"), vm.History.Caption);

        vm.History.SearchText = "BAR";
        Assert.Equal(["What is wrong in bar 34?", "Finger the chorus barre chords"], vm.History.Entries.Select(e => e.Title));

        vm.History.SearchText = "solo";
        Assert.False(vm.History.HasEntries);
        Assert.Equal(_loc.Format("AiChat_HistoryNoMatch", "solo"), vm.History.EmptyText);
    }

    [Fact]
    public async Task Delete_AsksFirst_AndOnlyTheConfirmationDeletes()
    {
        var vm = Build(OpenProject("a"));
        await Send(vm, "Mark the sections");
        await Open(vm);
        var entry = Assert.Single(vm.History.Entries);

        entry.DeleteCommand.Execute(null);
        Assert.True(entry.IsConfirmingDelete);
        Assert.Contains("Mark the sections", entry.DeletePromptText, StringComparison.Ordinal);
        Assert.Single((await _store.ListAsync(Ct)).Conversations);

        entry.CancelDeleteCommand.Execute(null);
        Assert.False(entry.IsConfirmingDelete);
        Assert.Single((await _store.ListAsync(Ct)).Conversations);

        entry.DeleteCommand.Execute(null);
        await ((IAsyncRelayCommand)entry.ConfirmDeleteCommand).ExecuteAsync(null);
        Assert.Empty((await _store.ListAsync(Ct)).Conversations);
        Assert.Empty(vm.History.Entries);
        Assert.False(vm.HasTurns);
        Assert.Equal(_loc["AiChat_NewChat"], vm.Title);
    }

    /// <summary>
    ///     #632's root cause: the dropdown shows what <c>Entries</c> PUBLISHES. A delete removed the row
    ///     from the list <c>Entries</c> already was and re-assigned that same list, so no change was
    ///     raised and the deleted row stayed on screen; its next delete and every open found no file.
    /// </summary>
    [Fact]
    public async Task Delete_PublishesAListWithoutTheRow_SoTheDropdownDropsIt()
    {
        var vm = Build(OpenProject("a"));
        await Send(vm, "Mark the sections");
        vm.NewChatCommand.Execute(null);
        await Send(vm, "Finger the chorus");
        await Open(vm);
        var shown = Shown(vm);
        var entry = vm.History.Entries.Single(e => e.Title == "Mark the sections");

        entry.DeleteCommand.Execute(null);
        await ((IAsyncRelayCommand)entry.ConfirmDeleteCommand).ExecuteAsync(null);

        Assert.Equal(["Finger the chorus"], shown[^1]);
    }

    /// <summary>#632: a chat whose file is already gone deletes like any other — the row goes, the pane
    /// forgets it, and no refusal is shown for what the user asked for anyway.</summary>
    [Fact]
    public async Task Delete_OfAChatWhoseFileIsGone_DropsTheRow_ForgetsIt_AndShowsNoError()
    {
        var project = OpenProject("a");
        var vm = Build(project);
        await Send(vm, "Mark the sections");
        await Open(vm);
        var shown = Shown(vm);
        var entry = Assert.Single(vm.History.Entries);
        Assert.True(entry.IsCurrent);
        File.Delete(ChatFile(project, ((AiChatHistoryEntryViewModel)entry).Id));

        entry.DeleteCommand.Execute(null);
        await ((IAsyncRelayCommand)entry.ConfirmDeleteCommand).ExecuteAsync(null);

        Assert.Empty(shown[^1]);
        Assert.False(vm.History.HasNotice);
        Assert.False(vm.HasTurns);
        Assert.Equal(_loc["AiChat_NewChat"], vm.Title);
    }

    /// <summary>#632: opening a row whose file is gone drops the row, lists the folder again (here it
    /// picks up a chat saved since the dropdown opened) and says the chat is no longer there.</summary>
    [Fact]
    public async Task Open_OfAChatWhoseFileIsGone_DropsTheRow_RelistsTheFolder_AndSaysSo()
    {
        var project = OpenProject("a");
        var vm = Build(project);
        await Send(vm, "Mark the sections");
        vm.NewChatCommand.Execute(null);
        await Send(vm, "Finger the chorus");
        vm.NewChatCommand.Execute(null);
        await Open(vm);
        var shown = Shown(vm);
        var gone = vm.History.Entries.Single(e => e.Title == "Mark the sections");
        File.Delete(ChatFile(project, ((AiChatHistoryEntryViewModel)gone).Id));
        var later = _store.StartNew("claude-sonnet-5-5")!
            .Append(new AiChatTurn(AiChatRole.User, "Name the solo", _time.GetUtcNow().AddMinutes(5)));
        Assert.Equal(AiChatStatus.Ok, (await _store.SaveAsync(later, Ct)).Status);

        await ((IAsyncRelayCommand)gone.OpenCommand).ExecuteAsync(null);
        await Loaded(vm);

        Assert.Equal(["Name the solo", "Finger the chorus"], shown[^1]);
        Assert.Equal(_loc["AiChat_Status_NotFound"], vm.History.NoticeText);
        Assert.False(vm.HasTurns);
    }

    [Fact]
    public async Task Rename_ReachesTheStore_AndTheOpenChatsTitle_SoTheNextSaveKeepsIt()
    {
        var vm = Build(OpenProject("a"));
        await Send(vm, "Mark the sections");
        await Open(vm);
        var entry = Assert.Single(vm.History.Entries);

        entry.BeginRenameCommand.Execute(null);
        entry.TitleDraft = "Sections pass";
        await ((IAsyncRelayCommand)entry.CommitRenameCommand).ExecuteAsync(null);
        await Loaded(vm);

        Assert.Equal("Sections pass", vm.Title);
        await Send(vm, "And the solo?");
        Assert.Equal("Sections pass", Assert.Single((await _store.ListAsync(Ct)).Conversations).Title);
    }

    [Fact]
    public async Task OpeningAChat_ShowsItsTurns_AndClosesTheDropdown()
    {
        var vm = Build(OpenProject("a"));
        await Send(vm, "Mark the sections");
        vm.NewChatCommand.Execute(null);
        Assert.False(vm.HasTurns);

        await Open(vm);
        await ((IAsyncRelayCommand)vm.History.Entries[0].OpenCommand).ExecuteAsync(null);

        Assert.False(vm.History.IsOpen);
        Assert.Equal(["Mark the sections", FakeChartActions.ReplyText], vm.Turns.Select(t => t.Text));
        Assert.True(vm.History.Entries[0].IsCurrent);
    }

    [Fact]
    public async Task AnotherChartsChat_InThisChartsFolder_IsNotListed_AndTheNoticeSaysWhy()
    {
        var project = OpenProject("a");
        var vm = Build(project);
        await Send(vm, "Mark the sections");

        // A conversation of ANOTHER chart, copied into this chart's folder.
        var folder = Path.Combine(_root, "a", "ai-chats", project.ProjectId);
        var mine = Directory.GetFiles(folder, "*.json").Single();
        var foreignId = ProjectIdentity.NewId();
        var json = File.ReadAllText(mine)
            .Replace(project.ProjectId, ProjectIdentity.NewId(), StringComparison.Ordinal)
            .Replace(Path.GetFileNameWithoutExtension(mine), foreignId, StringComparison.Ordinal);
        File.WriteAllText(Path.Combine(folder, foreignId + ".json"), json);

        await Open(vm);

        Assert.Single(vm.History.Entries);
        Assert.True(vm.History.HasNotice);
        Assert.Contains(_loc["AiChat_Status_ChartMismatch"], vm.History.NoticeText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SwitchingCharts_EmptiesThePane_AndTheDropdownListsOnlyTheNewChartsChats()
    {
        var vm = Build(OpenProject("a"));
        await Send(vm, "Mark the sections");
        Assert.True(vm.HasTurns);

        var other = OpenProject("b");
        Assert.False(vm.HasTurns);
        Assert.Equal(["Lead"], vm.Arrangements.Select(a => a.Label));

        await Open(vm);
        Assert.Empty(vm.History.Entries);
        Assert.Equal(_loc["AiChat_HistoryEmpty"], vm.History.EmptyText);
        Assert.Equal(other.ProjectId, _store.OpenChartId);
    }

    [Fact]
    public void TheScopeSummary_SaysWhatTheScopeCoversRightNow()
    {
        var project = OpenProject("a");
        var vm = Build(project);

        vm.SelectedScope = vm.Scopes[0];
        Assert.Equal(_loc["AiChat_Summary_NothingSelected"], vm.ScopeSummary);
        _selection.SelectedNotes.Add(project.Arrangements[0].Levels[0].Notes[0]);
        Assert.Equal(_loc.Format("AiChat_Summary_Selection", 1, 0), vm.ScopeSummary);

        vm.SelectedScope = vm.Scopes[1];
        Assert.Equal(_loc["AiChat_Summary_NoSection"], vm.ScopeSummary);
        _state.CurrentSectionDisplay = "Verse 2";
        Assert.Equal(_loc.Format("AiChat_Summary_Section", "Verse 2"), vm.ScopeSummary);

        vm.SelectedScope = vm.Scopes[2];
        Assert.Equal(_loc.Format("AiChat_Summary_Arrangement", "Lead", 2, 0), vm.ScopeSummary);
    }

    /// <summary>
    ///     #640: the empty pane and the message box's hint follow the PICKED task — what it does and
    ///     what to type — for each of the five, both written in every shipped language; without a chart
    ///     the pane says to open one.
    /// </summary>
    [Fact]
    public void EachTask_HasItsOwnEmptyLine_AndHint()
    {
        var keys = FakeLocalization.KeysOnly();
        var vm = Build(OpenProject("a"), keys);
        var names = new Dictionary<AiChartJob, string>
        {
            [AiChartJob.FingeringOnly] = "FingeringOnly",
            [AiChartJob.FhpAndFingering] = "Fingering",
            [AiChartJob.SectionsAndPhrases] = "Structure",
            [AiChartJob.Explain] = "Explain",
            [AiChartJob.Tones] = "Tones"
        };
        Assert.Equal(names.Keys.Order(), vm.Jobs.Select(j => JobOf(j)!.Value).Order());
        Assert.Equal("AiChat_Empty_FingeringOnly", vm.EmptyText);
        Assert.Equal("AiChat_Hint_FingeringOnly", vm.InputHint);

        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        // Backwards, so every pick is a change and the last one returns to the default.
        foreach (var job in vm.Jobs.Reverse())
        {
            vm.SelectedJob = job;
            var name = names[JobOf(job)!.Value];
            Assert.Equal("AiChat_Empty_" + name, vm.EmptyText);
            Assert.Equal("AiChat_Hint_" + name, vm.InputHint);
            foreach (var key in new[] { vm.EmptyText, vm.InputHint })
            {
                Assert.NotEqual(key, _loc[key]);
                foreach (var culture in new[] { "de-DE", "ja-JP" })
                    Assert.NotEqual(_loc[key], Translations.GetString(key, CultureInfo.GetCultureInfo(culture)));
            }
        }

        Assert.Equal(names.Count, changed.Count(p => p == nameof(AiChatViewModel.InputHint)));
        _state.ActiveProject = null;
        Assert.Equal("AiChat_EmptyNoChart", vm.EmptyText);
    }

    /// <summary>#640: only a reply that came back with NO answer — the run failed, or another was in
    /// flight — offers Retry; an answer, held proposals or a refusal in words do not.</summary>
    [Theory]
    [InlineData(AiChartRunStatus.Failed, true)]
    [InlineData(AiChartRunStatus.Busy, true)]
    [InlineData(AiChartRunStatus.Answered, false)]
    [InlineData(AiChartRunStatus.Held, false)]
    [InlineData(AiChartRunStatus.NothingToDo, false)]
    public async Task OnlyAReplyWithNoAnswer_OffersRetry(AiChartRunStatus status, bool offers)
    {
        var vm = Build(OpenProject("a"));
        _actions.Replies.Enqueue(new AiChatInstructionReply(status, "reply", null, null));
        await Send(vm, "Why 1-3 in bar 19?");

        Assert.Equal(offers, vm.Turns[^1].CanRetry);
        Assert.Equal(offers, vm.Turns[^1].RetryCommand is not null);
        Assert.False(vm.Turns[0].CanRetry);
    }

    /// <summary>
    ///     #640: Retry on an unanswered Explain sends the SAME request again — same words, task, scope
    ///     and model — in the unanswered reply's place, one at a time; the answer takes its place in the
    ///     thread and in the stored chat. A message sent after a failed reply retires its Retry.
    /// </summary>
    [Fact]
    public async Task Retry_SendsTheSameRequestAgain_InTheUnansweredReplysPlace_OneAtATime()
    {
        var vm = Build(OpenProject("a"));
        vm.SelectedJob = vm.Jobs.Single(j => JobOf(j) == AiChartJob.Explain);
        _actions.Replies.Enqueue(new AiChatInstructionReply(AiChartRunStatus.Failed, _loc["AiChat_ExplainNoAnswer"], null, null));
        await Send(vm, "Why is the hand in 5th position in bar 19?");
        var request = Assert.Single(_actions.Requests);
        var retry = Assert.IsAssignableFrom<IAsyncRelayCommand>(vm.Turns[^1].RetryCommand);
        // Picking another task afterwards changes nothing about what Retry sends.
        vm.SelectedJob = vm.Jobs[0];

        var gate = _actions.Gate = new TaskCompletionSource();
        var retrying = retry.ExecuteAsync(null);
        Assert.True(vm.IsBusy);
        Assert.False(retry.CanExecute(null));
        var working = Assert.Single(vm.Turns);
        Assert.True(working.IsUser);
        gate.SetResult();
        await retrying;

        Assert.Equal(2, _actions.Requests.Count);
        Assert.Same(request, _actions.Requests[1]);
        Assert.Equal(AiChartJob.Explain, _actions.Requests[1].Job);
        Assert.False(vm.IsBusy);
        Assert.Collection(vm.Turns,
            t => Assert.True(t.IsUser),
            t =>
            {
                Assert.Equal(FakeChartActions.ReplyText, t.Text);
                Assert.False(t.CanRetry);
            });
        var summary = Assert.Single((await _store.ListAsync(Ct)).Conversations);
        var stored = (await _store.LoadAsync(summary.Id, Ct)).Conversation!;
        Assert.Equal([AiChatRole.User, AiChatRole.Assistant], stored.Turns.Select(t => t.Role));
        Assert.Equal(FakeChartActions.ReplyText, stored.Turns[1].Text);

        // A failed reply, then another message: only the LAST turn can be sent again.
        _actions.Gate = null;
        _actions.Replies.Enqueue(new AiChatInstructionReply(AiChartRunStatus.Failed, "no answer", null, null));
        await Send(vm, "And bar 27?");
        Assert.True(vm.Turns[^1].CanRetry);
        vm.InputText = "And bar 31?";
        var sending = ((IAsyncRelayCommand)vm.SendCommand).ExecuteAsync(null);
        Assert.DoesNotContain(vm.Turns, t => t.CanRetry);
        await sending;
        Assert.DoesNotContain(vm.Turns, t => t.CanRetry);
    }

    /// <summary>#803: the Issues tab's Fix / Explain with AI arrive as the charter's message, on the
    /// request the tab built (its job, scope and targets), with the pane's picked model.</summary>
    [Fact]
    public async Task TheFindingsDoor_SendsTheRequestAsTheChartersMessage_AndShowsThePane()
    {
        var vm = Build(OpenProject("a"));
        var shown = 0;
        vm.ShowRequested += () => shown++;
        var targets = new[] { new AiChartFindingTarget("I20", 5000) };
        var request = new AiChatInstructionRequest("Fix these chart findings:\n• I20 · M3.3", AiChatScope.WholeArrangement(),
            Job: AiChartJob.FhpOnly);
        request = request with { Targets = targets };

        Assert.True(vm.CanAsk);
        Assert.True(await vm.AskAsync(request));

        Assert.Equal(1, shown);
        var sent = Assert.Single(_actions.Requests);
        Assert.Equal(AiChartJob.FhpOnly, sent.Job);
        Assert.Same(targets, sent.Targets);
        Assert.Equal("claude-sonnet-5-5", sent.ModelId);
        Assert.NotNull(sent.Conversation);
        Assert.Equal(request.Instruction, vm.Turns[0].Text);
        Assert.True(vm.Turns[0].IsUser);
    }

    /// <summary>#803: a reply that lists findings a job can fix offers "Ask the AI to fix these"; pressing it
    /// sends the follow-up and retires the offer.</summary>
    [Fact]
    public async Task AReplyWithFindingsLeft_OffersTheFix_AndTheFixSendsTheFollowUp()
    {
        var vm = Build(OpenProject("a"));
        var followUp = new AiChatInstructionRequest("Fix these chart findings:\n• L05", AiChatScope.WholeArrangement(),
            Job: AiChartJob.FhpOnly);
        followUp = followUp with { Targets = [new AiChartFindingTarget("L05", 11000, 3)] };
        _actions.Replies.Enqueue(new AiChatInstructionReply(AiChartRunStatus.NoChange, "1 finding is still left", null, null, followUp));
        vm.InputText = "Fix the hand positions";
        await ((IAsyncRelayCommand)vm.SendCommand).ExecuteAsync(null);

        var reply = vm.Turns[^1];
        Assert.True(reply.CanFix);
        Assert.DoesNotContain(vm.Turns.Take(vm.Turns.Count - 1), t => t.CanFix);
        await ((IAsyncRelayCommand)reply.FixCommand!).ExecuteAsync(null);

        Assert.Equal(2, _actions.Requests.Count);
        Assert.Same(followUp.Targets, _actions.Requests[1].Targets);
        Assert.Equal(followUp.Instruction, vm.Turns[^2].Text);
        Assert.DoesNotContain(vm.Turns, t => t.CanFix);
    }

    /// <summary>#803: after an Accept of a review THIS chat proposed, what is left in its range becomes the
    /// assistant's turn with the fix on offer; another review's Accept adds nothing.</summary>
    [Fact]
    public async Task AnAcceptOfThisChatsReview_ReportsWhatIsLeft_WithTheFixOnOffer()
    {
        var vm = Build(OpenProject("a"));
        var count = await LinkLastTurnToReview(vm, "review-1");

        _actions.RaiseApplied(new AiChatApplied("another-review", "left: L05", null));
        Assert.Equal(count, vm.Turns.Count);

        var followUp = new AiChatInstructionRequest("Fix: L05", AiChatScope.WholeArrangement(), Job: AiChartJob.FhpOnly)
        {
            Targets = [new AiChartFindingTarget("L05", 11000, 3)]
        };
        _actions.RaiseApplied(new AiChatApplied("review-1", "1 finding is still left in this range:\n• L05", followUp));

        Assert.Equal(count + 1, vm.Turns.Count);
        Assert.False(vm.Turns[^1].IsUser);
        Assert.StartsWith("1 finding is still left", vm.Turns[^1].Text, StringComparison.Ordinal);
        Assert.True(vm.Turns[^1].CanFix);
    }

    /// <summary>#808: a menu verb's Accept that leaves findings is reported in the chat with the fix on offer,
    /// even before any message was sent; the fix sends its follow-up.</summary>
    [Fact]
    public async Task AMenuPassesAccept_IsReportedInTheChat_WithTheFixOnOffer()
    {
        var vm = Build(OpenProject("a"));
        Assert.Empty(vm.Turns);
        var followUp = new AiChatInstructionRequest("Fix: L05", AiChatScope.WholeArrangement(), Job: AiChartJob.FhpOnly)
        {
            Targets = [new AiChartFindingTarget("L05", 11000, 3)]
        };

        _actions.RaiseApplied(new AiChatApplied("menu-review", "After the menu pass:\n• L05", followUp) { FromMenu = true });

        var turn = Assert.Single(vm.Turns);
        Assert.False(turn.IsUser);
        Assert.StartsWith("After the menu pass", turn.Text, StringComparison.Ordinal);
        Assert.True(turn.CanFix);
        await ((IAsyncRelayCommand)turn.FixCommand!).ExecuteAsync(null);
        Assert.Same(followUp.Targets, Assert.Single(_actions.Requests).Targets);
    }

    /// <summary>Sends one more message whose reply carries <paramref name="reviewId" />, as a held reply does.</summary>
    private async Task<int> LinkLastTurnToReview(AiChatViewModel vm, string reviewId)
    {
        _actions.Replies.Enqueue(new AiChatInstructionReply(AiChartRunStatus.Held, "Proposed", ReviewWithId(reviewId), null));
        vm.InputText = "Again";
        await ((IAsyncRelayCommand)vm.SendCommand).ExecuteAsync(null);
        return vm.Turns.Count;
    }

    /// <summary>A held review with a chosen id, through the run the app uses (a fake model never answers it).</summary>
    private static AiChartReview ReviewWithId(string reviewId)
    {
        var review = (AiChartReview)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(AiChartReview));
        typeof(AiChartReview).GetField("<Id>k__BackingField",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(review, reviewId);
        return review;
    }

    private AiChatViewModel Build(ChartProject project, ILocalizationService? localization = null)
    {
        _ = project;
        var vm = new AiChatViewModel(_config, _state, _selection, new AiChatServices(_store, _actions, _time),
            new TestUiDispatcher(), localization ?? _loc);
        vm.SelectedScope = vm.Scopes[2];
        return vm;
    }

    private static AiChartJob? JobOf(IAiChatOption? option) => option is AiChatOptionItem { Key: AiChartJob job } ? job : null;

    private (ChartProject Project, Arrangement Bass) OpenGuitarAndBass()
    {
        var project = OpenProject("gb");
        var bass = new Arrangement { Name = "Bass", ArrangementType = ArrangementType.Bass, StringCount = 4 };
        bass.Levels.Add(new ChartLevel());
        project.Arrangements.Add(bass);
        // The pane lists the arrangements when the chart opens: open it again with both.
        _state.ActiveProject = null;
        _state.ActiveProject = project;
        _state.ActiveArrangement = project.Arrangements[0];
        return (project, bass);
    }

    private ChartProject OpenProject(string name, bool saved = true)
    {
        var project = new ChartProject();
        project.Chart.SongName = "Song " + name;
        var arrangement = new Arrangement { Name = "Lead" };
        var level = new ChartLevel();
        level.Notes.Add(new GuitarNote { StringIndex = 1, Fret = 5 });
        level.Notes.Add(new GuitarNote { StringIndex = 1, Fret = 7 });
        arrangement.Levels.Add(level);
        project.Arrangements.Add(arrangement);
        var folder = Path.Combine(_root, name);
        Directory.CreateDirectory(folder);
        _state.CurrentProjectPath = saved ? Path.Combine(folder, "project.rscp") : "";
        _state.ActiveProject = project;
        _state.ActiveArrangement = arrangement;
        return project;
    }

    private async Task Send(AiChatViewModel vm, string text)
    {
        _time.Advance(TimeSpan.FromMinutes(1));
        vm.InputText = text;
        await ((IAsyncRelayCommand)vm.SendCommand).ExecuteAsync(null);
        Assert.Equal("", vm.ErrorText);
    }

    private static async Task Open(AiChatViewModel vm)
    {
        vm.History.IsOpen = true;
        await Loaded(vm);
    }

    private static Task Loaded(AiChatViewModel vm) => ((AiChatHistoryViewModel)vm.History).Loading;

    /// <summary>Every list the dropdown is handed from now on, copied when it is handed over: what the
    /// view shows, rather than whatever the view model's list holds later.</summary>
    private static List<string[]> Shown(AiChatViewModel vm)
    {
        var shown = new List<string[]> { vm.History.Entries.Select(e => e.Title).ToArray() };
        vm.History.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AiChatHistoryViewModel.Entries)) shown.Add([.. vm.History.Entries.Select(x => x.Title)]);
        };
        return shown;
    }

    private string ChatFile(ChartProject project, string conversationId) =>
        Path.Combine(_root, "a", "ai-chats", project.ProjectId, conversationId + ".json");

    /// <summary>The chart pass as the pane sees it: records each request and answers with a held reply,
    /// or with the next of <see cref="Replies" />; <see cref="Gate" /> holds the answer until it is set.</summary>
    private sealed class FakeChartActions : IAiChatChartActions
    {
        public const string ReplyText = "Proposed 1 section: Verse, 0:12, 2 hand positions. Accept it in the review card.";

        public List<AiChatInstructionRequest> Requests { get; } = [];

        public Queue<AiChatInstructionReply> Replies { get; } = new();

        public bool Hold { get; set; }

        public TaskCompletionSource? Gate { get; set; }

        public bool IsRunning => false;

        public event EventHandler<AiChatApplied>? Applied;

        public void RaiseApplied(AiChatApplied applied) => Applied?.Invoke(this, applied);

        public async Task<AiChatInstructionReply> RunInstructionAsync(AiChatInstructionRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            if (Hold) await Task.Delay(Timeout.Infinite, ct);
            if (Gate is { } gate) await gate.Task.WaitAsync(ct);
            return Replies.TryDequeue(out var reply)
                ? reply
                : new AiChatInstructionReply(AiChartRunStatus.Held, ReplyText, null, new AiChatUsage(1200, 340));
        }
    }

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
