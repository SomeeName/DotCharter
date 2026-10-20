using System.Numerics;
using Avalonia.Media;
using DotCharter.Rendering.Data;
using DotCharter.Rendering.Data.Render;
using DotCharter.Rendering.Scene;
using DotCharter.Rendering.Vulkan;

namespace DotCharter.Rendering.Components;

public class HudComponent : IRenderable
{
    private string? _cachedSectionInput;
    private string? _cachedSectionOutput;
    private string? _cachedPhraseInput;
    private string? _cachedPhraseOutput;

    public void Initialize(IVulkanRenderService vulkanRenderer, RenderResources resources)
    {
    }

    public void Render(RenderContext context)
    {
        // Render stats live in the bottom-left corner as a single dim line —
        // present when you look for it, invisible when you don't.
        const float statsScale = 0.16f;
        var statsY = context.ScreenHeight - 16f - 64f * statsScale;
        context.TextBatcher.DrawText2D(context.Snapshot.DebugText, new Vector2(14, statsY),
            Color.FromArgb(120, 158, 168, 182), statsScale);

        RenderEventsAndPhrases(context);
        RenderLyrics(context);
        RenderTimingDirection(context);
        RenderTimingTier(context);
    }

    /// <summary>
    ///     The timing flashes were tuned as fixed PIXEL scales on a ~1080-row
    ///     frame, so on a 4K / 200 % display (where the preview renders at the
    ///     physical pixel size) they came out half as tall as intended, and in a
    ///     small docked preview they covered the highway. Scale them with the
    ///     frame height instead — the section, phrase, tone and lyric lines
    ///     already do — clamped so neither extreme becomes unreadable.
    /// </summary>
    internal static float HeightRelativeScale(float designScaleAt1080, int screenHeight) =>
        designScaleAt1080 * Math.Clamp(screenHeight / 1080f, 0.5f, 3f);

    /// <summary>How long the EARLY/LATE flash stays up after the hit.</summary>
    private const double TimingFlashMs = 600.0;

    private const byte DirectionEarly = 1;
    private const byte DirectionLate = 3;

    /// <summary>
    ///     Flashes EARLY / LATE over the highway after a hit that landed outside
    ///     the on-time band.
    ///
    ///     <para>It is drawn HERE, by the renderer, because the native Vulkan
    ///     surface renders above all Avalonia content (airspace) — the score/streak
    ///     HUD lives in a thin Avalonia bar at the TOP of the window, which is
    ///     nowhere near where a player is looking mid-run. A timing cue you have to
    ///     look away from the fretboard to read is a cue nobody reads.</para>
    ///
    ///     <para>Direction is None for misses and CQT pull-verified hits — they have
    ///     no honest attack time — so nothing is drawn for them (see
    ///     LiveTiming.Classify). None is "no answer", not "on time".</para>
    /// </summary>
    private static void RenderTimingDirection(RenderContext context)
    {
        var snap = context.Snapshot;
        if (!snap.LivePlayActive) return;

        var direction = snap.LastTimingDirection;
        if (direction is not (DirectionEarly or DirectionLate)) return;

        // Song time, so the flash freezes with the transport when paused — the same
        // convention the other time-driven effects follow.
        var age = context.TimeMs - snap.LastTimingAtMs;
        if (age is < 0 or > TimingFlashMs) return;

        var fade = 1f - (float)(age / TimingFlashMs);
        var alpha = (byte)Math.Clamp(255f * fade * fade, 0f, 255f);

        var early = direction == DirectionEarly;
        var text = early ? "EARLY" : "LATE";

        // Amber for early (you're ahead), a cooler red-orange for late — distinct at
        // a glance without stealing the green/red the hit/miss markers already own.
        var color = early
            ? Color.FromArgb(alpha, 255, 196, 64)
            : Color.FromArgb(alpha, 255, 122, 64);

        // Just above the strike zone at the bottom of the highway: in the player's
        // gaze, but clear of the note lanes themselves.
        var scale = HeightRelativeScale(0.42f, context.ScreenHeight);
        var y = context.ScreenHeight * 0.74f;
        var x = context.ScreenWidth * 0.5f - text.Length * 16f * scale;

        context.TextBatcher.DrawText2D(text, new Vector2(x, y), color, scale);
    }

    // LiveTimingTier ordinals (the enum lives in Services, which this project
    // does not reference — the driver hands them over as raw bytes). Good (1) is
    // the switch default below; None (0) never reaches the draw.
    private const byte TierGreat = 2;
    private const byte TierPerfect = 3;

    /// <summary>
    ///     Flashes PERFECT / GREAT / GOOD over the highway at the instant a hit is
    ///     judged — the timing grade that until now only surfaced post-hoc on the
    ///     results screen, made visible where the player is actually looking.
    ///
    ///     <para>Drawn ONE lane above the EARLY/LATE cue so a hit that is both (a
    ///     rushed Great, say) shows its quality and its side at once without
    ///     overlap.</para>
    ///
    ///     <para>Tier None never reaches the draw: misses and CQT pull-verified
    ///     hits confirm WHAT was played, not WHEN, so they carry no honest tier and
    ///     the driver holds the snapshot byte at 0 for them (see TimingFlashMath and
    ///     LiveTiming.Classify). Flashing a tier there would be a fabricated claim
    ///     about the player's timing.</para>
    /// </summary>
    private static void RenderTimingTier(RenderContext context)
    {
        var snap = context.Snapshot;
        if (!snap.LivePlayActive) return;

        var tier = snap.LastTimingTier;

        // Song time, so the flash freezes with the transport when paused — the same
        // convention RenderTimingDirection and the other time-driven effects follow.
        var age = context.TimeMs - snap.LastTimingTierAtMs;
        if (!TimingFlashMath.ShouldFlash(tier, age)) return;

        var fade = 1f - (float)(age / TimingFlashMath.FlashDurationMs);
        var alpha = (byte)Math.Clamp(255f * fade * fade, 0f, 255f);

        // A quality ladder you read at a glance without parsing the word: vivid cyan
        // for Perfect, cooler periwinkle for Great, a dull cool grey for Good
        // (vivid → dull tracks best → worst). Deliberately clear of the amber/orange
        // the EARLY/LATE cue owns and the green/red of the hit/miss markers. Perfect
        // also runs a touch larger for punch.
        var (text, color, scale) = tier switch
        {
            TierPerfect => ("PERFECT", Color.FromArgb(alpha, 64, 232, 255), 0.46f),
            TierGreat => ("GREAT", Color.FromArgb(alpha, 132, 148, 255), 0.42f),
            _ => ("GOOD", Color.FromArgb(alpha, 176, 190, 205), 0.40f) // Good (1)
        };

        // One lane above the EARLY/LATE flash (which sits at 0.74·H, scale 0.42):
        // this line's glyph box is 64·scale ≈ 30 px tall, so 0.66·H clears it with
        // room to spare at any gameplay resolution.
        scale = HeightRelativeScale(scale, context.ScreenHeight);
        var y = context.ScreenHeight * 0.66f;
        var x = context.ScreenWidth * 0.5f - text.Length * 16f * scale;

        context.TextBatcher.DrawText2D(text, new Vector2(x, y), color, scale);
    }

    private string FormatTitleCase(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        return char.ToUpper(text[0]) + text.Substring(1).ToLower();
    }

    private string FormatTitleCaseSection(string text)
    {
        if (!ReferenceEquals(_cachedSectionInput, text) && _cachedSectionInput != text)
        {
            _cachedSectionInput = text;
            _cachedSectionOutput = FormatTitleCase(text);
        }
        return _cachedSectionOutput ?? text;
    }

    private string FormatTitleCasePhrase(string text)
    {
        if (!ReferenceEquals(_cachedPhraseInput, text) && _cachedPhraseInput != text)
        {
            _cachedPhraseInput = text;
            _cachedPhraseOutput = FormatTitleCase(text);
        }
        return _cachedPhraseOutput ?? text;
    }

    private void RenderEventsAndPhrases(RenderContext context)
    {
        // X = 20% of the screen width from the left edge
        var startX = context.ScreenWidth * 0.17f;

        // Scaled down sizes to be less overwhelming
        // Section: ~3.5% of screen height, Phrase: ~2.5% of screen height
        var sectionScale = context.ScreenHeight * 0.045f / 64f;
        var phraseScale = context.ScreenHeight * 0.035f / 64f;

        // Adjusted Y positions for top-down rendering, below the editor's
        // show-light chip when it is up.
        var top = ChipInset(context);
        var sectionY = top + context.ScreenHeight * 0.01f;
        var phraseY = top + context.ScreenHeight * 0.07f;

        // The section and the phrase are resolved INDEPENDENTLY (HudEventBadges,
        // pure and unit-tested): wrapping both draws in
        // `if (phrase is not null && phrase != "END")` would make a chart with sections
        // and no phrase events show no section at all, and every chart would lose its
        // section badge the moment the terminating END phrase went by — while
        // the 2D timeline's pinned badges keep drawing it.
        var (section, phrase) = HudEventBadges.Resolve(
            context.Snapshot.EventPoints, context.Snapshot.EventPointCount, context.TimeMs);

        if (!string.IsNullOrEmpty(section))
        {
            var sectionColor = HudEventBadges.SectionColor;
            context.TextBatcher.DrawText2D(FormatTitleCaseSection(section), new Vector2(startX, sectionY),
                sectionColor, sectionScale);
        }

        if (!string.IsNullOrEmpty(phrase))
        {
            var phraseColor = HudEventBadges.PhraseColor;
            context.TextBatcher.DrawText2D(FormatTitleCasePhrase(phrase), new Vector2(startX, phraseY),
                phraseColor, phraseScale);
        }

        RenderActiveTone(context, startX);
    }

    /// <summary>The editor's show-light chip owns the top-left corner; the
    /// section/phrase/tone/lyric block moves down by its height while it shows.</summary>
    private static float ChipInset(RenderContext context) =>
        string.IsNullOrEmpty(context.Snapshot.ShowlightChipText)
            ? 0f
            : ShowlightChipLayout.ReservedTop(context.ScreenHeight);

    // Duration of the on-switch emphasis flash (ms).
    private const float ToneFlashMs = 900f;

    // Draws the single active tone name resolved by the snapshot
    // (rig-applied preset when in-game tones drive, else the chart's own tone)
    // and briefly emphasises it — scale bump + brighten toward white — right
    // after a switch so the change is unmistakable. Also the live proof that
    // in-game-tone switching is firing.
    private void RenderActiveTone(RenderContext context, float startX)
    {
        var toneText = context.Snapshot.ActiveToneName;
        if (string.IsNullOrEmpty(toneText)) return;

        // 1 → 0 ease over ToneFlashMs since the last change.
        var elapsed = Environment.TickCount64 - context.Snapshot.ActiveToneChangedTick;
        var flash = elapsed is >= 0 and < (long)ToneFlashMs ? 1f - elapsed / ToneFlashMs : 0f;

        // Soft cyan resting colour (88,165,222) → white on flash.
        byte Mix(byte rest, byte hot) => (byte)(rest + (hot - rest) * flash);
        // Approximated tones sit a touch dimmer so a 1:1 tone reads as "clean".
        var alpha = context.Snapshot.ActiveToneApproximated ? (byte)205 : (byte)255;
        var toneColor = Color.FromArgb(alpha, Mix(88, 255), Mix(165, 255), Mix(222, 255));

        var baseScale = context.ScreenHeight * 0.030f / 64f;
        var toneScale = baseScale * (1f + 0.30f * flash);
        var toneY = ChipInset(context) + context.ScreenHeight * 0.115f;
        context.TextBatcher.DrawText2D(toneText, new Vector2(startX, toneY), toneColor, toneScale);
    }

    private void RenderLyrics(RenderContext context)
    {
        var startX = context.ScreenWidth * 0.17f;

        // Scaled down lyric height to ~3.0% of screen height
        var lyricScale = context.ScreenHeight * 0.028f / 64f;

        // Below the tone badge's WORST-CASE extent: the badge sits at 0.115·H
        // with ~0.030·H text that flash-scales up to ×1.30 (bottom ≈ 0.154·H),
        // so the lyric block starts at 0.165·H to keep a visible gap even
        // mid-flash (0.14·H would collide with the badge).
        var top = ChipInset(context);
        var currentY = top + context.ScreenHeight * 0.165f;
        var nextY = top + context.ScreenHeight * 0.205f;

        RenderLyricPhrase? currentLine = null;
        RenderLyricPhrase? nextLine = null;

        for (var i = 0; i < context.Snapshot.LyricPhraseCount; i++)
        {
            var lp = context.Snapshot.LyricPhrases[i];

            if (lp.EndTime >= context.TimeMs - 100 && lp.StartTime <= context.TimeMs + 5000)
            {
                if (currentLine == null && context.TimeMs >= lp.StartTime - 1000)
                    currentLine = lp;
                else if (nextLine == null && context.TimeMs < lp.StartTime)
                    nextLine = lp;
            }
        }

        if (currentLine.HasValue) DrawCurrentLyricLine(context, currentLine.Value, startX, currentY, lyricScale);

        if (nextLine.HasValue)
        {
            // PREVIEW_3D_LYRICS (255, 255, 255)
            var nextColor = Colors.White;
            DrawLyricLineSolid(context, nextLine.Value, startX, nextY, lyricScale, nextColor);
        }
    }

    private void DrawCurrentLyricLine(RenderContext context, RenderLyricPhrase phrase, float x, float y, float scale)
    {
        // PREVIEW_3D_LYRICS_PASSED (13, 162, 255)
        var passedColor = Color.FromArgb(255, 13, 162, 255);
        // PREVIEW_3D_LYRICS (255, 255, 255)
        var upcomingColor = Colors.White;

        var currentX = x;
        for (var i = 0; i < phrase.WordCount; i++)
        {
            var word = context.Snapshot.LyricWords[phrase.WordOffset + i];
            var color = context.TimeMs >= word.Time ? passedColor : upcomingColor;
            currentX = context.TextBatcher.DrawText2D(word.Text, new Vector2(currentX, y), color, scale);
            if (!word.IsWordPart)
                currentX = context.TextBatcher.DrawText2D(" ", new Vector2(currentX, y), color, scale);
        }
    }

    private void DrawLyricLineSolid(RenderContext context, RenderLyricPhrase phrase, float x, float y, float scale,
        Color color)
    {
        var currentX = x;
        for (var i = 0; i < phrase.WordCount; i++)
        {
            var word = context.Snapshot.LyricWords[phrase.WordOffset + i];
            currentX = context.TextBatcher.DrawText2D(word.Text, new Vector2(currentX, y), color, scale);
            if (!word.IsWordPart)
                currentX = context.TextBatcher.DrawText2D(" ", new Vector2(currentX, y), color, scale);
        }
    }
}
