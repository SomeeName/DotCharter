using DotCharter.UI.ViewModels.PlayMode;

namespace DotCharter.Tests.UI.ViewModels.PlayMode;

/// <summary>
///     Get ready's path list names a path the way the song list just did. Rocksmith names
///     both guitar paths "Combo" (the loader makes the second "Combo (2)"), so the list read
///     "Combo" / "Combo (2)" right after the song list had said Lead / Rhythm (2026-09-24).
/// </summary>
public sealed class ArrangementOptionDisplayNameTests
{
    private static ArrangementOption Option(string name, ArrangementType type, int lead = 0, int rhythm = 0, int bass = 0)
    {
        var arrangement = new Arrangement { Name = name, ArrangementType = type };
        arrangement.Properties.PathLead = lead;
        arrangement.Properties.PathRhythm = rhythm;
        arrangement.Properties.PathBass = bass;
        return new ArrangementOption(arrangement, FakeLocalization.English());
    }

    [Fact]
    public void A_generic_Combo_takes_its_path_word_and_a_real_name_stays()
    {
        var options = new List<ArrangementOption>
        {
            Option("Combo", ArrangementType.Combo, lead: 1),
            Option("Combo (2)", ArrangementType.Combo, rhythm: 1),
            Option("Bass", ArrangementType.Bass, bass: 1),
            Option("Lead 1", ArrangementType.Lead, lead: 1)
        };

        ArrangementOption.AssignDisplayNames(options);

        Assert.Equal(["Lead", "Rhythm", "Bass", "Lead 1"], options.Select(o => o.DisplayName));
        Assert.Equal("Combo (2)", options[1].Name);
    }

    /// <summary>Get ready's header prints the artist beside the title, so the title is the
    /// song's own ("3 Doors Down - When I'm Gone  3 Doors Down" before).</summary>
    [Fact]
    public void Get_readys_header_does_not_repeat_the_artist()
    {
        Assert.Equal("When I'm Gone", PlayModeViewModel.HeaderTitle("When I'm Gone", "3 Doors Down", "3 Doors Down - When I'm Gone"));
        Assert.Equal("Sakasma", PlayModeViewModel.HeaderTitle("Sakasma", "", "Sakasma"));
    }

    /// <summary>With one tuning for every path the card's header says it once; a row names
    /// its tuning only when the paths differ (every row said "· E Standard").</summary>
    [Fact]
    public void A_row_names_its_tuning_only_when_the_paths_differ()
    {
        var shared = new List<ArrangementOption> { Option("Lead", ArrangementType.Lead), Option("Bass", ArrangementType.Bass) };
        ArrangementOption.AssignDisplayNames(shared);
        Assert.All(shared, o => Assert.False(o.ShowsTuning));

        var dropBass = Option("Bass", ArrangementType.Bass);
        dropBass.Arrangement.Tuning = ["D", "A", "D", "G", "B", "e"];
        var mixed = new List<ArrangementOption> { Option("Lead", ArrangementType.Lead), dropBass };
        ArrangementOption.AssignDisplayNames(mixed);
        Assert.All(mixed, o => Assert.True(o.ShowsTuning));
    }

    [Fact]
    public void Two_that_come_out_the_same_are_numbered_in_list_order()
    {
        var options = new List<ArrangementOption>
        {
            Option("Combo", ArrangementType.Lead, lead: 1),
            Option("Combo (2)", ArrangementType.Lead, lead: 1),
            Option("Combo (3)", ArrangementType.Combo)
        };

        ArrangementOption.AssignDisplayNames(options);

        Assert.Equal(["Lead", "Lead (2)", "Combo (3)"], options.Select(o => o.DisplayName));
    }
}
