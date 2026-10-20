namespace DotCharter.Presentation.Fakes.PlayMode;

public sealed partial class FakeSongSelectScreen
{
    /// <summary>A song picked and a five-song setlist picked beside it.</summary>
    public static FakeSongSelectScreen Setlist() => WithSetlists(Library(), FakeSetlistPanel.Picked());

    /// <summary>A setlist just made, with no songs yet.</summary>
    public static FakeSongSelectScreen SetlistEmpty() => WithSetlists(Library(), FakeSetlistPanel.EmptyList());

    /// <summary>Back on Song Select after a setlist ended early: the panel names the songs it skipped.</summary>
    public static FakeSongSelectScreen SetlistEnded() => WithSetlists(Library(), FakeSetlistPanel.Skipped());

    private static FakeSongSelectScreen WithSetlists(FakeSongSelectScreen screen, FakeSetlistPanel setlists)
    {
        screen.Setlists = setlists;
        return screen;
    }
}
