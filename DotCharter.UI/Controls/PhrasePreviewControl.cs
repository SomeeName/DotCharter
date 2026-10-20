using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using DotCharter.Services.Editing.Catalog;
using DotCharter.UI.Controls.Themes;

namespace DotCharter.UI.Controls;

/// <summary>
///     A compact tab-style preview of one phrase-catalog entry: string lanes
///     across, beats along, a fret number where each sound sits.
///     <para>
///         Editor-on-Fire renders its catalog entry live in the info panel
///         (<c>eof_render_fret_catalog_window</c>) — knowing what a named phrase
///         actually contains before you paste it is most of what makes a catalog
///         usable. This is the same idea at panel scale.
///     </para>
///     <para>
///         Colours come from <see cref="ChartTheme" /> (the runtime palette the
///         timeline and highway already draw with), so a theme switch changes the
///         preview with everything else and there is no hard-coded colour here.
///         The control neither subscribes to anything nor overrides
///         <c>OnAttachedToVisualTree</c> — it repaints only when its
///         <see cref="Entry" /> changes.
///     </para>
/// </summary>
public class PhrasePreviewControl : Control
{
    public static readonly StyledProperty<PhraseCatalogEntry?> EntryProperty =
        AvaloniaProperty.Register<PhrasePreviewControl, PhraseCatalogEntry?>(nameof(Entry));

    private const double SidePadding = 6;
    private const double MinLaneHeight = 7;
    private const double MinSpanInBeats = 1.0;

    static PhrasePreviewControl() => AffectsRender<PhrasePreviewControl>(EntryProperty);

    public PhraseCatalogEntry? Entry
    {
        get => GetValue(EntryProperty);
        set => SetValue(EntryProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        context.FillRectangle(ChartTheme.BottomMarginBgBrush, bounds);

        var entry = Entry;
        if (entry == null || bounds.Width <= 2 * SidePadding || bounds.Height <= 4) return;

        var strings = Math.Max(1, entry.Source.StringCount);
        var laneHeight = Math.Max(MinLaneHeight, bounds.Height / strings);
        var plotWidth = bounds.Width - 2 * SidePadding;
        var span = Math.Max(MinSpanInBeats, entry.LengthInBeats);

        double X(FractionalPosition position) =>
            SidePadding + Math.Clamp(position.DoubleValue / span, 0, 1) * plotWidth;

        // String index 0 is the LOW string and sits at the bottom, the way a
        // fretboard diagram reads.
        double LaneY(int stringIndex) =>
            bounds.Height - (Math.Clamp(stringIndex, 0, strings - 1) + 0.5) * laneHeight;

        // Hand shapes first, so the sounds they cover draw on top of the bar.
        foreach (var shape in entry.Payload.HandShapes)
        {
            var x0 = X(shape.Position);
            var x1 = Math.Max(x0 + 2, X(shape.EndPosition));
            context.FillRectangle(ChartTheme.HandShapeBgBrush, new Rect(x0, 0, x1 - x0, bounds.Height));
        }

        foreach (var anchor in entry.Payload.Anchors)
        {
            var x = X(anchor.Position);
            context.FillRectangle(ChartTheme.GoldBrush, new Rect(x, bounds.Height - 3, 2, 3));
        }

        for (var s = 0; s < strings; s++)
        {
            var y = LaneY(s);
            context.FillRectangle(ChartTheme.MutedNoteBrush, new Rect(SidePadding, y - 0.5, plotWidth, 1));
        }

        foreach (var note in entry.Payload.Notes)
        {
            DrawSound(context, note.StringIndex, note.Fret, X(note.Position),
                X(note.Position + note.Sustain), LaneY(note.StringIndex), strings);
        }

        foreach (var chord in entry.Payload.Chords)
        {
            foreach (var chordNote in chord.ChordNotes)
            {
                DrawSound(context, chordNote.StringIndex, chordNote.Fret, X(chord.Position),
                X(chord.Position + chordNote.Sustain), LaneY(chordNote.StringIndex), strings);
            }
        }
    }

    private static void DrawSound(DrawingContext context, int stringIndex, int fret,
        double x, double sustainEnd, double y, int stringCount)
    {
        var brush = ChartTheme.StringBrushFor(Math.Abs(stringIndex), stringCount);

        if (sustainEnd > x + 2)
            context.FillRectangle(brush, new Rect(x, y - 1, sustainEnd - x, 2));

        var text = ChartTheme.GetCachedText(fret.ToString(), 10, brush, true);
        context.DrawText(text, new Point(x - text.Width / 2, y - text.Height / 2));
    }
}
