using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Screens.Dialogs;
using DotCharter.Services.Editing;
using DotCharter.Services.Editing.Events;
using DotCharter.Services.Editing.Selection;

namespace DotCharter.Tests.Presentation.Dialogs;

/// <summary>
///     Timeline ▸ event marker over a real chart editor: the save is one undo step,
///     the phrase follows a picked section until the charter types their own,
///     empty ground repeats the last marker, and nothing a marker carries that the
///     dialog does not show (an off-list section, a foreign event code) is lost.
/// </summary>
public sealed class EventViewModelTests
{
    private const double OneSecond = 1000;

    [Fact]
    public void Saving_writes_the_marker_as_one_undo_step()
    {
        var rig = new Rig();
        var vm = rig.Open(OneSecond);

        vm.SelectedSection = Section(vm, "Chorus");
        vm.SectionStatusIndex = (int)SectionStatus.Review;
        vm.EventToggles[0].IsSelected = true;
        vm.SaveCommand.Execute(null);

        var marker = Assert.Single(rig.Lead.EventPoints);
        Assert.Equal("chorus", marker.Section);
        Assert.Equal("chorus", marker.Phrase);
        Assert.Equal(SectionStatus.Review, marker.Status);
        Assert.Equal(RsEventCatalog.Events[0].Code, marker.Events);
        Assert.True(ChartEditHarness.Answer(vm.Result));

        rig.Chart.History.Undo();
        Assert.Empty(rig.Lead.EventPoints);
    }

    [Fact]
    public void A_picked_section_fills_the_phrase_but_never_overwrites_a_typed_one()
    {
        var vm = new Rig().Open(OneSecond);

        vm.SelectedSection = Section(vm, "Verse");
        Assert.Equal("verse", vm.PhraseName);
        vm.SelectedSection = Section(vm, "Chorus");
        Assert.Equal("chorus", vm.PhraseName);

        vm.PhraseName = "verse2";
        vm.SelectedSection = Section(vm, "Bridge");
        Assert.Equal("verse2", vm.PhraseName);
    }

    [Fact]
    public void Empty_ground_repeats_the_last_marker_and_an_existing_marker_shows_itself()
    {
        var rig = new Rig();
        var first = rig.Open(OneSecond);
        first.SelectedSection = Section(first, "Solo");
        first.SaveCommand.Execute(null);

        var next = rig.Open(3 * OneSecond);
        Assert.Equal("solo", next.SectionName);
        Assert.Equal("solo", next.PhraseName);

        rig.Lead.EventPoints.Add(new EventPoint
        { Position = TimelineMath.PositionFromMs(2 * OneSecond, rig.Chart.Project.Beats), Section = "intro", Phrase = "intro" });
        var existing = rig.Open(2 * OneSecond);
        Assert.Equal("intro", existing.SectionName);
    }

    [Fact]
    public void An_off_list_section_and_foreign_event_codes_survive_a_save()
    {
        var rig = new Rig();
        rig.Lead.EventPoints.Add(new EventPoint
        {
            Position = TimelineMath.PositionFromMs(OneSecond, rig.Chart.Project.Beats),
            Section = "customsection",
            Phrase = "p1",
            Events = "zz9"
        });

        var vm = rig.Open(OneSecond);
        Assert.True(vm.SectionNameIsUnknown);
        Assert.Contains("customsection", vm.SectionOptions[^1].Display, StringComparison.Ordinal);
        Assert.Same(vm.SectionOptions[^1], vm.SelectedSection);

        vm.ToneName = "lead";
        vm.SaveCommand.Execute(null);

        var marker = Assert.Single(rig.Lead.EventPoints);
        Assert.Equal("customsection", marker.Section);
        Assert.Equal("zz9", marker.Events);
    }

    [Fact]
    public void A_marker_with_only_event_codes_is_saved()
    {
        var rig = new Rig();
        var vm = rig.Open(OneSecond);

        vm.EventToggles[^1].IsSelected = true;
        vm.SaveCommand.Execute(null);

        var marker = Assert.Single(rig.Lead.EventPoints);
        Assert.Equal("", marker.Section);
        Assert.Equal(RsEventCatalog.Events[^1].Code, marker.Events);
    }

    [Fact]
    public void A_tone_with_no_body_is_warned_about_and_still_placed()
    {
        var rig = new Rig();
        var vm = rig.Open(OneSecond);

        vm.ToneName = "never_authored";
        Assert.True(vm.ToneHasNoBody);
        vm.SaveCommand.Execute(null);

        Assert.Equal("never_authored", Assert.Single(rig.Lead.ToneChanges).ToneName);
    }

    [Fact]
    public void A_ticked_second_arrangement_gets_the_marker_too_and_the_clicked_one_cannot_be_unticked()
    {
        var rig = new Rig();
        var rhythm = new Arrangement { Name = "Rhythm" };
        rhythm.Levels.Add(new ChartLevel());
        rig.Chart.Project.Arrangements.Add(rhythm);

        var vm = rig.Open(OneSecond);
        Assert.True(vm.HasMultipleArrangements);
        Assert.True(vm.Arrangements[0].IsActive);
        Assert.False(vm.Arrangements[1].IsSelected);

        vm.Arrangements[1].IsSelected = true;
        vm.SelectedSection = Section(vm, "Intro");
        vm.SaveCommand.Execute(null);

        Assert.Single(rig.Lead.EventPoints);
        Assert.Equal("intro", Assert.Single(rhythm.EventPoints).Section);
    }

    [Fact]
    public void Cancel_answers_false_and_writes_nothing()
    {
        var rig = new Rig();
        var vm = rig.Open(OneSecond);
        vm.SelectedSection = Section(vm, "Chorus");

        vm.CancelCommand.Execute(null);

        Assert.False(ChartEditHarness.Answer(vm.Result));
        Assert.Empty(rig.Lead.EventPoints);
    }

    [Fact]
    public void Conforms_to_its_contract()
    {
        var vm = new Rig().Open(OneSecond);
        Assert.Empty(ContractConformanceTests.Violations(typeof(IEventScreen), vm));
    }

    private static IEventSectionOption Section(IEventScreen vm, string display) =>
        vm.SectionOptions.First(o => o.Display == display);

    private sealed class Rig
    {
        public Rig()
        {
            var selection = new SelectionService();
            Events = new EventEditingService(Chart.State, selection, Chart.History, new EditingScope(Chart.State),
                new FakeAppLogger());
        }

        public ChartEditHarness Chart { get; } = new();

        public EventEditingService Events { get; }

        public Arrangement Lead => Chart.Project.Arrangements[0];

        public EventViewModel Open(double timeMs)
        {
            var vm = new EventViewModel(Chart.Services, Events);
            vm.Configure(new EventRequest(timeMs, Lead));
            return vm;
        }
    }
}
