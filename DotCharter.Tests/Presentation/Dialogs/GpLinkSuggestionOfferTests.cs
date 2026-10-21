using System.IO.Compression;
using System.Text;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Screens.Dialogs;
using DotCharter.Services.Editing.Maintenance;
using DotCharter.Services.Import.GuitarPro;
using DotCharter.Services.Parsing.GuitarPro;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Presentation.Dialogs;

/// <summary>
///     A real Guitar Pro import, then the link offer: a two-track score whose first bar sits in a
///     repeat. The repeat links across both tracks (the reliable tier), the bar after it matches
///     across the tracks too (the content tier); Link all is ONE undo step, Review opens the
///     manager with the suggestions, Skip links nothing, and only an add-new import is offered.
/// </summary>
public sealed class GpLinkSuggestionOfferTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("dotcharter-gplinks-").FullName;

    public void Dispose() => BestEffortDelete.Directory(_tempDir);

    private sealed class Harness
    {
        public Harness(Func<object, bool> answer)
        {
            var (maintenance, _) = UndoStepProxy.Create<IChartMaintenanceService>();
            Applier = new GpImportApplier(Edit.State, Edit.Editor, Edit.Transport, new FakeConfigurationService(), maintenance,
                new FakeAppLogger(), FakeLocalization.English());
            Dialogs = new ScriptedDialogs(
                type => type == typeof(ILinkSuggestionsScreen)
                    ? new LinkSuggestionsViewModel(Edit.Services)
                    : new LinkGroupsViewModel(Edit.Services),
                screen => answer(screen));
            Offer = new GpLinkSuggestionOffer(Dialogs, Applier, Edit.Services);
        }

        public ChartEditHarness Edit { get; } = new();

        public GpImportApplier Applier { get; }

        public ScriptedDialogs Dialogs { get; }

        public GpLinkSuggestionOffer Offer { get; }

        public async Task ImportAsync(string path, bool techniqueOnly = false)
        {
            var vm = new GpImportViewModel(Applier, new FakeFileStorageService(), new FakeConfigurationService(), Edit.State,
                new FakeAppLogger(), FakeLocalization.English());
            vm.Configure(new GpImportRequest(path, new GpImporter().GetAvailableTracks(path)));
            if (techniqueOnly) vm.IsTechniqueOnlyMode = true;
            await ((IAsyncRelayCommand)vm.ImportCommand).ExecuteAsync(null);
            Assert.True(ChartEditHarness.Answer(vm.Result));
        }
    }

    private static bool Choose(object screen, LinkSuggestionsChoice choice)
    {
        switch (screen)
        {
            case LinkSuggestionsViewModel summary:
                (choice switch
                {
                    LinkSuggestionsChoice.LinkAll => summary.LinkAllCommand,
                    LinkSuggestionsChoice.Review => summary.ReviewCommand,
                    _ => summary.SkipCommand
                }).Execute(null);
                return true;
            case LinkGroupsViewModel manager:
                manager.CloseCommand.Execute(null);
                return true;
            default:
                return false;
        }
    }

    [Fact]
    public async Task Link_all_links_the_scores_repeat_and_the_shared_bar_in_one_undo_step()
    {
        var h = new Harness(screen => Choose(screen, LinkSuggestionsChoice.LinkAll));
        await h.ImportAsync(WriteScore());
        var project = h.Edit.Project;
        Assert.Equal(["Lead", "Rhythm"], project.Arrangements.Select(a => a.Name));
        var steps = h.Edit.History.GetUndoSteps().Count;

        var linked = await h.Offer.OfferAsync();

        Assert.Equal(2, linked);
        var summary = Assert.IsType<LinkSuggestionsViewModel>(Assert.Single(h.Dialogs.Opened));
        Assert.Equal("2 groups to link, 6 placements.", summary.Headline);
        Assert.Equal(steps + 1, h.Edit.History.GetUndoSteps().Count);
        Assert.Equal(["A", "B"], project.LinkGroups.Select(g => g.Letter));
        Assert.Equal(4, project.LinkGroups[0].Placements.Count); // the repeat: bars 1 and 2 of both tracks
        Assert.Equal(2, project.LinkGroups[1].Placements.Count); // bar 3 of both tracks

        h.Edit.History.Undo();
        Assert.Empty(project.LinkGroups);
    }

    [Fact]
    public async Task Review_opens_the_manager_with_every_suggestion_and_links_nothing()
    {
        var h = new Harness(screen => Choose(screen, LinkSuggestionsChoice.Review));
        await h.ImportAsync(WriteScore());

        Assert.Equal(0, await h.Offer.OfferAsync());

        var manager = Assert.IsType<LinkGroupsViewModel>(h.Dialogs.Opened[1]);
        Assert.Equal(2, manager.Suggestions.Count);
        Assert.Contains("from the score's repeats", manager.Suggestions[0].Detail, StringComparison.Ordinal);
        Assert.Empty(h.Edit.Project.LinkGroups);
    }

    [Fact]
    public async Task Skip_links_nothing_and_the_offer_is_made_once()
    {
        var h = new Harness(screen => Choose(screen, LinkSuggestionsChoice.Skip));
        await h.ImportAsync(WriteScore());

        Assert.Equal(0, await h.Offer.OfferAsync());
        Assert.Equal(0, await h.Offer.OfferAsync());

        Assert.Single(h.Dialogs.Opened);
        Assert.Empty(h.Edit.Project.LinkGroups);
    }

    [Fact]
    public async Task A_technique_only_import_offers_nothing()
    {
        var h = new Harness(screen => Choose(screen, LinkSuggestionsChoice.LinkAll));
        await h.ImportAsync(WriteScore());
        h.Applier.TakeLastImport();

        await h.ImportAsync(WriteScore(), techniqueOnly: true);

        Assert.Equal(0, await h.Offer.OfferAsync());
        Assert.Empty(h.Dialogs.Opened);
    }

    /// <summary>
    ///     Two tracks, three master bars; the first is a repeat played twice, so the score plays
    ///     bars 1, 1, 2. Both tracks carry the same notes.
    /// </summary>
    private string WriteScore()
    {
        const string gpif = """
            <GPIF>
              <Score><Title>Link Fixture</Title></Score>
              <MasterTrack>
                <Tracks>0 1</Tracks>
                <Automations>
                  <Automation><Type>Tempo</Type><Bar>0</Bar><Position>0</Position><Value>120 2</Value></Automation>
                </Automations>
              </MasterTrack>
              <Tracks>
                <Track id="0">
                  <Name>Lead</Name>
                  <Staves><Staff><Properties>
                    <Property name="Tuning"><Pitches>40 45 50 55 59 64</Pitches></Property>
                  </Properties></Staff></Staves>
                </Track>
                <Track id="1">
                  <Name>Rhythm</Name>
                  <Staves><Staff><Properties>
                    <Property name="Tuning"><Pitches>40 45 50 55 59 64</Pitches></Property>
                  </Properties></Staff></Staves>
                </Track>
              </Tracks>
              <MasterBars>
                <MasterBar><Time>4/4</Time><Repeat start="true" end="true" count="2" /><Bars>0 2</Bars></MasterBar>
                <MasterBar><Time>4/4</Time><Bars>1 3</Bars></MasterBar>
              </MasterBars>
              <Bars>
                <Bar id="0"><Voices>0 -1 -1 -1</Voices></Bar>
                <Bar id="1"><Voices>1 -1 -1 -1</Voices></Bar>
                <Bar id="2"><Voices>0 -1 -1 -1</Voices></Bar>
                <Bar id="3"><Voices>1 -1 -1 -1</Voices></Bar>
              </Bars>
              <Voices>
                <Voice id="0"><Beats>0 1</Beats></Voice>
                <Voice id="1"><Beats>2 3</Beats></Voice>
              </Voices>
              <Beats>
                <Beat id="0"><Rhythm ref="0" /><Notes>0</Notes></Beat>
                <Beat id="1"><Rhythm ref="0" /><Notes>1</Notes></Beat>
                <Beat id="2"><Rhythm ref="0" /><Notes>2</Notes></Beat>
                <Beat id="3"><Rhythm ref="0" /><Notes>3</Notes></Beat>
              </Beats>
              <Notes>
                <Note id="0"><Properties><Property name="String"><String>2</String></Property><Property name="Fret"><Fret>5</Fret></Property></Properties></Note>
                <Note id="1"><Properties><Property name="String"><String>2</String></Property><Property name="Fret"><Fret>7</Fret></Property></Properties></Note>
                <Note id="2"><Properties><Property name="String"><String>3</String></Property><Property name="Fret"><Fret>3</Fret></Property></Properties></Note>
                <Note id="3"><Properties><Property name="String"><String>1</String></Property><Property name="Fret"><Fret>2</Fret></Property></Properties></Note>
              </Notes>
              <Rhythms><Rhythm id="0"><NoteValue>Half</NoteValue></Rhythm></Rhythms>
            </GPIF>
            """;

        var path = Path.Combine(_tempDir, $"links-{Guid.NewGuid():N}.gp");
        using var fs = File.Create(path);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        using var writer = new StreamWriter(zip.CreateEntry("Content/score.gpif").Open(), Encoding.UTF8);
        writer.Write(gpif);
        return path;
    }
}
