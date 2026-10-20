using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using DotCharter.Tone.UI.Controls.Dsp;

namespace DotCharter.Tone.UI.Controls.Fire;

/// <summary>
///     One of the rig's illustrations (<see cref="RigArtwork" />), drawn
///     "contain"-fitted with rounded corners. Decorative only: it is never a
///     hit target and never supplies a control.
///
///     <para>
///         Set <see cref="ModuleTypeId" /> for a module's own picture, or
///         <see cref="File" /> for a named asset (a shell, a grille, a
///         texture). Bitmaps are decoded once per process and shared by every
///         tile, row and editor that shows them — the board re-inflates its
///         tiles on chain changes, and 57 decodes per add would be the wrong
///         place to pay for a picture.
///     </para>
/// </summary>
public sealed class RigArtworkImage : Control
{
    public static readonly StyledProperty<string?> ModuleTypeIdProperty =
        AvaloniaProperty.Register<RigArtworkImage, string?>(nameof(ModuleTypeId));

    public static readonly StyledProperty<string?> FileProperty =
        AvaloniaProperty.Register<RigArtworkImage, string?>(nameof(File));

    public static readonly StyledProperty<double> CornerRadiusProperty =
        AvaloniaProperty.Register<RigArtworkImage, double>(nameof(CornerRadius), 4);

    /// <summary>Stretch to fill instead of contain — for shells and textures
    /// that are meant to cover their panel.</summary>
    public static readonly StyledProperty<bool> FillProperty =
        AvaloniaProperty.Register<RigArtworkImage, bool>(nameof(Fill));

    public string? ModuleTypeId { get => GetValue(ModuleTypeIdProperty); set => SetValue(ModuleTypeIdProperty, value); }
    public string? File { get => GetValue(FileProperty); set => SetValue(FileProperty, value); }
    public double CornerRadius { get => GetValue(CornerRadiusProperty); set => SetValue(CornerRadiusProperty, value); }
    public bool Fill { get => GetValue(FillProperty); set => SetValue(FillProperty, value); }

    static RigArtworkImage()
    {
        IsHitTestVisibleProperty.OverrideDefaultValue<RigArtworkImage>(false);
        AffectsRender<RigArtworkImage>(ModuleTypeIdProperty, FileProperty, CornerRadiusProperty, FillProperty,
            FireUnitState.IsBypassedProperty);
    }

    /// <summary>The asset this control resolves to, for tests and tooltips.</summary>
    public string ResolvedFile => File is { Length: > 0 } f
        ? f
        : RigArtwork.IllustrationFor(ModuleTypeId ?? string.Empty);

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Bounds.Width < 1 || Bounds.Height < 1) return;
        var bitmap = RigArtworkStore.GetForRender(ResolvedFile, this);
        if (bitmap is null) return;

        var bounds = new Rect(Bounds.Size);
        Rect dest;
        if (Fill)
        {
            dest = bounds;
        }
        else
        {
            var scale = Math.Min(bounds.Width / bitmap.Size.Width, bounds.Height / bitmap.Size.Height);
            var size = new Size(bitmap.Size.Width * scale, bitmap.Size.Height * scale);
            dest = new Rect(bounds.Center.X - size.Width / 2, bounds.Center.Y - size.Height / 2, size.Width, size.Height);
        }

        var opacity = FireUnitState.IsInactive(this) ? FireUnitState.BypassedOpacity : 1.0;
        using var _ = context.PushOpacity(opacity);
        using var clip = context.PushClip(new RoundedRect(dest, CornerRadius));
        context.DrawImage(bitmap, new Rect(bitmap.Size), dest);
    }
}

/// <summary>Process-wide decoded-bitmap cache for the rig's artwork.</summary>
/// <remarks>
///     <para>
///         <b>A render of a HIDDEN control never decodes on the UI thread</b>
///         (<see cref="GetForRender(string, int, Visual)" />). The compositor records
///         <c>Render</c> for every control in the tree, including the ones under an
///         <c>IsVisible=false</c> overlay that the post-first-paint prewarm has just
///         realized, and a WebP decode is 10-20 ms: the rig's gear list alone asked
///         for 13 of them in one frame, ~190 ms of the 315 ms first open of the Amp
/// &amp; FX rig. A hidden request queues the decode on the thread
///         pool and draws nothing; the decode then invalidates the requester, so
///         the drawing is current by the time anything shows it. A VISIBLE render
///         still decodes synchronously, as it always has: what the user can see is
///         never drawn without its picture.
///     </para>
/// </remarks>
public static class RigArtworkStore
{
    private static readonly Dictionary<string, Bitmap?> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, List<Visual>> Pending = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Gate = new();

    /// <summary>Two decodes at a time: the queue is a prewarm's, never a race.</summary>
    private static readonly SemaphoreSlim DecodeSlots = new(2);

    /// <summary>The decoded asset, or null when the file is not shipped (a
    /// missing picture is a blank, never a crash on the render thread).
    /// <paramref name="file" /> is a name under <see cref="RigArtwork.AssetRoot" />
    /// or a full <c>avares://</c> URI (a faceplate, a knob cap).</summary>
    public static Bitmap? Get(string file) => Get(file, 0);

    /// <summary>The asset decoded at <paramref name="decodeWidth" /> pixels wide (0 =
    /// full size). A board tile wants a 160 px faceplate, not a 560 px one: ten
    /// full-size faces on a chain would hold ~20 MB of pixels to draw 44 px tiles.</summary>
    public static Bitmap? Get(string file, int decodeWidth)
    {
        var key = KeyOf(file, decodeWidth);
        lock (Gate)
        {
            if (Cache.TryGetValue(key, out var cached)) return cached;
        }

        // Decoded OUTSIDE the lock, so a pool decode never holds up a UI-thread
        // lookup of another asset; the first result published wins.
        return Publish(key, Decode(file, decodeWidth));
    }

    /// <inheritdoc cref="GetForRender(string, int, Visual)" />
    public static Bitmap? GetForRender(string file, Visual requester) => GetForRender(file, 0, requester);

    /// <summary>
    ///     <see cref="Get(string, int)" /> for a <c>Render</c> override: when
    ///     <paramref name="requester" /> is not effectively visible and the asset is
    ///     not decoded yet, queues the decode on the thread pool and returns null
    ///     (draw nothing, or the vector stand-in); the requester is invalidated once
    ///     the bitmap is in.
    /// </summary>
    public static Bitmap? GetForRender(string file, int decodeWidth, Visual requester)
    {
        if (requester.IsEffectivelyVisible) return Get(file, decodeWidth);

        var key = KeyOf(file, decodeWidth);
        lock (Gate)
        {
            if (Cache.TryGetValue(key, out var cached)) return cached;
            if (Pending.TryGetValue(key, out var waiters))
            {
                if (!waiters.Contains(requester)) waiters.Add(requester);
                return null;
            }

            Pending[key] = [requester];
        }

        _ = Task.Run(async () =>
        {
            await DecodeSlots.WaitAsync().ConfigureAwait(false);
            try
            {
                Publish(key, Decode(file, decodeWidth));
            }
            finally
            {
                DecodeSlots.Release();
            }
        });
        return null;
    }

    /// <summary>Test seam: whether a hidden render's decode of this asset is still
    /// in flight.</summary>
    public static bool IsPending(string file, int decodeWidth)
    {
        lock (Gate) return Pending.ContainsKey(KeyOf(file, decodeWidth));
    }

    /// <summary>Test seam: whether this asset has been decoded (or found missing).</summary>
    public static bool IsCached(string file, int decodeWidth)
    {
        lock (Gate) return Cache.ContainsKey(KeyOf(file, decodeWidth));
    }

    /// <summary>Test seam: how many assets have been decoded so far.</summary>
    public static int DecodedCount
    {
        get { lock (Gate) return Cache.Count(kv => kv.Value != null); }
    }

    private static string KeyOf(string file, int decodeWidth) => decodeWidth > 0 ? file + "@" + decodeWidth : file;

    private static Bitmap? Decode(string file, int decodeWidth)
    {
        try
        {
            var uri = new Uri(file.StartsWith("avares://", StringComparison.Ordinal) ? file : RigArtwork.AssetRoot + file);
            if (!AssetLoader.Exists(uri)) return null;
            using var stream = AssetLoader.Open(uri);
            return decodeWidth > 0
                ? Bitmap.DecodeToWidth(stream, decodeWidth, BitmapInterpolationMode.HighQuality)
                : new Bitmap(stream);
        }
        catch (Exception)
        {
            return null; // an undecodable asset draws nothing; the lint test names it
        }
    }

    /// <summary>Stores a decode unless another one got there first, and hands every
    /// hidden render that asked for it a repaint.</summary>
    private static Bitmap? Publish(string key, Bitmap? bitmap)
    {
        Bitmap? winner;
        List<Visual>? waiters;
        lock (Gate)
        {
            if (Cache.TryGetValue(key, out winner))
            {
                if (!ReferenceEquals(winner, bitmap)) bitmap?.Dispose();
            }
            else
            {
                Cache[key] = winner = bitmap;
            }

            Pending.Remove(key, out waiters);
        }

        if (waiters is { Count: > 0 })
        {
            Dispatcher.UIThread.Post(() =>
            {
                foreach (var visual in waiters) visual.InvalidateVisual();
            }, DispatcherPriority.Background);
        }

        return winner;
    }
}