using Avalonia;
using Avalonia.Input;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Services.Editing.Highway;
using DotCharter.Services.Editing.Tracks;
using DotCharter.Tests.Presentation.Dialogs;
using DotCharter.UI.Constants;
using DotCharter.UI.Controls;
using DotCharter.UI.Editing.Tracks;
using DotCharter.UI.Input;
using DotCharter.UI.Input.Tools;

namespace DotCharter.Tests.UI.Input.Tools;

/// <summary>A real anchor tool and chart editor over a row viewport and an editor-request recorder.</summary>
internal sealed class FretAnchorToolHarness
{
    public FretAnchorToolHarness(bool labels = true)
    {
        Chart.State.PixelsPerMs = 1;
        var arrangement = Chart.State.ActiveArrangement!;
        if (!labels)
        {
            arrangement = new Arrangement { Name = "Rhythm" };
            arrangement.Levels.Add(new ChartLevel());
            Chart.Project.Arrangements.Add(arrangement);
        }

        Track = Chart.Editor.TrackContexts.GetOrCreate(arrangement);
        Anchor = new FretAnchor(new FractionalPosition(1), 3, 5);
        Track.Anchors.Add(Anchor);
        Viewport = new RowViewport(labels);
        Tool = new FretAnchorInteractionTool(RecordingProxy.Create<IHighwayEditingService>().Proxy,
            Chart.History, new FakeAppLogger(), Chart.State, Chart.Editor, Launcher);
        Context = new ToolContext(null!, Viewport, Chart.Editor.TrackContexts) { Track = Track };
    }

    public ChartEditHarness Chart { get; } = new();
    public TrackEditorContext Track { get; }
    public FretAnchor Anchor { get; }
    public RowViewport Viewport { get; }
    public RequestRecorder Launcher { get; } = new();
    public FretAnchorInteractionTool Tool { get; }
    public ToolContext Context { get; }

    public void Press(double timeMs, bool right = true, int clicks = 1, KeyModifiers modifiers = KeyModifiers.None)
    {
        SetPosition(timeMs);
        Context.Modifiers = modifiers;
        Context.Properties = new PointerPointProperties(right ? RawInputModifiers.RightMouseButton : RawInputModifiers.LeftMouseButton,
            right ? PointerUpdateKind.RightButtonPressed : PointerUpdateKind.LeftButtonPressed);
        var e = new PointerPressedEventArgs(Viewport, new Pointer(1, PointerType.Mouse, true), null!,
            Context.Position, 0, Context.Properties, modifiers, clicks);
        Tool.OnPointerPressed(e, Context);
    }

    public void Hover(double timeMs)
    {
        SetPosition(timeMs);
        Context.Properties = new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.Other);
        Tool.OnPointerMoved(null!, Context);
    }

    private void SetPosition(double timeMs)
    {
        Context.ExactTimeMs = timeMs;
        Context.Position = new Point((timeMs - Chart.State.TimelineViewAnchorEffectiveMs) * Chart.State.PixelsPerMs
            + ChartEditorConstants.PlayheadX, Viewport.ViewportHeight - Viewport.BottomMargin
            + ChartEditorConstants.RenderMetrics.FhpYOffset + 9);
        Context.ResetSnapCache();
    }

    internal sealed class RequestRecorder : IFhpEditLauncher
    {
        public List<FhpRequest> Requests { get; } = [];
        public void EditFhpInPlace(object? viewport, FhpRequest request) => Requests.Add(request);
    }

    internal sealed class RowViewport(bool labels) : IChartViewport
    {
        public double ViewportWidth => 2400;
        public double ViewportHeight => 400;
        public double TopMargin => 40;
        public double BottomMargin => 40;
        public bool LeftyFlip => false;
        public bool InvertStrings => false;
        public RibbonLayout TopRibbon => default;
        public float? MeasureTimelineLabelWidth(string text) => labels ? 16f : null;
        public void RequestNextFrameRendering() { }
        public void SetMarquee(Rect? marquee) { }
        public void ShowContextMenu(IReadOnlyList<TimelineMenuItem> items) { }
    }
}
