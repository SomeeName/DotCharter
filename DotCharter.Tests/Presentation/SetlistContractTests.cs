using System.Text.RegularExpressions;
using System.Xml.Linq;
using DotCharter.Presentation.Contracts.PlayMode;
using DotCharter.Presentation.Fakes;
using DotCharter.Presentation.Fakes.PlayMode;
using DotCharter.UI.ViewModels.PlayMode;

namespace DotCharter.Tests.Presentation;

/// <summary>
///     Play Mode setlists on the Presentation contracts (#646): Song Select's
///     <see cref="ISetlistPanel" /> and the flow screens' <see cref="IGetReadySetlist" />,
///     <see cref="IPausedSetlist" /> and <see cref="IResultsSetlist" />.
///     <list type="bullet">
///         <item>Every fake variant's setlist part, and the app's
///         <see cref="PlaySetlistViewModel" />, conform to their part contract under
///         <c>ContractConformanceTests</c>' rule (the screen-level run reads only the
///         part's reference, never its members).</item>
///         <item>The variants carry the states the gallery is for: none, a setlist
///         picked, an empty one, a long one, one that ended early, mid-run, last song,
///         a skipped song and long text.</item>
///         <item>The SHIPPED views reach the setlist only through those contracts —
///         the shared <c>SetlistPanelView</c> binds every <see cref="ISetlistPanel" />
///         member, and Get Ready, Pause and Results bind no setlist member off the
///         concrete view model.</item>
///     </list>
/// </summary>
public sealed class SetlistContractTests
{
    private static readonly (Type Contract, Func<object, object> Part, Type PartContract)[] Screens =
    [
        (typeof(ISongSelectScreen), s => ((ISongSelectScreen)s).Setlists, typeof(ISetlistPanel)),
        (typeof(ITuningCheckScreen), s => ((ITuningCheckScreen)s).Setlist, typeof(IGetReadySetlist)),
        (typeof(IPausedScreen), s => ((IPausedScreen)s).Setlist, typeof(IPausedSetlist)),
        (typeof(IResultsScreen), s => ((IResultsScreen)s).Setlist, typeof(IResultsSetlist))
    ];

    public static TheoryData<string, string> FakeVariants
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (var (contract, _, _) in Screens)
            {
                foreach (var variant in FakeScreens.VariantsOf(contract))
                    data.Add(contract.Name, variant.Name);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(FakeVariants))]
    public void Every_fake_variants_setlist_part_conforms_to_its_contract(string contractName, string variantName)
    {
        var (contract, part, partContract) = Screens.Single(s => s.Contract.Name == contractName);
        var screen = FakeScreens.VariantsOf(contract).Single(v => v.Name == variantName).Create();

        var violations = ContractConformanceTests.Violations(partContract, part(screen));
        Assert.True(violations.Count == 0,
            $"{contractName} {variantName}, its {partContract.Name}:\n  {string.Join("\n  ", violations)}");
    }

    [Theory]
    [InlineData(typeof(ISetlistPanel))]
    [InlineData(typeof(IGetReadySetlist))]
    [InlineData(typeof(IPausedSetlist))]
    [InlineData(typeof(IResultsSetlist))]
    public void The_apps_setlist_view_model_conforms_to_every_setlist_contract(Type partContract)
    {
        var setlists = new PlaySetlistViewModel(FakeLocalization.English());

        var violations = ContractConformanceTests.Violations(partContract, setlists);
        Assert.True(violations.Count == 0, $"PlaySetlistViewModel as {partContract.Name}:\n  {string.Join("\n  ", violations)}");
    }

    [Fact]
    public void The_apps_panel_raises_its_list_state_when_a_setlist_is_made()
    {
        ISetlistPanel panel = new PlaySetlistViewModel(FakeLocalization.English());
        var raised = new List<string?>();
        panel.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        panel.NewSetlistCommand.Execute(null);

        var list = Assert.Single(panel.Lists);
        Assert.Same(list, panel.SelectedList);
        Assert.Equal(list.Name, panel.SelectedListName);
        Assert.True(panel.ShowsEmptyHint);
        Assert.False(panel.CanPlaySelected);
        Assert.Contains(nameof(ISetlistPanel.Lists), raised);
        Assert.Contains(nameof(ISetlistPanel.ShowsEmptyHint), raised);
    }

    [Fact]
    public void The_song_select_variants_show_every_panel_state()
    {
        Assert.False(FakeSongSelectScreen.Library().Setlists.HasLists);
        Assert.False(FakeSongSelectScreen.Empty().Setlists.HasLists);

        var picked = FakeSongSelectScreen.Setlist().Setlists;
        Assert.True(picked is { HasEntries: true, CanPlaySelected: true, CanAddSong: true });
        Assert.True(picked.Lists.Count >= 3, "the picker has something to pick between");
        Assert.Contains(picked.Entries, e => e.HasDetail && e.Detail.Contains('%', StringComparison.Ordinal));

        Assert.True(FakeSongSelectScreen.SetlistEmpty().Setlists is { ShowsEmptyHint: true, CanPlaySelected: false });
        Assert.NotEmpty(FakeSongSelectScreen.SetlistEnded().Setlists.Notice);
        Assert.True(FakeSongSelectScreen.Full().Setlists.Entries.Count >= 20, "Full is a setlist long enough to scroll");
        Assert.True(HasNonLatin(FakeSongSelectScreen.LongText().Setlists.Lists.Select(l => l.Name)));
    }

    [Fact]
    public void The_flow_variants_show_mid_run_last_song_and_a_skipped_song()
    {
        Assert.False(FakeResultsScreen.Strong().Setlist.IsRunning);
        Assert.True(FakeResultsScreen.SetlistNext().Setlist is { HasNext: true, IsFinished: false });
        Assert.StartsWith("Next: ", FakeResultsScreen.SetlistNext().Setlist.NextLabel, StringComparison.Ordinal);
        Assert.True(FakeResultsScreen.SetlistDone().Setlist is { HasNext: false, IsFinished: true });
        Assert.NotEmpty(FakeResultsScreen.SetlistDone().Setlist.FinishedLabel);

        Assert.False(FakePausedScreen.MidRun().Setlist.IsRunning);
        Assert.True(FakePausedScreen.InSetlist().Setlist.IsRunning);

        Assert.False(FakeTuningCheckScreen.InSetlist().Setlist.HasSkippedNote);
        Assert.True(FakeTuningCheckScreen.SetlistSkipped().Setlist.HasSkippedNote);
        Assert.True(FakeTuningCheckScreen.LongText().Setlist.ProgressLabel.Length > 50, "LongText carries a long setlist name");
        Assert.True(FakeResultsScreen.LongText().Setlist.NextLabel.Length > 60, "LongText carries a long next title");
    }

    [Fact]
    public void A_fake_run_moves_to_its_next_song_and_ends()
    {
        var run = FakeResultsScreen.SetlistNext().Setlist;
        var before = run.ProgressLabel;

        run.NextSetlistSongCommand.Execute(null);
        Assert.NotEqual(before, run.ProgressLabel);

        run.EndSetlistCommand.Execute(null);
        Assert.False(run.IsRunning);
        Assert.Equal("", run.ProgressLabel);
    }

    // ── the shipped views bind the contracts ─────────────────────────────

    [Fact]
    public void The_shared_setlist_panel_binds_every_member_of_its_contract()
    {
        var xaml = UiSourceTree.ReadAllText(ViewPath("SetlistPanelView"));
        Assert.Equal("contracts:ISetlistPanel", XDocument.Parse(xaml).Root!.Attribute(XamlNs + "DataType")?.Value);
        Assert.Empty(ContractBindingLintTests.UnboundMembers(typeof(ISetlistPanel), xaml));
    }

    [Theory]
    [InlineData("GetReadyView")]
    [InlineData("PauseMenuView")]
    [InlineData("ResultsView")]
    [InlineData("SongSelectDetailRailView")]
    public void A_shipped_flow_view_reaches_the_setlist_only_through_a_contract(string view)
    {
        var xaml = UiSourceTree.ReadAllText(ViewPath(view));
        Assert.Contains("{Binding Setlists}", xaml, StringComparison.Ordinal);
        Assert.Empty(ConcreteSetlistBindings(xaml));
    }

    [Fact]
    public void Probe_a_path_through_the_view_model_or_an_untyped_setlist_scope_is_named()
    {
        const string bad = """
            <UserControl xmlns="https://github.com/avaloniaui" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                         xmlns:contracts="using:DotCharter.Presentation.Contracts.PlayMode">
                <StackPanel>
                    <TextBlock Text="{Binding Setlists.ProgressLabel}" />
                    <TextBlock DataContext="{Binding Setlists}" Text="{Binding ProgressLabel}" />
                    <TextBlock DataContext="{Binding Setlists}" x:DataType="contracts:IPausedSetlist" Text="{Binding ProgressLabel}" />
                </StackPanel>
            </UserControl>
            """;

        Assert.Equal(2, ConcreteSetlistBindings(bad).Count);
    }

    // ── the rule ─────────────────────────────────────────────────────────

    private static readonly XNamespace XamlNs = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static readonly Regex ThroughTheViewModel = new(@"\{\s*(?:Compiled)?Binding\s+!?Setlists\.", RegexOptions.CultureInvariant);

    /// <summary>A binding that walks <c>Setlists.</c> off Play Mode's view model, and every
    /// element that takes <c>Setlists</c> as its DataContext without typing it to a
    /// <c>DotCharter.Presentation.Contracts.PlayMode</c> contract (or hosting the
    /// contract-bound <c>SetlistPanelView</c>).</summary>
    private static List<string> ConcreteSetlistBindings(string xaml)
    {
        var offenders = new List<string>();
        var doc = XDocument.Parse(xaml);
        foreach (var element in doc.Descendants())
        {
            offenders.AddRange(element.Attributes()
                .Where(a => ThroughTheViewModel.IsMatch(a.Value))
                .Select(a => $"{element.Name.LocalName}.{a.Name.LocalName}=\"{a.Value}\""));

            if (element.Attribute("DataContext")?.Value != "{Binding Setlists}") continue;
            if (element.Name.LocalName == "SetlistPanelView") continue;
            var type = element.Attribute(XamlNs + "DataType")?.Value ?? "";
            var prefix = type.Split(':')[0];
            if (type.Length == 0 || element.GetNamespaceOfPrefix(prefix)?.NamespaceName != "using:DotCharter.Presentation.Contracts.PlayMode")
                offenders.Add($"{element.Name.LocalName} takes Setlists untyped ({type})");
        }

        return offenders;
    }

    private static bool HasNonLatin(IEnumerable<string> texts) => texts.Any(t => t.Any(c => c > 'ɏ'));

    private static string ViewPath(string view) =>
        UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "Views", "PlayMode", "Screens", view + ".axaml"));
}
