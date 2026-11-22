using DotCharter.Presentation.Screens.Dialogs;
using DotCharter.Services.Editing;
using DotCharter.Services.Editing.Events;
using DotCharter.Services.Editing.Selection;

namespace DotCharter.Tests.Presentation.Dialogs;

/// <summary>The timeline in-place editors over the real chart editor of <see cref="ChartEditHarness" />.</summary>
internal sealed class InlineEditRig
{
    public InlineEditRig()
    {
        Events = new EventEditingService(Chart.State, new SelectionService(), Chart.History, new EditingScope(Chart.State),
            new FakeAppLogger());
        Inline = new InlineEditViewModel(new FhpInlineEditorViewModel(Chart.Services),
            new SectionQuickAddViewModel(Chart.Services, Events), new LyricInlineEditorViewModel(Chart.Services), Chart.State);
    }

    public ChartEditHarness Chart { get; } = new();

    public EventEditingService Events { get; }

    public InlineEditViewModel Inline { get; }

    public Arrangement Lead => Chart.Project.Arrangements[0];

    /// <summary>Undoes everything and returns how many steps that took.</summary>
    public int UndoSteps()
    {
        var steps = 0;
        while (Chart.History.CanUndo)
        {
            Chart.History.Undo();
            steps++;
        }

        return steps;
    }
}
