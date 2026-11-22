using DotCharter.Core.Domain.Events;
using DotCharter.Core.Domain.Project;
using DotCharter.Core.Domain.Vocals;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Services.State;

namespace DotCharter.Presentation.Screens.Dialogs;

/// <summary>
///     The timeline's in-place editors (fret-hand position, section, lyric) share one host.
///     Only one draft is open at a time.
/// </summary>
public sealed class InlineEditViewModel(
    FhpInlineEditorViewModel fhp,
    SectionQuickAddViewModel section,
    LyricInlineEditorViewModel lyric,
    ProjectState projectState)
{
    public FhpInlineEditorViewModel Fhp { get; } = fhp;

    public SectionQuickAddViewModel Section { get; } = section;

    public LyricInlineEditorViewModel Lyric { get; } = lyric;

    public ProjectState State { get; } = projectState;

    public object? Host { get; set; }

    /// <summary>Opens the full event dialog from the popover's "More...".</summary>
    public Action<EventRequest>? OpenEventDialog { get; set; }

    public bool Hosts(object? viewport) => Host != null && ReferenceEquals(Host, viewport);

    public void ShowFhp(double timeMs, FretAnchor? anchor) => ShowFhp(new FhpRequest(timeMs, anchor));

    public void ShowFhp(FhpRequest request)
    {
        Lyric.Cancel();
        Section.Cancel();
        Fhp.Begin(request);
    }

    public void ShowSection(double timeMs, Arrangement? target)
    {
        Lyric.Cancel();
        Fhp.Cancel();
        Section.Begin(timeMs, target);
    }

    public void ShowFullEventDialog()
    {
        if (Section.MoreRequest() is { } request) OpenEventDialog?.Invoke(request);
    }

    public void ShowLyric(double timeMs, LyricEvent? lyric)
    {
        Fhp.Cancel();
        Section.Cancel();
        Lyric.Begin(timeMs, lyric);
    }
}
