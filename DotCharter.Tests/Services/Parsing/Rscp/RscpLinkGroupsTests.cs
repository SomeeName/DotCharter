using System.Xml.Linq;
using DotCharter.Core.Linking;
using DotCharter.Tone.Infrastructure.Storage;
using DotCharter.Services.Parsing.Rscp;
using DotCharter.Tests.Services.State.Linking;
using static DotCharter.Tests.Core.Linking.LinkFixture;

namespace DotCharter.Tests.Services.Parsing.Rscp;

/// <summary>
///     <c>&lt;dcLinkGroups&gt;</c> (#88; <c>docs/DCPROJ-FORMAT-GUARANTEES.md</c> § Linked measures): the
///     links round-trip, an older build reads the chart in full and keeps the node as residue, and
///     the load check unlinks — never rewrites — a copy that stopped matching.
/// </summary>
public sealed class RscpLinkGroupsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"dc_rscp_links_{Guid.NewGuid():N}");

    public RscpLinkGroupsTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => BestEffortDelete.Directory(_dir);

    private static LinkHarness Linked(out LinkGroup group)
    {
        var h = LinkHarness.WithRhythm();
        group = h.Link((h.Lead, 1), (h.Lead, 4), (h.Lead, 7), (h.Rhythm, 4));
        group.Name = "Chorus";
        return h;
    }

    private ChartProject RoundTrip(ChartProject project, string name = "p.rscp")
    {
        var path = Path.Combine(_dir, name);
        new RscpChartSerializer().Save(path, project);
        return new RscpChartSerializer().Load(path);
    }

    [Fact]
    public void Links_round_trip_with_their_arrangements_barlines_letter_name_and_rule()
    {
        var h = Linked(out var group);

        var loaded = RoundTrip(h.Project);

        var back = Assert.Single(loaded.LinkGroups);
        Assert.Equal(group.Id, back.Id);
        Assert.Equal(group.Letter, back.Letter);
        Assert.Equal("Chorus", back.Name);
        Assert.Equal(group.Rule, back.Rule);
        Assert.Equal(
            group.Placements.Select(p => (h.Project.Arrangements.IndexOf(p.Arrangement!), p.Position, p.EndPosition)),
            back.Placements.Select(p => (loaded.Arrangements.IndexOf(p.Arrangement!), p.Position, p.EndPosition)));
    }

    [Fact]
    public void A_project_without_links_writes_no_node()
    {
        var h = LinkHarness.WithRhythm();
        var path = Path.Combine(_dir, "plain.rscp");
        new RscpChartSerializer().Save(path, h.Project);

        Assert.Null(XDocument.Load(path).Root!.Element(RscpLinkGroups.RootElement));
    }

    /// <summary>
    ///     An older build: it has no idea what <c>dcLinkGroups</c> is. The chart it reads is the
    ///     WHOLE chart (the copies are real notes; the node holds no chart content), it keeps the node
    ///     verbatim as residue, and it edits one copy without propagating. The next load here
    ///     unlinks exactly that copy, with a notice, and keeps its edit.
    /// </summary>
    [Fact]
    public void An_older_build_keeps_the_node_and_a_copy_it_edited_is_unlinked_on_load()
    {
        var h = Linked(out _);
        var path = Path.Combine(_dir, "new.rscp");
        new RscpChartSerializer().Save(path, h.Project);

        // What an older reader sees: the document minus a node it does not know is the same chart.
        var doc = XDocument.Load(path);
        var node = doc.Root!.Element(RscpLinkGroups.RootElement)!;
        var withLinks = new RscpChartSerializer().Load(path);
        node.Remove();
        var olderPath = Path.Combine(_dir, "older-view.rscp");
        doc.Save(olderPath);
        var older = new RscpChartSerializer().Load(olderPath);
        Assert.Empty(older.LinkGroups);
        Assert.Empty(DeepCompare.Diff("arrangements", withLinks.Arrangements, older.Arrangements,
            new HashSet<string>(StringComparer.Ordinal)));

        // The older build keeps the node as residue, edits one copy, and saves.
        older.UnknownRscpNodes.Add(node.ToString(SaveOptions.DisableFormatting));
        LinkHarness.NoteAt(older.Arrangements[0], At(4, 1), 1).Fret = 11;
        var olderSaved = Path.Combine(_dir, "older-saved.rscp");
        new RscpChartSerializer().Save(olderSaved, older);

        var logger = new FakeAppLogger();
        var reloaded = new RscpChartSerializer(logger).Load(olderSaved);

        var group = Assert.Single(reloaded.LinkGroups);
        Assert.Equal(3, group.Placements.Count);
        Assert.DoesNotContain(group.Placements, p => p.Position == At(4, 0) && ReferenceEquals(p.Arrangement, reloaded.Arrangements[0]));
        Assert.Equal(11, LinkHarness.NoteAt(reloaded.Arrangements[0], At(4, 1), 1).Fret); // the edit is kept
        Assert.Contains(logger.Entries, e => e.Message.Contains("unlinked on load", StringComparison.Ordinal));
    }

    [Fact]
    public void The_load_check_reports_each_unlinked_copy_and_keeps_copies_that_still_agree_together()
    {
        var h = Linked(out var group);
        var doc = new XDocument();
        new RscpWriter().WriteDocument(doc, h.Project);
        // Two copies edited the same way outside DotCharter: they leave the group but stay linked to each other.
        LinkHarness.NoteAt(h.Lead, At(7, 1), 1).Fret = 2;
        LinkHarness.NoteAt(h.Rhythm, At(4, 1), 1).Fret = 2;
        var reread = new ChartProject();
        foreach (var beat in h.Project.Beats) reread.Beats.Add(beat.Clone());
        foreach (var arrangement in h.Project.Arrangements) reread.Arrangements.Add(ChartProjectCloner.CloneArrangement(arrangement));

        var notices = RscpLinkGroups.Read(doc, reread);

        var notice = Assert.Single(notices);
        Assert.Equal(LinkLoadReason.ContentChanged, notice.Reason);
        Assert.Equal(2, notice.Unlinked);
        Assert.Equal(2, reread.LinkGroups.Count);
        Assert.Equal([group.Letter, "B"], reread.LinkGroups.Select(g => g.Letter));
        Assert.All(reread.LinkGroups, g => Assert.Equal(2, g.Placements.Count));
    }

    /// <summary>
    ///     The saved hash decides which copies are the group's content, not the majority: three
    ///     copies edited alike outside DotCharter leave the group (and stay linked to each other as a
    ///     new group); the one untouched copy is what the group held.
    /// </summary>
    [Fact]
    public void The_saved_hash_not_the_majority_decides_which_copies_are_the_groups()
    {
        var h = Linked(out var group);
        var doc = new XDocument();
        new RscpWriter().WriteDocument(doc, h.Project);
        foreach (var (arrangement, measure) in new[] { (h.Lead, 4), (h.Lead, 7), (h.Rhythm, 4) })
            LinkHarness.NoteAt(arrangement, At(measure, 1), 1).Fret = 3;
        var reread = new ChartProject();
        foreach (var beat in h.Project.Beats) reread.Beats.Add(beat.Clone());
        foreach (var arrangement in h.Project.Arrangements) reread.Arrangements.Add(ChartProjectCloner.CloneArrangement(arrangement));

        var notice = Assert.Single(RscpLinkGroups.Read(doc, reread));

        Assert.Equal(3, notice.Unlinked);
        var survivor = Assert.Single(reread.LinkGroups);
        Assert.NotEqual(group.Id, survivor.Id);
        Assert.Equal(3, survivor.Placements.Count);
    }

    [Fact]
    public void A_placement_that_is_out_of_range_or_off_its_barlines_is_dropped_with_a_notice()
    {
        var h = Linked(out _);
        var doc = new XDocument();
        new RscpWriter().WriteDocument(doc, h.Project);
        var placements = doc.Root!.Element(RscpLinkGroups.RootElement)!.Descendants("linkPlacement").ToList();
        placements[0].SetAttributeValue("arr", "9");
        placements[1].SetAttributeValue("p", "17");

        var project = new RscpChartSerializer().Load(Save(doc, "bad.rscp"));

        var group = Assert.Single(project.LinkGroups);
        Assert.Equal(2, group.Placements.Count);
    }

    [Fact]
    public void A_node_of_an_unknown_version_links_nothing_and_the_chart_still_loads()
    {
        var h = Linked(out _);
        var doc = new XDocument();
        new RscpWriter().WriteDocument(doc, h.Project);
        doc.Root!.Element(RscpLinkGroups.RootElement)!.SetAttributeValue("v", "2");

        var project = new RscpChartSerializer().Load(Save(doc, "v2.rscp"));

        Assert.Empty(project.LinkGroups);
        Assert.Equal(LinkHarness.Hardest(h.Lead).Notes.Count, LinkHarness.Hardest(project.Arrangements[0]).Notes.Count);
    }

    private string Save(XDocument doc, string name)
    {
        var path = Path.Combine(_dir, name);
        doc.Save(path);
        return path;
    }
}
