using System.Diagnostics;
using AlphaSkia;
using AlphaTab;
using AlphaTab.Core.EcmaScript;
using AlphaTab.Importer;
using AlphaTab.Model;
using AlphaTab.Rendering;
using DotCharter.Services.Infrastructure;
using SkiaSharp;
using Environment = AlphaTab.Environment;

namespace DotCharter.Services.Export.Sheet;

/// <summary>
/// Renders an AlphaTex source string to a list of <see cref="SKImage"/> chunks
/// using AlphaTab's Skia engine + AlphaSkia native backend, then hands the
/// chunks back so an Avalonia control can blit them directly onto the
/// active GPU canvas via <see cref="Avalonia.Rendering.SceneGraph.ICustomDrawOperation"/>.
/// We never round-trip through PNG — the AlphaSkia pixels are copied once into
/// an SKImage and reused for every paint.
/// </summary>
public sealed class SheetMusicRenderer
{
    /// <summary>
    ///     <see cref="EnsureAlphaSkiaLoaded" />'s completion barrier. This was an
    ///     <c>Interlocked.Exchange</c> claim flag — "somebody has STARTED
    ///     loading" — so the second of two renderers on a cold process (the sheet
    ///     strip becoming visible while a Sheet-PDF export starts; both entry
    ///     points are <c>Task.Run</c>, on separate instances) saw the flag and
    ///     walked straight into <c>ReadScore</c>/<c>RenderScore</c> with AlphaSkia
    ///     half-loaded and no fonts registered — which this file's own note
    ///     below says "silently draws nothing for text runs" (concurrency audit
    ///     #7). A lock is a barrier: the second caller WAITS for the first to
    ///     finish, and a failed load leaves the flag clear so the next caller
    ///     retries instead of running on a half-initialised process.
    /// </summary>
    private static readonly object AlphaSkiaGate = new();

    private static bool _alphaSkiaReady;
    private static byte[]? _bravuraFont;

    /// <summary>Which staves to lay out: 0 = notation + tab, 1 = notation
    /// only, 2 = tab only. Fewer staves = larger glyphs at the same strip
    /// height, since the fit-to-height pass divides it among the staves.</summary>
    public const int StavesBoth = 0;
    public const int StavesNotation = 1;
    public const int StavesTab = 2;

    /// <summary>
    ///     The ink this render lays down. <see cref="Screen" /> is light glyphs
    ///     for the dark backdrop <c>SheetDrawOp</c> paints behind the strip; it
    ///     is the default because the on-screen strip and page views are the
    ///     only callers that render onto that backdrop.
    ///     <para>
    ///         <see cref="Print" /> exists because <c>SheetPdfExporter</c> writes
    ///         onto WHITE PAPER and never fills a page background, so the screen
    ///         palette put every staff line, note head, stem and fret digit at
    ///         <c>#E8E8E8</c> on white — about 1.15:1, i.e. blank — while the
    ///         exporter's own title block, drawn in <c>SKColors.Black</c>, came
    ///         out fine. The notation raster goes through AlphaSkia and is not
    ///         reproducible headless, so no golden frame covers it and it went
    ///         unnoticed.
    ///     </para>
    /// </summary>
    public enum InkPalette
    {
        Screen,
        Print
    }

    public Task<RenderResult?> RenderAsync(string tex, double width, double displayScale, int staves,
        CancellationToken cancellationToken) =>
        Task.Run(() => RenderInternal(tex, width, displayScale, staves, LayoutMode.Horizontal,
                InkPalette.Screen, cancellationToken),
            cancellationToken);

    /// <summary>
    /// Renders in PAGE layout (systems wrap to <paramref name="pageWidth"/> and
    /// break at barlines) instead of the single-row strip layout — used by the
    /// PDF/print exporter. Returns the full stacked score as positioned chunk
    /// rows; the exporter paginates them onto physical pages.
    /// </summary>
    /// <remarks>
    ///     <paramref name="palette" /> defaults to <see cref="InkPalette.Screen" />,
    ///     which is what the in-app page view wants; <c>SheetPdfExporter</c>
    ///     passes <see cref="InkPalette.Print" /> because a PDF page is white
    ///     paper and nothing fills a ground behind these transparent chunks.
    /// </remarks>
    public Task<RenderResult?> RenderPagedAsync(string tex, double pageWidth, double displayScale, int staves,
        CancellationToken cancellationToken, InkPalette palette = InkPalette.Screen) =>
        Task.Run(() => RenderInternal(tex, pageWidth, displayScale, staves, LayoutMode.Page,
                palette, cancellationToken),
            cancellationToken);

    /// <summary>
    ///     Delegates to the ONE process-wide gate. A private lock
    ///     of its own would serialize sheet renders against each other but not
    ///     against <c>GpExporter</c>'s unguarded construction — two separate gates
    ///     over one shared static is no gate at all, and the save companions build
    ///     detached so those two really do overlap.
    /// </summary>
    private static Settings CreateSettings() => AlphaTabRuntime.CreateSettings();

    /// <summary>
    ///     The two inks, and the ground each is meant to be read against.
    ///     Exposed so it can be asserted without AlphaSkia: the raster itself is
    ///     native and not reproducible headless, which is exactly why the PDF
    ///     shipped engraved in <c>#E8E8E8</c> on white paper.
    /// </summary>
    /// <returns>The main glyph/staff colour, and the dimmer one used for bar
    /// separators and bar numbers.</returns>
    internal static (Color Glyph, Color Dim) InkFor(InkPalette palette) => palette == InkPalette.Print
        ? (new Color(0, 0, 0), new Color(80, 80, 80))
        : (new Color(232, 232, 232), new Color(170, 170, 170));

    /// <summary>The ground <c>SheetDrawOp</c> fills behind an on-screen chunk.
    /// The chunks are transparent, so this is what the Screen ink is read
    /// against — and the PDF, which fills nothing, is why Print exists.</summary>
    internal static readonly Color ScreenGround = new(28, 28, 32);

    /// <summary>The ground a PDF page stands on: <c>SheetPdfExporter.WritePdf</c>
    /// never fills one, so it is the paper.</summary>
    internal static readonly Color PrintGround = new(255, 255, 255);

    private static RenderResult? RenderInternal(string tex, double width, double displayScale, int staves,
        LayoutMode layoutMode, InkPalette palette, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(tex) || width < 64) return null;

        EnsureAlphaSkiaLoaded();

        var settings = CreateSettings();
        settings.Core.Engine = "skia";
        settings.Core.EnableLazyLoading = false;
        settings.Core.IncludeNoteBounds = true;
        settings.Player.PlayerMode = PlayerMode.Disabled;
        settings.Display.LayoutMode = layoutMode;
#pragma warning disable CS0618 // AlphaTab has no equivalent profile shortcut; staff-level replacement is not exposed in 1.8.x.
        settings.Display.StaveProfile = staves switch
        {
            StavesNotation => StaveProfile.Score,
            StavesTab => StaveProfile.Tab,
            _ => StaveProfile.ScoreTab
        };
#pragma warning restore CS0618

        // Tab-only, Guitar-Pro style: rhythm stems below the tab staff (and
        // proper rest glyphs), so durations stay readable without the notation
        // staff. ShowWith BARS, not BEAMS: despite the name, ShowWithBeams
        // renders every beat with its own little flag — nothing ever connects —
        // while ShowWithBars beams eighth/sixteenth runs together like standard
        // notation (verified against the bundled renderer; the shipped "no
        // beams in tab mode" bug was exactly this enum value).
        // SheetTabBeamingRenderTests guards it structurally.
        if (staves == StavesTab)
            settings.Notation.RhythmMode = TabRhythmMode.ShowWithBars;
        settings.Display.Scale = Math.Clamp(displayScale, 0.8, 4.0);
        // Trim AlphaTab's default page padding — in a slim strip every dead
        // pixel comes straight out of the staff size after fit-to-height.
        settings.Display.Padding = new[] { 6.0, 6.0 };

        // Screen: light glyphs on the dark sheet backdrop drawn by SheetDrawOp.
        // Print: black on the white paper the PDF exporter writes onto — the
        // chunks are transparent, so whichever ground the caller paints behind
        // them is what the glyphs have to read against.
        var (fg, dim) = InkFor(palette);
        settings.Display.Resources.MainGlyphColor = fg;
        settings.Display.Resources.SecondaryGlyphColor = fg;
        settings.Display.Resources.StaffLineColor = fg;
        settings.Display.Resources.BarSeparatorColor = dim;
        settings.Display.Resources.BarNumberColor = dim;
        settings.Display.Resources.ScoreInfoColor = fg;

        // Force every textual element (fret numbers, bar numbers, tempo,
        // section markers, etc.) to use the same registered text font so
        // the FreeType backend always finds a glyph. Without an explicit
        // family that matches a registered font, AlphaSkia silently draws
        // nothing for text runs.
        ApplyTextFont(settings.Display.Resources, "Arial");

        // Enable measure numbers, tempo markers, and the common articulations
        // we map from the domain model.
        settings.Notation.Elements.Set(NotationElement.BarNumber, true);
        settings.Notation.Elements.Set(NotationElement.EffectChordNames, true);
        settings.Notation.Elements.Set(NotationElement.EffectTempo, true);
        settings.Notation.Elements.Set(NotationElement.EffectPalmMute, true);
        settings.Notation.Elements.Set(NotationElement.EffectHarmonics, true);
        settings.Notation.Elements.Set(NotationElement.EffectTap, true);
        settings.Notation.Elements.Set(NotationElement.EffectLetRing, true);
        settings.Notation.Elements.Set(NotationElement.EffectSlightNoteVibrato, true);
        settings.Notation.Elements.Set(NotationElement.EffectWideNoteVibrato, true);
        // The domain has no dynamics, but AlphaTab renders its default forte
        // as a stray "f" under the first beat of every score — engraving noise
        // Guitar Pro would only show when authored. Suppress.
        settings.Notation.Elements.Set(NotationElement.EffectDynamics, false);

        // Model construction AND rendering sit inside the one process-wide
        // alphaTab region (concurrency audit #8). Only the settings went through
        // the gate before: ReadScore allocates Bar/Beat ids from the same static
        // counter GpExporter's Score.ResetIds() rewinds inside ITS region, and
        // the save companions build detached — so a .gp export really does
        // overlap a sheet render, and the id→rect maps below then collide.
        return AlphaTabRuntime.RunExclusive<RenderResult?>(() =>
        {
            var importer = new AlphaTexImporter();
            importer.InitFromString(tex, settings);
            var score = importer.ReadScore();

            var renderer = new ScoreRenderer(settings) { Width = (float)width };

            var chunks = new List<SheetChunk>();
            var totalWidth = 0.0;
            var totalHeight = 0.0;
            var done = false;
            Exception? error = null;

            renderer.PartialRenderFinished.On(args =>
            {
                if (args.RenderResult is AlphaTab.Platform.Skia.AlphaSkiaBridge.AlphaSkiaImage bridge && bridge.Image is { } img)
                {
                    var skImage = ConvertToSkImage(img);
                    if (skImage != null)
                        chunks.Add(new SheetChunk(args.X, args.Y, args.Width, args.Height, skImage));
                }
            });
            renderer.RenderFinished.On(args =>
            {
                totalWidth = args.TotalWidth;
                totalHeight = args.TotalHeight;
                done = true;
            });
            renderer.Error.On(e =>
            {
                error = e;
                done = true;
            });

            renderer.RenderScore(score, new double[] { 0 }, new DefaultRenderHints());

            if (error != null)
            {
                DisposeChunks(chunks);
                throw error;
            }
            if (!done || chunks.Count == 0)
            {
                DisposeChunks(chunks);
                return null;
            }

            ct.ThrowIfCancellationRequested();

            var w = (int)Math.Ceiling(totalWidth);
            var h = (int)Math.Ceiling(totalHeight);
            if (w <= 0 || h <= 0)
            {
                DisposeChunks(chunks);
                return null;
            }

            var measureBounds = ExtractMeasureBounds(renderer);
            var (beatBounds, stringLattice) = ExtractBeatBounds(renderer);
            var systemLattices = ExtractSystemLattices(renderer);
            var masterBarCount = score.MasterBars.Count;
            var diag =
                $"score.MasterBars={masterBarCount} chunks={chunks.Count} " +
                $"bounds={measureBounds.Count} totalWidth={totalWidth:F0} totalHeight={totalHeight:F0}";
            // No %TEMP% side-file: appending a line to
            // dotcharter-sheet-debug.log on every render would grow unconditionally and forever,
            // with nothing to read, truncate or clean it up. Sheet
            // render problems belong in the app log and the diagnostics bundle.
            Debug.WriteLine("[SheetMusicRenderer] " + diag);

            return new RenderResult(chunks, w, h, measureBounds, beatBounds, stringLattice, systemLattices);
        });
    }

    /// <summary>
    /// Per-SYSTEM tab string-line lattice (page layout wraps bars into several
    /// stacked systems, so string Y is NOT constant across the score the way it
    /// is in the single-row strip). Each entry carries the system's Y band and
    /// the median tab-line Y per DOMAIN string within it. Used by the sheet view
    /// to resolve a page-mode click's Y to a string (interval pick / insert) in
    /// the system the click landed in. The strip's single <see cref="StringLine"/>
    /// lattice stays the source of truth for horizontal layout.
    /// </summary>
    private static IReadOnlyList<SystemStringLattice> ExtractSystemLattices(ScoreRenderer renderer)
    {
        var lookup = renderer.BoundsLookup;
        if (lookup == null) return Array.Empty<SystemStringLattice>();

        var systems = new List<SystemStringLattice>(lookup.StaffSystems.Count);
        for (var s = 0; s < lookup.StaffSystems.Count; s++)
        {
            var sys = lookup.StaffSystems[s];
            var ySamples = new Dictionary<int, List<double>>();
            var top = double.MaxValue;
            var bottom = double.MinValue;

            for (var b = 0; b < sys.Bars.Count; b++)
            {
                var bar = sys.Bars[b];
                var rb = bar.RealBounds;
                if (rb.Y < top) top = rb.Y;
                if (rb.Y + rb.H > bottom) bottom = rb.Y + rb.H;
                if (bar.Bars.Count == 0) continue;

                var firstStaff = bar.Bars[0];
                var bottomStaff = bar.Bars[bar.Bars.Count - 1];
                if (!(bar.Bars.Count > 1 || bottomStaff == firstStaff)) continue;

                for (var i = 0; i < bottomStaff.Beats.Count; i++)
                {
                    var beat = bottomStaff.Beats[i];
                    if (beat.Notes == null) continue;
                    for (var n = 0; n < beat.Notes.Count; n++)
                    {
                        var nb = beat.Notes[n];
                        var domainString = (int)nb.Note.String - 1;
                        if (domainString < 0) continue;
                        if (!ySamples.TryGetValue(domainString, out var list))
                            ySamples[domainString] = list = [];
                        list.Add(nb.NoteHeadBounds.Y);
                    }
                }
            }

            if (top > bottom) continue;
            var lines = new List<StringLine>(ySamples.Count);
            foreach (var (stringIndex, list) in ySamples)
            {
                list.Sort();
                lines.Add(new StringLine(stringIndex, list[list.Count / 2]));
            }

            lines.Sort(static (a, b) => a.StringIndex.CompareTo(b.StringIndex));
            systems.Add(new SystemStringLattice(top, bottom, lines));
        }

        return systems;
    }

    /// <summary>
    /// Extracts per-beat bounds (bar index + beat index within the bar + X span)
    /// and the TAB staff's string-line Y lattice from AlphaTab's BoundsLookup.
    /// The beat index within a bar matches the exporter's emission order
    /// FOR VOICE 0, so <c>(BarIndex, BeatIndex)</c> indexes straight into
    /// <see cref="AlphaTexExporter.ExportResult.BeatMap"/> — that is what turns
    /// a click on a glyph into the exact domain note. Sidecar notation voices
    /// (multi-voice imports) and grace beats are filtered OUT: the beat map
    /// records neither, and mixing their bounds in would break the per-bar
    /// index zip (clicks would land on the wrong domain sound). The lattice
    /// maps a click's Y to a DOMAIN string index (AlphaTab's model numbers
    /// strings 1 = lowest, so <c>Note.String − 1</c> IS the domain index).
    /// </summary>
    private static (IReadOnlyList<BeatBound> Bounds, IReadOnlyList<StringLine> Lattice)
        ExtractBeatBounds(ScoreRenderer renderer)
    {
        var lookup = renderer.BoundsLookup;
        if (lookup == null) return (Array.Empty<BeatBound>(), Array.Empty<StringLine>());

        // Same dedupe as ExtractMeasureBounds: partial render passes append
        // stale bounds; the final position per master bar is the rightmost.
        var byIndex = new Dictionary<int, AlphaTab.Rendering.Utils.MasterBarBounds>(256);
        for (var s = 0; s < lookup.StaffSystems.Count; s++)
        {
            var sys = lookup.StaffSystems[s];
            for (var b = 0; b < sys.Bars.Count; b++)
            {
                var bar = sys.Bars[b];
                var index = (int)bar.Index;
                if (!byIndex.TryGetValue(index, out var existing) ||
                    bar.RealBounds.X > existing.RealBounds.X ||
                    (Math.Abs(bar.RealBounds.X - existing.RealBounds.X) < 0.01 &&
                     bar.RealBounds.W > existing.RealBounds.W))
                {
                    byIndex[index] = bar;
                }
            }
        }

        var beatBounds = new List<BeatBound>(byIndex.Count * 8);
        // Per-string Y samples (domain index) from note heads on the BOTTOM
        // staff (the tab staff in ScoreTab/Tab profiles — string lines have a
        // constant Y there, unlike notation heads whose Y follows pitch).
        var ySamples = new Dictionary<int, List<double>>();

        foreach (var (barIndex, bar) in byIndex.OrderBy(kv => kv.Key))
        {
            if (bar.Bars.Count == 0) continue;
            var firstStaff = bar.Bars[0];
            var bottomStaff = bar.Bars[bar.Bars.Count - 1];

            for (var i = 0; i < firstStaff.Beats.Count; i++)
            {
                var beat = firstStaff.Beats[i];
                if ((int)beat.Beat.Voice.Index != 0) continue;          // sidecar voices: not click targets
                if (beat.Beat.GraceType != GraceType.None) continue;    // grace beats: not in the beat map
                var rb = beat.RealBounds;
                beatBounds.Add(new BeatBound(barIndex, (int)beat.Beat.Index, rb.X, rb.W));
            }

            if (bar.Bars.Count > 1 || bottomStaff == firstStaff)
            {
                for (var i = 0; i < bottomStaff.Beats.Count; i++)
                {
                    var beat = bottomStaff.Beats[i];
                    if (beat.Notes == null) continue;
                    for (var n = 0; n < beat.Notes.Count; n++)
                    {
                        var nb = beat.Notes[n];
                        var domainString = (int)nb.Note.String - 1;
                        if (domainString < 0) continue;
                        if (!ySamples.TryGetValue(domainString, out var list))
                            ySamples[domainString] = list = [];
                        // NoteHeadBounds.Y already sits ON the tab string line
                        // (verified live: adding H/2 shifted the lattice half a
                        // line down and clicks resolved to the string above).
                        list.Add(nb.NoteHeadBounds.Y);
                    }
                }
            }
        }

        beatBounds.Sort(static (a, b) =>
            a.BarIndex != b.BarIndex ? a.BarIndex.CompareTo(b.BarIndex) : a.BeatIndex.CompareTo(b.BeatIndex));

        // Median Y per string; the horizontal strip is ONE system, so string
        // lines are constant across the whole sheet.
        var lattice = new List<StringLine>(ySamples.Count);
        foreach (var (stringIndex, list) in ySamples)
        {
            list.Sort();
            lattice.Add(new StringLine(stringIndex, list[list.Count / 2]));
        }
        lattice.Sort(static (a, b) => a.StringIndex.CompareTo(b.StringIndex));

        return (beatBounds, lattice);
    }

    private static IReadOnlyList<MeasureBound> ExtractMeasureBounds(ScoreRenderer renderer)
    {
        var lookup = renderer.BoundsLookup;
        if (lookup == null)
        {
            Debug.WriteLine("[SheetMusicRenderer] BoundsLookup is null — IncludeNoteBounds may not be honored.");
            return Array.Empty<MeasureBound>();
        }

        // AlphaTab adds bounds entries per partial-render pass / per voice.
        // For each MasterBar.Index we therefore see multiple MasterBarBounds,
        // some of which carry stale "row origin" coordinates from earlier
        // layout passes. The final, correct on-screen position is always the
        // entry with the largest X for that index, so we dedupe by Index and
        // keep the rightmost (and widest, when X ties) bound.
        var byIndex = new Dictionary<int, MeasureBound>(256);
        for (var s = 0; s < lookup.StaffSystems.Count; s++)
        {
            var sys = lookup.StaffSystems[s];
            for (var b = 0; b < sys.Bars.Count; b++)
            {
                var bar = sys.Bars[b];
                var rb = bar.RealBounds;
                var index = (int)bar.Index;
                var entry = new MeasureBound(index, rb.X, rb.Y, rb.W, rb.H);
                if (!byIndex.TryGetValue(index, out var existing))
                {
                    byIndex[index] = entry;
                }
                // ReSharper disable once CompareOfFloatsByEqualityOperator -- intentional tie-breaker: keep widest bound at same X
                else if (rb.X > existing.X || (rb.X == existing.X && rb.W > existing.Width))
                {
                    byIndex[index] = entry;
                }
            }
        }

        var list = new List<MeasureBound>(byIndex.Count);
        list.AddRange(byIndex.Values);
        list.Sort(static (a, b) => a.Index.CompareTo(b.Index));

        // Repair any zero-width entries by stretching them to the start of
        // the next bar — keeps the cursor mapping continuous even if a bar
        // ended up with a stub bound for some reason.
        for (var i = 0; i < list.Count - 1; i++)
        {
            var cur = list[i];
            var next = list[i + 1];
            if (cur.Width <= 1 && next.X > cur.X)
                list[i] = new MeasureBound(cur.Index, cur.X, cur.Y, next.X - cur.X, cur.Height);
        }

        Debug.WriteLine(
            $"[SheetMusicRenderer] Captured {list.Count} measure bounds (lookup.IsFinished={lookup.IsFinished}).");
        return list;
    }

    private static SKImage? ConvertToSkImage(AlphaSkiaImage img)
    {
        var w = img.Width;
        var h = img.Height;
        if (w <= 0 || h <= 0) return null;

        var bytes = img.ReadPixels();
        if (bytes == null || bytes.Length < w * h * 4) return null;

        // AlphaSkia exposes pixels as native 32bpp BGRA (premultiplied) — same
        // memory layout SkiaSharp expects for SKColorType.Bgra8888 + Premul.
        var info = new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
        return SKImage.FromPixelCopy(info, bytes);
    }

    private static void DisposeChunks(List<SheetChunk> chunks)
    {
        for (var i = 0; i < chunks.Count; i++) chunks[i].Image.Dispose();
        chunks.Clear();
    }

    private static void ApplyTextFont(RenderingResources r, string family)
    {
        // Bulk-rewrite the family list of every Font property so the
        // FreeType backend resolves them all to the single TTF we
        // registered via RegisterAlphaSkiaCustomFont.
        var props = typeof(RenderingResources).GetProperties();
        for (var i = 0; i < props.Length; i++)
        {
            var p = props[i];
            if (p.PropertyType != typeof(Font)) continue;
            if (p.GetValue(r) is not Font f) continue;
            f.Families = [family];
        }
    }

    private static void EnsureAlphaSkiaLoaded()
    {
        lock (AlphaSkiaGate)
        {
            if (_alphaSkiaReady) return;
            AlphaSkiaPlatform.LoadLibrary();
            AlphaSkiaCanvas.SwitchToFreeTypeFonts();
            _bravuraFont ??= LoadEmbeddedBravura();
            Environment.EnableAlphaSkia(
                new ArrayBuffer(_bravuraFont),
                null);

            // FreeType doesn't enumerate system fonts on its own. Register
            // a minimal Arial set so text glyphs (fret numbers, bar numbers,
            // tempo markings) actually rasterize.
            RegisterArialFonts();
            _alphaSkiaReady = true; // only once EVERYTHING above has run
        }
    }

    private static void RegisterArialFonts()
    {
        TryRegister("arial.ttf");
        TryRegister("arialbd.ttf");
        TryRegister("ariali.ttf");
        TryRegister("arialbi.ttf");
    }

    private static void TryRegister(string fileName)
    {
        try
        {
            var path = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.Fonts), fileName);
            if (!File.Exists(path)) return;
            var data = File.ReadAllBytes(path);
            Environment.RegisterAlphaSkiaCustomFont(new Uint8Array(data));
        }
        catch
        {
            // Font is optional — missing weights just fall back to Regular.
        }
    }

    private static byte[] LoadEmbeddedBravura()
    {
        var asm = typeof(Environment).Assembly;
        using var s = asm.GetManifestResourceStream("AlphaTab.Platform.Skia.Bravura.otf")
                      ?? throw new InvalidOperationException("AlphaTab.Platform.Skia.Bravura.otf resource not found.");
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    public sealed record SheetChunk(double X, double Y, double Width, double Height, SKImage Image);

    public readonly record struct MeasureBound(int Index, double X, double Y, double Width, double Height);

    /// <summary>X span of one rendered beat, in source pixels. <c>BeatIndex</c> is
    /// the beat's index within its bar's voice — 1:1 with the exporter's
    /// per-measure emission order.</summary>
    public readonly record struct BeatBound(int BarIndex, int BeatIndex, double X, double Width);

    /// <summary>Y center (source pixels) of one tab-staff string line, keyed by
    /// DOMAIN string index (0 = lowest pitch).</summary>
    public readonly record struct StringLine(int StringIndex, double Y);

    /// <summary>One rendered system (row) in page layout: its source-pixel Y band
    /// and the tab string lines within it. In the horizontal strip there is
    /// exactly one of these.</summary>
    public sealed record SystemStringLattice(double Top, double Bottom, IReadOnlyList<StringLine> Lines);

    public sealed class RenderResult : IDisposable
    {
        private int _refCount = 1;
        private int _disposed;

        public RenderResult(IReadOnlyList<SheetChunk> chunks, int width, int height,
            IReadOnlyList<MeasureBound> measureBounds,
            IReadOnlyList<BeatBound>? beatBounds = null,
            IReadOnlyList<StringLine>? stringLattice = null,
            IReadOnlyList<SystemStringLattice>? systemLattices = null)
        {
            Chunks = chunks;
            Width = width;
            Height = height;
            MeasureBounds = measureBounds;
            BeatBounds = beatBounds ?? Array.Empty<BeatBound>();
            StringLattice = stringLattice ?? Array.Empty<StringLine>();
            SystemLattices = systemLattices ?? Array.Empty<SystemStringLattice>();
        }

        public IReadOnlyList<SheetChunk> Chunks { get; }
        public IReadOnlyList<MeasureBound> MeasureBounds { get; }
        public IReadOnlyList<BeatBound> BeatBounds { get; }
        public IReadOnlyList<StringLine> StringLattice { get; }
        public IReadOnlyList<SystemStringLattice> SystemLattices { get; }
        public int Width { get; }
        public int Height { get; }

        public RenderResult Retain()
        {
            if (Interlocked.Increment(ref _refCount) <= 1)
                throw new ObjectDisposedException(nameof(RenderResult));

            return this;
        }

        public void Dispose() => Release();

        public void Release()
        {
            if (Interlocked.Decrement(ref _refCount) != 0)
                return;

            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            for (var i = 0; i < Chunks.Count; i++) Chunks[i].Image.Dispose();
        }
    }

    private sealed class DefaultRenderHints : RenderHints
    {
        public bool? ReuseViewport { get; set; } = true;
        public double? FirstChangedMasterBar { get; set; }
    }
}
