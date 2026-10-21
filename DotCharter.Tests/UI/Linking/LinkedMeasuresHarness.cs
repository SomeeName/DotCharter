using DotCharter.Services.Editing.Selection;
using DotCharter.Services.Editing.Tracks;
using DotCharter.Tests.Services.State.Linking;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.ViewModels.Editor.Linking;
using DotCharter.Tests.Core.Linking;

namespace DotCharter.Tests.UI.Linking;

/// <summary>
///     The linked-measures view models over a live <see cref="LinkHarness" /> session: a real
///     history, the editor view model on the same project state, English strings.
/// </summary>
internal sealed class LinkedMeasuresHarness
{
    public LinkedMeasuresHarness(LinkHarness links)
    {
        Links = links;
        var config = new FakeConfigurationService { AppConfig = links.Config };
        var localization = FakeLocalization.English();
        var selection = new SelectionService();
        var transport = Transport = new TransportViewModel(new FakeAudioPlaybackService(), links.State, config, localization);
        var tracks = new TrackContextRegistry(links.State, selection);
        Editor = new ChartEditorViewModel(links.State, transport, links.History, selection, new TempoService(new FakeAppLogger()),
            tracks, localization);
        Vm = new LinkedMeasuresViewModel(Editor, links.State, links.History, localization, config);
    }

    public LinkHarness Links { get; }
    public TransportViewModel Transport { get; }
    public ChartEditorViewModel Editor { get; }
    public LinkedMeasuresViewModel Vm { get; }

    public static LinkedMeasuresHarness WithRhythm() => new(LinkHarness.WithRhythm());

    /// <summary>The first note of the hardest Lead level in <paramref name="measure" />.</summary>
    public GuitarNote NoteIn(int measure) =>
        LinkHarness.Hardest(Links.Lead).Notes.First(n =>
            n.Position >= LinkFixture.At(measure, 0) && n.Position < LinkFixture.At(measure + 1, 0));

    public void Select(GuitarNote note)
    {
        Editor.Selection.SelectedNotes.Clear();
        Editor.Selection.SelectedNotes.Add(note);
    }
}
