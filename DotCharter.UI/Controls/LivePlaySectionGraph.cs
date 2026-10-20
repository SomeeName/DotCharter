using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using DotCharter.Services.Audio.LivePlay;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.ViewModels.Shell;
using Microsoft.Extensions.DependencyInjection;
using ProjectState = DotCharter.Services.State.ProjectState;

namespace DotCharter.UI.Controls;

/// <summary>
///     Rocksmith-style section strip above the 3D preview — the riff
///     repeater's section picker: one block per chart section, block height
///     proportional to its note/chord density, tinted live by per-section
///     ACCURACY (green → red heat), with a playhead marker. Clicking a
///     section loops it (count-in included); the active A/B loop region is
///     highlighted. Repaints on the view model's
///     <see cref="LivePlayViewModel.SectionsVersion"/> counter and the shared
///     ProjectState playhead — the section list itself is read on demand.
/// </summary>
public sealed class LivePlaySectionGraph : Control
{
    public static readonly StyledProperty<LivePlayViewModel?> LivePlayProperty =
        AvaloniaProperty.Register<LivePlaySectionGraph, LivePlayViewModel?>(nameof(LivePlay));

    public static readonly StyledProperty<int> SectionsVersionProperty =
        AvaloniaProperty.Register<LivePlaySectionGraph, int>(nameof(SectionsVersion));

    // ── Chrome palette ───────────────────────────────────────────────────────
    // RESOLVED from the theme once per attach, never per frame: this strip
    // repaints on every playhead tick during a run, and a dictionary walk per
    // brush per frame is exactly the kind of cost this control's own comments
    // exist to keep out. They were baked literals from the pre-Fire palette
    // (`#3A3B4A`, `#2A2C3A`, `#E11D48`, `#F59E0B`, `#5E9FF2`), so the strip
    // stayed on the old colours after the app moved and read as a foreign
    // widget on the run HUD. The FALLBACKS are those same literals, so a
    // missing token degrades to exactly the old appearance rather than to an
    // invisible one on a surface the player is looking at mid-song.
    //
    // The accuracy HEAT ramp below is deliberately NOT resolved: red→green is
    // a data encoding, not app chrome, and it must not move with the palette.
    private ImmutableSolidColorBrush _blockIdle = new(Color.Parse("#3A3B4A"));
    private ImmutableSolidColorBrush _playheadBrush = new(Color.Parse("#E11D48"));
    private ImmutableSolidColorBrush _baselineBrush = new(Color.Parse("#2A2C3A"));
    private ImmutableSolidColorBrush _loopBrush = new(Color.Parse("#F59E0B"), 0.18);
    private ImmutablePen _loopEdgePen = new(new ImmutableSolidColorBrush(Color.Parse("#F59E0B")), 1.5);
    private ImmutableSolidColorBrush _phraseBrush = new(Color.Parse("#5E9FF2"));
    private ImmutableSolidColorBrush _phraseDimBrush = new(Color.Parse("#3C7BD1"));
    private ImmutableSolidColorBrush _sectionBandBrush = new(Color.Parse("#F59E0B"));

    // Pure white at 10 %: a hover veil, not a colour — it has no token and
    // wants none.
    private static readonly ImmutableSolidColorBrush HoverBrush = new(Colors.White, 0.10);

    private void ResolvePalette()
    {
        _blockIdle = Solid("FireBorderStrong", _blockIdle);
        _playheadBrush = Solid("FireDanger", _playheadBrush);
        _baselineBrush = Solid("FireBorder", _baselineBrush);
        _phraseBrush = Solid("FireAccentBright", _phraseBrush);
        // A phrase behind the playhead reads as "already done", which is the
        // same job `FireAccentDeep` does everywhere else — there is no
        // `FireAccentDim`, and asking for one would have silently kept the old
        // `#3C7BD1` forever.
        _phraseDimBrush = Solid("FireAccentDeep", _phraseDimBrush);
        _sectionBandBrush = Solid("FireWarning", _sectionBandBrush);

        var loop = Solid("FireWarning", _loopEdgePen.Brush as ImmutableSolidColorBrush
                                        ?? new ImmutableSolidColorBrush(Color.Parse("#F59E0B")));
        _loopBrush = new ImmutableSolidColorBrush(loop.Color, 0.18);
        _loopEdgePen = new ImmutablePen(loop, 1.5);
    }

    private ImmutableSolidColorBrush Solid(string key, ImmutableSolidColorBrush fallback)
        => this.TryFindResource(key, out var value) && value is ISolidColorBrush brush
            ? new ImmutableSolidColorBrush(brush.Color)
            : fallback;

    private int _hoverSection = -1;
    private ProjectState? _projectState;

    public LivePlaySectionGraph() => Cursor = new Cursor(StandardCursorType.Hand);

    public LivePlayViewModel? LivePlay
    {
        get => GetValue(LivePlayProperty);
        set => SetValue(LivePlayProperty, value);
    }

    public int SectionsVersion
    {
        get => GetValue(SectionsVersionProperty);
        set => SetValue(SectionsVersionProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SectionsVersionProperty || change.Property == LivePlayProperty)
            InvalidateVisual();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // Attach is the first point at which the style scopes above this
        // control are reachable, and it is also the only moment the palette
        // can have changed under us — so it is resolved here and never again
        // per frame.
        ResolvePalette();
        // Playhead updates come straight from ProjectState (cached event
        // args, ≤60 Hz on the UI thread) instead of an Avalonia binding —
        // every bound update costs ~10 KB on the compositor thread (see
        // AudioPlaybackCoordinator).
        _projectState ??= App.Services?.GetService<ProjectState>();
        _projectState?.PropertyChanged += OnProjectStateChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _projectState?.PropertyChanged -= OnProjectStateChanged;
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>The playhead gate — the same ~30 Hz the minimap repaints at.
    /// Without it this strip repainted its whole phrase + section pass on every
    /// one of the coordinator's up-to-60 Hz publishes, to move a 2 px line.</summary>
    private RepaintThrottle _playheadRepaint = new(RepaintThrottle.DefaultIntervalMs);

    private void OnProjectStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ProjectState.CurrentTimeMs)) return;
        // The strip is only shown while Live Play is active; skip repaint
        // churn for the (common) hidden state.
        if (!IsEffectivelyVisible) return;
        if (_playheadRepaint.TryPass(Environment.TickCount64)) InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var next = SectionIndexAt(e.GetPosition(this).X);
        if (next == _hoverSection) return;
        _hoverSection = next;
        var sections = LivePlay?.Sections;
        var hint = LocalizationService.Instance["LivePlay_SectionGraph_Tip"];
        ToolTip.SetTip(this, next >= 0 && sections != null && next < sections.Count
            ? $"{sections[next].Name}  ({sections[next].SoundCount})\n{hint}"
            : hint);
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_hoverSection == -1) return;
        _hoverSection = -1;
        InvalidateVisual();
    }

    /// <summary>Height of the orange section band at the bottom — clicking
    /// IN the band picks a riff-repeater section loop (Rocksmith semantics),
    /// clicking anywhere above it seeks to the exact time under the cursor.</summary>
    private const double SectionBandHeight = 8.0;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var vm = LivePlay;
        var sections = vm?.Sections;
        if (vm == null || sections == null || sections.Count == 0) return;

        var pos = e.GetPosition(this);

        // Upper area (phrase bars): exact proportional seek — the strip is
        // the only precise navigation surface during a run.
        if (pos.Y < Bounds.Height - SectionBandHeight)
        {
            var songStart = sections[0].StartMs;
            var span = Math.Max(1.0, SongEnd(sections) - songStart);
            var targetMs = songStart + Math.Clamp(pos.X / Math.Max(1.0, Bounds.Width), 0, 1) * span;
            if (DataContext is MainWindowViewModel shell) shell.SeekTo(targetMs);
            e.Handled = true;
            return;
        }

        var index = SectionIndexAt(pos.X);
        if (index < 0) return;

        var section = sections[index];
        // Clicking the section that is already looping clears the loop.
        if (vm.LoopActive && vm.LoopLabel.Length > 0 && IsCurrentLoop(vm, section))
            vm.ClearLoopCommand.Execute(null);
        else
            vm.LoopSectionAt((section.StartMs + section.EndMs) / 2);
        e.Handled = true;
    }

    private static bool IsCurrentLoop(LivePlayViewModel vm, LivePlaySection section)
        => vm.CurrentLoop is { } loop &&
           Math.Abs(loop.StartMs - section.StartMs) < 1 && Math.Abs(loop.EndMs - section.EndMs) < 1;

    private int SectionIndexAt(double x)
    {
        var sections = LivePlay?.Sections;
        var w = Bounds.Width;
        if (sections == null || sections.Count == 0 || w <= 0) return -1;

        var songStart = sections[0].StartMs;
        var span = Math.Max(1.0, SongEnd(sections) - songStart);
        var timeMs = songStart + x / w * span;
        for (var i = 0; i < sections.Count; i++)
        {
            if (timeMs >= sections[i].StartMs && timeMs < sections[i].EndMs)
                return i;
        }

        return sections.Count - 1;
    }

    private static double SongEnd(IReadOnlyList<LivePlaySection> sections) => sections[^1].EndMs;

    // Heat changes only when a section's hit/miss counts change, but Render runs
    // at ≤60 Hz during Live Play — cache one brush per resolved colour so we
    // don't allocate a fresh ImmutableSolidColorBrush per judged section per
    // frame. The ramp yields a small, bounded set of distinct colours and Render
    // is UI-thread-only, so a plain dictionary stays tiny and needs no locking.
    private static readonly Dictionary<uint, ImmutableSolidColorBrush> HeatBrushCache = [];

    /// <summary>Accuracy heat: 1.0 → green, 0.0 → red, unjudged → idle gray.
    /// The ramp is a DATA encoding and stays fixed; only the unjudged idle
    /// colour follows the theme, which is why this takes it as an
    /// argument.</summary>
    private static ImmutableSolidColorBrush HeatBrush(LivePlaySection s, ImmutableSolidColorBrush idle)
    {
        if (s.Hits + s.Misses == 0) return idle;
        var t = Math.Clamp(s.Accuracy, 0, 1);
        // Red (#7F1D1D) → amber → green (#166534) ramp.
        var r = (byte)(0x7F + (0x16 - 0x7F) * t);
        var g = (byte)(0x1D + (0x65 - 0x1D) * t);
        var b = (byte)(0x1D + (0x34 - 0x1D) * t);
        var key = (uint)((r << 16) | (g << 8) | b);
        if (!HeatBrushCache.TryGetValue(key, out var brush))
        {
            brush = new ImmutableSolidColorBrush(Color.FromRgb(r, g, b));
            HeatBrushCache[key] = brush;
        }

        return brush;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var vm = LivePlay;
        var sections = vm?.Sections;
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (sections == null || sections.Count == 0 || w <= 0 || h <= 0) return;

        var songStart = sections[0].StartMs;
        var songEnd = SongEnd(sections);
        var span = Math.Max(1.0, songEnd - songStart);
        var barsBottom = h - SectionBandHeight;
        var playheadMs = _projectState?.CurrentTimeMs ?? 0;

        // Baseline the phrase bars sit on.
        context.FillRectangle(_baselineBrush, new Rect(0, barsBottom - 1, w, 1));

        // Rocksmith structure: PURPLE per-phrase bars, height ∝ how many
        // sounds are squeezed into the phrase (sqrt keeps sparse ones
        // visible); phrases already behind the playhead render dimmed.
        var phrases = vm!.Phrases;
        if (phrases.Count > 0)
        {
            var maxCount = 1;
            foreach (var p in phrases)
                if (p.SoundCount > maxCount) maxCount = p.SoundCount;

            foreach (var p in phrases)
            {
                if (p.SoundCount == 0) continue;
                var x0 = Math.Clamp((p.StartMs - songStart) / span, 0, 1) * w;
                var x1 = Math.Clamp((p.EndMs - songStart) / span, 0, 1) * w;
                var barH = Math.Max(3.0, Math.Sqrt(p.SoundCount / (double)maxCount) * (barsBottom - 4));
                var brush = p.EndMs < playheadMs ? _phraseDimBrush : _phraseBrush;
                context.DrawRectangle(brush, null,
                    new RoundedRect(new Rect(x0 + 0.5, barsBottom - barH,
                        Math.Max(1.0, x1 - x0 - 1.0), barH), 1));
            }
        }

        // ORANGE section band at the bottom — the riff-repeater targets.
        // Judged sections tint toward their accuracy heat so progress and
        // trouble spots stay visible at a glance.
        for (var i = 0; i < sections.Count; i++)
        {
            var s = sections[i];
            var x0 = (s.StartMs - songStart) / span * w;
            var x1 = (s.EndMs - songStart) / span * w;
            var brush = s.Hits + s.Misses > 0 ? HeatBrush(s, _blockIdle) : _sectionBandBrush;
            context.DrawRectangle(brush, null,
                new RoundedRect(new Rect(x0 + 1, barsBottom + 2,
                    Math.Max(1.0, x1 - x0 - 2.0), SectionBandHeight - 3), 1.5));

            if (i == _hoverSection)
            {
                context.DrawRectangle(HoverBrush, null,
                    new RoundedRect(new Rect(x0 + 0.5, 0, Math.Max(1.0, x1 - x0 - 1.0), h), 2));
            }
        }

        // Active A/B loop region.
        if (vm!.CurrentLoop is { } loop)
        {
            var lx0 = Math.Clamp((loop.StartMs - songStart) / span, 0, 1) * w;
            var lx1 = Math.Clamp((loop.EndMs - songStart) / span, 0, 1) * w;
            context.DrawRectangle(_loopBrush, null, new Rect(lx0, 0, Math.Max(1, lx1 - lx0), h));
            context.DrawLine(_loopEdgePen, new Point(lx0, 0), new Point(lx0, h));
            context.DrawLine(_loopEdgePen, new Point(lx1, 0), new Point(lx1, h));
        }

        // Playhead marker.
        var px = Math.Clamp((playheadMs - songStart) / span, 0, 1) * w;
        context.FillRectangle(_playheadBrush, new Rect(px - 1, 0, 2, h));
    }
}
