using System.Text;
using System.Text.Json.Nodes;
using DotCharter.Services.Ai.Chat;
using DotCharter.Services.Parsing.Rscp;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Ai.Chat;

/// <summary>
///     The per-chart AI chat history (#562). Every case runs in its own temp folder;
///     the project folders here stand in for real ones, and the round-trip cases go
///     through a real <c>.rscp</c> save and reopen.
/// </summary>
public sealed class AiChatHistoryTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "DotCharterTests", "ai-chat-" + Guid.NewGuid().ToString("N"));

    public AiChatHistoryTests() => Directory.CreateDirectory(_root);

    public void Dispose() => BestEffortDelete.Directory(_root);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- round trip -------------------------------------------------------

    [Fact]
    public async Task ASavedConversation_ListsAndLoadsWithEveryField()
    {
        var chart = new FakeChart(ProjectIdentity.NewId(), Folder("song"));
        var history = new AiChatHistory(chart);

        var conversation = Conversation(chart.ChartId, "Use 1·3 octaves in the verse\nplease")
            .Append(new AiChatTurn(AiChatRole.Assistant, "Proposed 12 changes.", T0.AddMinutes(2),
                new AiChatUsage(1200, 340, 0.0123m), "review-7"));

        Assert.Equal(AiChatStatus.Ok, (await history.SaveAsync(conversation, Ct)).Status);

        var listing = await history.ListAsync(Ct);
        Assert.Equal(AiChatStatus.Ok, listing.Status);
        Assert.Equal(chart.ChartId, listing.ChartId);
        Assert.Empty(listing.Refused);
        var summary = Assert.Single(listing.Conversations);
        Assert.Equal(conversation.Id, summary.Id);
        Assert.Equal("Use 1·3 octaves in the verse please", summary.Title);
        Assert.Equal("claude-test", summary.ModelId);
        Assert.Equal(T0, summary.CreatedUtc);
        Assert.Equal(T0.AddMinutes(2), summary.UpdatedUtc);
        Assert.Equal(2, summary.TurnCount);

        var loaded = await history.LoadAsync(conversation.Id, Ct);
        Assert.Equal(AiChatStatus.Ok, loaded.Status);
        var turns = loaded.Conversation!.Turns;
        Assert.Equal(2, turns.Count);
        Assert.Equal(conversation.Turns[0], turns[0]);
        Assert.Equal(conversation.Turns[1], turns[1]);
        Assert.Equal(new AiChatUsage(1200, 340, 0.0123m), turns[1].Usage);
        Assert.Equal("review-7", turns[1].ReviewId);
    }

    [Fact]
    public async Task ChatsSurviveSavingAndReopeningTheProject()
    {
        var path = SaveProject("first", "Song A");
        var state = Open(path);
        var history = new AiChatHistory(new ProjectStateAiChatChartSource(state));

        var conversation = history.StartNew("claude-test")!.Append(UserTurn("Mark the sections"));
        Assert.Equal(AiChatStatus.Ok, (await history.SaveAsync(conversation, Ct)).Status);

        // Reopen: a fresh state over the same .rscp.
        var reopened = new AiChatHistory(new ProjectStateAiChatChartSource(Open(path)));
        var summary = Assert.Single((await reopened.ListAsync(Ct)).Conversations);
        Assert.Equal(conversation.Id, summary.Id);
        Assert.Equal(AiChatStatus.Ok, (await reopened.LoadAsync(conversation.Id, Ct)).Status);
    }

    [Fact]
    public async Task ALegacyProjectsChats_SurviveClosingItUnsaved_AndItsFirstSave()
    {
        var path = SaveProject("legacy", "Old Song");
        var text = File.ReadAllText(path, Encoding.UTF8);
        var start = text.IndexOf("  <dcProjectId>", StringComparison.Ordinal);
        File.WriteAllText(path, text.Remove(start, text.IndexOf('\n', start) - start + 1), new UTF8Encoding(false));

        var history = new AiChatHistory(new ProjectStateAiChatChartSource(Open(path)));
        var conversation = history.StartNew("m")!.Append(UserTurn("hello"));
        Assert.Equal(AiChatStatus.Ok, (await history.SaveAsync(conversation, Ct)).Status);

        // Closed without saving, reopened: the same derived id finds the chat.
        var reopenedState = Open(path);
        var reopened = new AiChatHistory(new ProjectStateAiChatChartSource(reopenedState));
        Assert.Single((await reopened.ListAsync(Ct)).Conversations);

        // The first save writes that id; the chat is still there after it.
        new RscpChartSerializer().Save(path, reopenedState.ActiveProject!);
        var afterSave = new AiChatHistory(new ProjectStateAiChatChartSource(Open(path)));
        Assert.Single((await afterSave.ListAsync(Ct)).Conversations);
    }

    [Fact]
    public async Task CopyingTheProjectFolder_CopiesItsChats()
    {
        var path = SaveProject("original", "Song");
        var history = new AiChatHistory(new ProjectStateAiChatChartSource(Open(path)));
        var conversation = history.StartNew("m")!.Append(UserTurn("keep the hand in 5th position"));
        await history.SaveAsync(conversation, Ct);

        var copy = Folder("copy");
        CopyDirectory(Path.GetDirectoryName(path)!, copy);
        var copied = new AiChatHistory(new ProjectStateAiChatChartSource(Open(Path.Combine(copy, "project.rscp"))));

        Assert.Equal(conversation.Id, Assert.Single((await copied.ListAsync(Ct)).Conversations).Id);
    }

    // ---- isolation ----------------------------------------------------------

    [Fact]
    public async Task TwoProjects_EachSeeOnlyTheirOwnChats_EvenInOneFolder()
    {
        var shared = Folder("shared");
        var a = new FakeChart(ProjectIdentity.NewId(), shared);
        var b = new FakeChart(ProjectIdentity.NewId(), shared);
        var c = new FakeChart(ProjectIdentity.NewId(), Folder("elsewhere"));
        var historyA = new AiChatHistory(a);
        var historyB = new AiChatHistory(b);
        var historyC = new AiChatHistory(c);

        var chatA = Conversation(a.ChartId, "A's question");
        var chatB = Conversation(b.ChartId, "B's question");
        await historyA.SaveAsync(chatA, Ct);
        await historyB.SaveAsync(chatB, Ct);

        Assert.Equal(chatA.Id, Assert.Single((await historyA.ListAsync(Ct)).Conversations).Id);
        Assert.Equal(chatB.Id, Assert.Single((await historyB.ListAsync(Ct)).Conversations).Id);
        Assert.Empty((await historyC.ListAsync(Ct)).Conversations);
        Assert.Equal(AiChatStatus.NotFound, (await historyB.LoadAsync(chatA.Id, Ct)).Status);
    }

    [Fact]
    public async Task SwitchingTheOpenChart_SwitchesTheList()
    {
        var folder = Folder("switch");
        var chart = new FakeChart(ProjectIdentity.NewId(), folder);
        var history = new AiChatHistory(chart);
        var first = Conversation(chart.ChartId, "first chart");
        await history.SaveAsync(first, Ct);

        var firstId = chart.ChartId;
        chart.ChartId = ProjectIdentity.NewId();

        var listing = await history.ListAsync(Ct);
        Assert.Equal(chart.ChartId, listing.ChartId);
        Assert.Empty(listing.Conversations);

        chart.ChartId = firstId;
        Assert.Single((await history.ListAsync(Ct)).Conversations);
    }

    [Fact]
    public async Task AConversationCopiedIntoAnotherChartsFolder_IsRefusedEverywhere()
    {
        var a = new FakeChart(ProjectIdentity.NewId(), Folder("a"));
        var b = new FakeChart(ProjectIdentity.NewId(), Folder("b"));
        var chatA = Conversation(a.ChartId, "A's private chat");
        await new AiChatHistory(a).SaveAsync(chatA, Ct);

        var stolen = Path.Combine(ChatFolder(b), chatA.Id + ".json");
        Directory.CreateDirectory(ChatFolder(b));
        File.Copy(Path.Combine(ChatFolder(a), chatA.Id + ".json"), stolen);
        var historyB = new AiChatHistory(b);

        var listing = await historyB.ListAsync(Ct);
        Assert.Empty(listing.Conversations);
        Assert.Equal(new AiChatRefusal(chatA.Id, AiChatStatus.ChartMismatch), Assert.Single(listing.Refused));

        var load = await historyB.LoadAsync(chatA.Id, Ct);
        Assert.Equal(AiChatStatus.ChartMismatch, load.Status);
        Assert.Null(load.Conversation);
        Assert.Contains(a.ChartId, load.Detail, StringComparison.Ordinal);

        var before = File.ReadAllBytes(stolen);
        Assert.Equal(AiChatStatus.ChartMismatch, (await historyB.DeleteAsync(chatA.Id, Ct)).Status);
        Assert.Equal(AiChatStatus.ChartMismatch, (await historyB.RenameAsync(chatA.Id, "mine now", Ct)).Status);
        Assert.Equal(before, File.ReadAllBytes(stolen));
    }

    [Fact]
    public async Task ATamperedChartId_IsRefused()
    {
        var chart = new FakeChart(ProjectIdentity.NewId(), Folder("tamper"));
        var history = new AiChatHistory(chart);
        var chat = Conversation(chart.ChartId, "question");
        await history.SaveAsync(chat, Ct);

        RewriteField(chart, chat.Id, "chartId", ProjectIdentity.NewId());

        Assert.Equal(AiChatStatus.ChartMismatch, (await history.LoadAsync(chat.Id, Ct)).Status);
    }

    [Fact]
    public async Task SavingAnotherChartsConversation_IsRefused_AndWritesNothing()
    {
        var chart = new FakeChart(ProjectIdentity.NewId(), Folder("open"));
        var history = new AiChatHistory(chart);
        var foreign = Conversation(ProjectIdentity.NewId(), "from the chart that was open before");

        var result = await history.SaveAsync(foreign, Ct);

        Assert.Equal(AiChatStatus.ChartMismatch, result.Status);
        Assert.False(Directory.Exists(Path.Combine(chart.ProjectFolder!, "ai-chats")));
    }

    [Theory]
    [InlineData(@"..\..\escape")]
    [InlineData("../x")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF")]
    [InlineData("")]
    public async Task AnIdThatIsNotIdShaped_IsRefused(string id)
    {
        var history = new AiChatHistory(new FakeChart(ProjectIdentity.NewId(), Folder("ids")));
        Assert.Equal(AiChatStatus.InvalidId, (await history.LoadAsync(id, Ct)).Status);
        Assert.Equal(AiChatStatus.InvalidId, (await history.DeleteAsync(id, Ct)).Status);

        var badChart = new AiChatHistory(new FakeChart(id, Folder("ids")));
        Assert.Equal(AiChatStatus.InvalidId, (await badChart.ListAsync(Ct)).Status);
    }

    [Fact]
    public async Task NoChart_AndAChartWithoutAFolder_AreRefusals()
    {
        var none = new AiChatHistory(new FakeChart(ProjectIdentity.NewId(), Folder("none")) { IsOpen = false });
        Assert.Null(none.OpenChartId);
        Assert.Null(none.StartNew("m"));
        Assert.Equal(AiChatStatus.NoChart, (await none.ListAsync(Ct)).Status);

        var unsaved = new AiChatHistory(new FakeChart(ProjectIdentity.NewId(), null));
        Assert.Equal(AiChatStatus.ChartNotSaved, (await unsaved.ListAsync(Ct)).Status);
        Assert.Equal(AiChatStatus.ChartNotSaved,
            (await unsaved.SaveAsync(unsaved.StartNew("m")!.Append(UserTurn("hi")), Ct)).Status);

        var state = new ProjectState { ActiveProject = new ChartProject(), CurrentProjectPath = "" };
        Assert.Null(new ProjectStateAiChatChartSource(state).Current!.ProjectFolder);
    }

    // ---- rename / delete ----------------------------------------------------

    [Fact]
    public async Task Rename_Persists_AndABlankTitleGoesBackToTheFirstMessage()
    {
        var chart = new FakeChart(ProjectIdentity.NewId(), Folder("rename"));
        var history = new AiChatHistory(chart);
        var chat = Conversation(chart.ChartId, "Fix the solo fingering");
        await history.SaveAsync(chat, Ct);

        Assert.Equal(AiChatStatus.Ok, (await history.RenameAsync(chat.Id, "  Solo\nwork  ", Ct)).Status);
        var renamed = Assert.Single((await history.ListAsync(Ct)).Conversations);
        Assert.Equal("Solo work", renamed.Title);
        Assert.Equal(chat.UpdatedUtc, renamed.UpdatedUtc);

        // A renamed chat keeps its name when it grows.
        var loaded = (await history.LoadAsync(chat.Id, Ct)).Conversation!;
        Assert.Equal("Solo work", loaded.Append(UserTurn("and the outro")).Title);

        await history.RenameAsync(chat.Id, "   ", Ct);
        Assert.Equal("Fix the solo fingering", Assert.Single((await history.ListAsync(Ct)).Conversations).Title);
    }

    [Fact]
    public async Task Delete_RemovesTheConversation()
    {
        var chart = new FakeChart(ProjectIdentity.NewId(), Folder("delete"));
        var history = new AiChatHistory(chart);
        var keep = Conversation(chart.ChartId, "keep");
        var drop = Conversation(chart.ChartId, "drop");
        await history.SaveAsync(keep, Ct);
        await history.SaveAsync(drop, Ct);

        Assert.Equal(AiChatStatus.Ok, (await history.DeleteAsync(drop.Id, Ct)).Status);

        Assert.Equal(keep.Id, Assert.Single((await history.ListAsync(Ct)).Conversations).Id);
        Assert.Equal(AiChatStatus.NotFound, (await history.LoadAsync(drop.Id, Ct)).Status);
        Assert.Equal(AiChatStatus.NotFound, (await history.DeleteAsync(drop.Id, Ct)).Status);
    }

    [Fact]
    public async Task TheList_IsNewestFirst()
    {
        var chart = new FakeChart(ProjectIdentity.NewId(), Folder("order"));
        var history = new AiChatHistory(chart);
        var older = Conversation(chart.ChartId, "older", T0);
        var newer = Conversation(chart.ChartId, "newer", T0.AddHours(1));
        await history.SaveAsync(older, Ct);
        await history.SaveAsync(newer, Ct);

        var ids = (await history.ListAsync(Ct)).Conversations.Select(c => c.Id).ToArray();
        Assert.Equal([newer.Id, older.Id], ids);
    }

    // ---- damaged files and bounds -------------------------------------------

    [Fact]
    public async Task ACorruptFile_IsRefused_AndASaveOverItKeepsItsBytesAside()
    {
        var chart = new FakeChart(ProjectIdentity.NewId(), Folder("corrupt"));
        var history = new AiChatHistory(chart);
        var chat = Conversation(chart.ChartId, "q");
        await history.SaveAsync(chat, Ct);
        var file = Path.Combine(ChatFolder(chart), chat.Id + ".json");
        File.WriteAllText(file, "{ not json");

        Assert.Equal(AiChatStatus.Corrupt, (await history.LoadAsync(chat.Id, Ct)).Status);
        Assert.Equal(AiChatStatus.Corrupt, Assert.Single((await history.ListAsync(Ct)).Refused).Reason);

        Assert.Equal(AiChatStatus.Ok, (await history.SaveAsync(chat, Ct)).Status);
        Assert.Equal(AiChatStatus.Ok, (await history.LoadAsync(chat.Id, Ct)).Status);
        var aside = Assert.Single(Directory.GetFiles(ChatFolder(chart), "*.corrupt-*.json"));
        Assert.Equal("{ not json", File.ReadAllText(aside));
        Assert.Empty((await history.ListAsync(Ct)).Refused);
    }

    [Theory]
    [InlineData("id", "ffffffffffffffffffffffffffffffff", AiChatStatus.Corrupt)]
    [InlineData("format", "2", AiChatStatus.UnsupportedFormat)]
    [InlineData("created", "yesterday", AiChatStatus.Corrupt)]
    public async Task AFileThatIsNotThisBuildsConversation_IsRefused(string field, string value, AiChatStatus expected)
    {
        var chart = new FakeChart(ProjectIdentity.NewId(), Folder("fields"));
        var history = new AiChatHistory(chart);
        var chat = Conversation(chart.ChartId, "q");
        await history.SaveAsync(chat, Ct);

        RewriteField(chart, chat.Id, field, value);

        Assert.Equal(expected, (await history.LoadAsync(chat.Id, Ct)).Status);
        if (expected == AiChatStatus.UnsupportedFormat)
            Assert.Equal(AiChatStatus.UnsupportedFormat, (await history.SaveAsync(chat, Ct)).Status);
    }

    [Fact]
    public async Task AnUnknownRole_MakesTheFileCorrupt()
    {
        var chart = new FakeChart(ProjectIdentity.NewId(), Folder("role"));
        var history = new AiChatHistory(chart);
        var chat = Conversation(chart.ChartId, "q");
        await history.SaveAsync(chat, Ct);

        var file = Path.Combine(ChatFolder(chart), chat.Id + ".json");
        File.WriteAllText(file, File.ReadAllText(file).Replace("\"user\"", "\"system\"", StringComparison.Ordinal));

        Assert.Equal(AiChatStatus.Corrupt, (await history.LoadAsync(chat.Id, Ct)).Status);
    }

    [Fact]
    public async Task AnUnreadableFile_IsNeverWrittenOver()
    {
        var chart = new FakeChart(ProjectIdentity.NewId(), Folder("locked"));
        var history = new AiChatHistory(chart);
        var chat = Conversation(chart.ChartId, "q");
        await history.SaveAsync(chat, Ct);
        var file = Path.Combine(ChatFolder(chart), chat.Id + ".json");
        var before = File.ReadAllBytes(file);

        using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Equal(AiChatStatus.Unreadable, (await history.LoadAsync(chat.Id, Ct)).Status);
            Assert.Equal(AiChatStatus.Unreadable,
                (await history.SaveAsync(chat.Append(UserTurn("more")), Ct)).Status);
        }

        Assert.Equal(before, File.ReadAllBytes(file));
    }

    [Fact]
    public async Task AConversationOverTheSizeBound_IsNotWritten_AndAnOversizeFileIsNotRead()
    {
        var chart = new FakeChart(ProjectIdentity.NewId(), Folder("size"));
        var history = new AiChatHistory(chart);
        var huge = Conversation(chart.ChartId, new string('x', AiChatLimits.MaxConversationBytes));

        Assert.Equal(AiChatStatus.TooLarge, (await history.SaveAsync(huge, Ct)).Status);
        Assert.False(File.Exists(Path.Combine(ChatFolder(chart), huge.Id + ".json")));

        var small = Conversation(chart.ChartId, "q");
        await history.SaveAsync(small, Ct);
        var file = Path.Combine(ChatFolder(chart), small.Id + ".json");
        using (var stream = new FileStream(file, FileMode.Append))
            stream.SetLength(AiChatLimits.MaxConversationBytes + 1L);

        Assert.Equal(AiChatStatus.TooLarge, (await history.LoadAsync(small.Id, Ct)).Status);
    }

    [Fact]
    public async Task ANewConversationPastTheCountBound_IsRefused_ButAnExistingOneStillSaves()
    {
        var chart = new FakeChart(ProjectIdentity.NewId(), Folder("count"));
        var history = new AiChatHistory(chart);
        var first = Conversation(chart.ChartId, "first");
        await history.SaveAsync(first, Ct);

        // Fill the folder directly: N atomic, fsynced saves would only be slower.
        for (var i = 1; i < AiChatLimits.MaxConversationsPerChart; i++)
        {
            var filler = Conversation(chart.ChartId, "filler " + i);
            File.WriteAllBytes(Path.Combine(ChatFolder(chart), filler.Id + ".json"), AiChatJson.Serialize(filler));
        }

        Assert.Equal(AiChatStatus.LimitReached,
            (await history.SaveAsync(Conversation(chart.ChartId, "one too many"), Ct)).Status);
        Assert.Equal(AiChatStatus.Ok, (await history.SaveAsync(first.Append(UserTurn("still fine")), Ct)).Status);
    }

    // ---- titles ---------------------------------------------------------------

    [Theory]
    [InlineData("  Use 1·3\r\n\toctaves  ", "Use 1·3 octaves")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    public void TheTitle_ComesFromTheFirstMessage(string message, string expected) =>
        Assert.Equal(expected, AiChatTitle.FromFirstMessage(message));

    [Fact]
    public void ALongTitle_IsCutAtAWord_AndNeverSplitsASurrogatePair()
    {
        var words = string.Join(' ', Enumerable.Repeat("fingering", 20));
        var cut = AiChatTitle.FromFirstMessage(words);
        Assert.True(cut.Length <= AiChatTitle.MaxLength, cut);
        Assert.EndsWith("fingering…", cut, StringComparison.Ordinal);

        var emoji = string.Concat(Enumerable.Repeat("\U0001F3B8", 60));
        var hard = AiChatTitle.FromFirstMessage(emoji);
        Assert.True(hard.Length <= AiChatTitle.MaxLength, hard);
        Assert.False(char.IsHighSurrogate(hard[^2]), "the cut split a surrogate pair");
    }

    [Fact]
    public void OnlyAUserTurn_TitlesAConversation()
    {
        var chat = AiChatConversation.Start(ProjectIdentity.NewId(), "m", T0)
            .Append(new AiChatTurn(AiChatRole.Assistant, "Hello, how can I help?", T0));
        Assert.Equal("", chat.Title);
        Assert.Equal("Mark sections", chat.Append(UserTurn("Mark sections")).Title);
    }

    // ---- helpers ----------------------------------------------------------------

    private string Folder(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static string ChatFolder(FakeChart chart) => Path.Combine(chart.ProjectFolder!, "ai-chats", chart.ChartId);

    private static AiChatTurn UserTurn(string text) => new(AiChatRole.User, text, T0.AddMinutes(1));

    private static AiChatConversation Conversation(string chartId, string firstMessage, DateTimeOffset? at = null)
    {
        var when = at ?? T0;
        return AiChatConversation.Start(chartId, "claude-test", when)
            .Append(new AiChatTurn(AiChatRole.User, firstMessage, when));
    }

    private static void RewriteField(FakeChart chart, string conversationId, string field, string value)
    {
        var file = Path.Combine(ChatFolder(chart), conversationId + ".json");
        var json = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        json[field] = field == "format" ? JsonValue.Create(int.Parse(value, System.Globalization.CultureInfo.InvariantCulture)) : value;
        File.WriteAllText(file, json.ToJsonString());
    }

    private string SaveProject(string folderName, string title)
    {
        var project = new ChartProject { AudioFileName = "song.wav" };
        project.Chart.SongName = title;
        project.Beats.Add(new BeatMarker { Time = 0, IsMeasure = true });
        var path = Path.Combine(Folder(folderName), "project.rscp");
        new RscpChartSerializer().Save(path, project);
        return path;
    }

    private static ProjectState Open(string rscpPath) =>
        new() { ActiveProject = new RscpChartSerializer().Load(rscpPath), CurrentProjectPath = rscpPath };

    private static void CopyDirectory(string from, string to)
    {
        foreach (var directory in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(directory.Replace(from, to, StringComparison.Ordinal));
        foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
            File.Copy(file, file.Replace(from, to, StringComparison.Ordinal));
    }

    private sealed class FakeChart(string chartId, string? projectFolder) : IAiChatChartSource
    {
        public string ChartId { get; set; } = chartId;

        public string? ProjectFolder { get; } = projectFolder;

        public bool IsOpen { get; init; } = true;

        public AiChatChart? Current => IsOpen ? new AiChatChart(ChartId, ProjectFolder) : null;
    }
}
