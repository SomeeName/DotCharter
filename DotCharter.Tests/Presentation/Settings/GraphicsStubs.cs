using System.Reflection;
using DotCharter.Tone.Audio.Abstractions;
using DotCharter.Services.Editing;
using DotCharter.Services.Infrastructure.Graphics;

namespace DotCharter.Tests.Presentation.Settings;

/// <summary>The three seams <c>GraphicsSettingsViewModel</c> takes, as recording stubs.</summary>
public static class GraphicsStubs
{
    internal sealed class FakeDevice : IGraphicsDeviceSettings
    {
        public List<string> Calls { get; } = [];
        public IReadOnlyList<string> SupportedPresentModes { get; set; } = ["Fifo", "Mailbox", "Immediate"];
        public IReadOnlyList<int> SupportedMsaaSamples { get; set; } = [1, 2, 4, 8];
        public int DefaultMsaaSamples => 4;
        public double MaxVisualLeadMs => 150;
        public void SetPresentMode(string presentMode) => Calls.Add("present:" + presentMode);
        public void SetMsaaSamples(int samples) => Calls.Add("msaa:" + samples);
        public void SetRenderScalePercent(int percent) => Calls.Add("scale:" + percent);
        public void SetMaxFps(int fps) => Calls.Add("fps:" + fps);
        public void SetIdleFps(int fps) => Calls.Add("idle:" + fps);
        public void SetTimelineVisuals(bool polish, bool laneTint) => Calls.Add($"timeline:{polish}/{laneTint}");
        public void SetVisualLeadTrimMs(double ms) => Calls.Add("lead:" + ms);
        public void SetFingeringOverlay(bool enabled) => Calls.Add("fingering:" + enabled);
    }

    internal sealed class FakeWizard : ILatencyCalibrationPrompt
    {
        public event EventHandler<double>? CalibrationApplied;
        public int Opens { get; private set; }

        public Task OpenAsync()
        {
            Opens++;
            return Task.CompletedTask;
        }
        public void Apply(double lead) => CalibrationApplied?.Invoke(this, lead);
        public bool HasSubscribers => CalibrationApplied is not null;
    }

    /// <summary>The editor surface is wide; the screen only ever asks it to redraw.</summary>
    public class RedrawCounter : DispatchProxy
    {
        public int Redraws { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IChartEditorSurface.RequestRedraw))
            {
                Redraws++;
                return null;
            }

            throw new NotSupportedException(targetMethod?.Name);
        }
    }
}
