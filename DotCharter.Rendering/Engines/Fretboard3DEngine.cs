using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using Avalonia.Media;
using DotCharter.Core.Domain.Vocals;
using DotCharter.Rendering.Batching;
using DotCharter.Rendering.Components;
using DotCharter.Rendering.Data;
using DotCharter.Rendering.Data.Render;
using DotCharter.Rendering.Scene;
using DotCharter.Rendering.Vulkan;
using DotCharter.Rendering.Vulkan.Composed;
using SkiaSharp;
using DotCharter.Core.Graphics;

namespace DotCharter.Rendering.Engines;

public class Fretboard3DEngine : IDisposable
{
    private readonly Stopwatch _gpuStopwatch = new();
    private readonly QuadBatcher _quadBatcher;

    private readonly RenderResources _resources = new();
    private readonly RenderScene _scene;
    private readonly TextBatcher _textBatcher;
    private readonly IVulkanRenderService _vulkanRenderer;

    // Reused per-frame to avoid GC pressure during playback.
    private readonly RenderContext _frameContext = new();
    private readonly StageLightsRenderer _stageLights = new();
    private readonly ShowlightChipComponent _showlightChip = new();

    private float _currentCamY;
    private float _currentCamZ;
    private Matrix4x4 _currentMvp;
    private float _currentTimeMs;
    private bool _isInitialized;

    /// <summary>Reused single-element view list backing the single-snapshot
    /// overload (its <see cref="HighwayViewport.Camera" /> IS the editor / solo
    /// gameplay camera, so that path behaves byte-for-byte as it did before the
    /// split highway existed). The render thread must not allocate per
    /// frame.</summary>
    private readonly HighwayViewport[] _soloViewports = [new()];

    // Headless (offscreen): the three chart textures fall back to solid
    // substitutes UNLESS the caller asked for the real sheets. That is ALL these
    // flags govern — the font atlas is built headless exactly as it is in the app
    // (SkiaSharp, no thread affinity). Never set on the app paths.
    // See InitializeHeadless / LoadVulkanTextures.
    private bool _headless;

    // Default TRUE with _headless: the golden frames were captured against the
    // 1×1 white substitutes, and a texture-less gem is deterministic solid vertex
    // colour. A caller that wants the frame the USER sees (the 3D highway
    // harness's --dump) opts out.
    private bool _headlessSolidTextures;

    public Fretboard3DEngine(IVulkanRenderService vulkanRenderer)
    {
        _vulkanRenderer = vulkanRenderer;
        _quadBatcher = new QuadBatcher(_vulkanRenderer);
        _textBatcher = new TextBatcher(_vulkanRenderer);
        _scene = new RenderScene();
    }

    public double GpuSubmitTimeMs { get; private set; }

    /// <summary>
    ///     True while the anchor-follow camera easing has not converged on its
    ///     target yet. The 3D render loop's idle gate (0.14 P6 —
    ///     Preview3DComposedCanvas/Preview3DFrameGate) keeps rendering full-rate while
    ///     this is set so a seek-while-paused still pans smoothly, and parks
    ///     once the camera settles. Within <see cref="CameraSettledEpsilon" />
    ///     the eased values snap to their targets (sub-pixel — invisible) so
    ///     settling is deterministic instead of an asymptote.
    /// </summary>
    public bool IsCameraAnimating { get; private set; }

    private const float CameraSettledEpsilon = 0.002f;

    /// <summary>
    ///     Drops the glyph sheet's project-scoped characters (see
    ///     <see cref="Vulkan.FontAtlas.ResetToSeedCharacters" />). This engine is
    ///     an app-wide SINGLETON, so without a reset every lyric, section name and
    ///     phrase name of every song opened in the session stays registered for the
    ///     life of the process — and the fixed 2048² sheet eventually stops fitting,
    ///     at which point glyphs render blank. Driven from the UI's
    ///     <c>ActiveProject</c> notification (<c>Preview3DRenderDriver</c>), the same
    ///     hook every other project-scoped cache prunes on.
    /// </summary>
    public void ResetTextRegistrations() => _resources.FontAtlas?.ResetToSeedCharacters();

    public void Dispose()
    {
        _resources.NotesTexture?.Dispose();
        _resources.FingeringTexture?.Dispose();
        _resources.InlayTexture?.Dispose();
        _resources.FontAtlas?.Dispose();
    }

    /// <summary>
    ///     Composed-output init (0.16): same engine, same pipelines/scene —
    ///     only the presentation differs (exportable images instead of a
    ///     swapchain; see <see cref="IVulkanComposedPresenter" />).
    ///     <paramref name="adapterLuid" /> is the compositor's adapter LUID
    ///     from <c>ICompositionGpuInterop.DeviceLuid</c>.
    /// </summary>
    public void InitializeComposed(byte[]? adapterLuid)
    {
        if (_vulkanRenderer is not IVulkanComposedPresenter presenter)
            throw new NotSupportedException("The renderer does not implement composed output.");
        presenter.InitializeDeviceComposed(adapterLuid);
        InitializeSharedResources();
    }

    /// <summary>
    ///     Headless offscreen init (testing): same engine, same pipelines/scene,
    ///     all frame passes — only the output differs (a device-local image
    ///     copied back to the CPU instead of a swapchain; see
    ///     <see cref="IVulkanHeadlessRenderer" />). No window, no surface, no
    ///     compositor. Asset textures fall back to solid substitutes (the
    ///     Avalonia asset system needs the running app); the FONT ATLAS is built
    ///     exactly as in the app, so text really rasterizes here and a golden
    ///     frame can prove a label appeared. Drives <c>HeadlessHighwayRenderer</c>.
    ///     <para>
    ///         <paramref name="useRealChartTextures" /> decides which of two
    ///         legitimate pictures you get. FALSE (the default, and what every
    ///         golden frame was captured against) substitutes 1×1 solid-white
    ///         textures: gems render as flat vertex-coloured quads, which is
    ///         deterministic and easy to assert on. TRUE decodes the real embedded
    ///         note/fingering/inlay sheets — the frame the USER actually sees, and
    ///         the only honest input to a REVIEW (the 3D highway harness's
    ///         <c>--dump</c>). It was never an availability question: the sheets
    ///         are <c>EmbeddedResource</c>s of this assembly and decode with
    ///         SkiaSharp, so they load headless perfectly well.
    ///     </para>
    /// </summary>
    public void InitializeHeadless(bool useRealChartTextures = false)
    {
        if (_vulkanRenderer is not IVulkanHeadlessRenderer headless)
            throw new NotSupportedException("The renderer does not implement headless output.");
        _headless = true;
        _headlessSolidTextures = !useRealChartTextures;
        headless.InitializeDeviceHeadless();
        InitializeSharedResources();
    }

    private void InitializeSharedResources()
    {
        if (_isInitialized) return;
        _isInitialized = true;

        var vertCode = LoadSpirv("highway.vert.spv");
        var fragCode = LoadSpirv("highway.frag.spv");
        _vulkanRenderer.InitializePipelines(vertCode, fragCode);

        var textVertCode = LoadSpirv("text.vert.spv");
        var textFragCode = LoadSpirv("text.frag.spv");
        _vulkanRenderer.InitializeTextPipeline(textVertCode, textFragCode);

        // Post-process chain (0.15.6 / plan 5.5): bloom extract/blur +
        // composite. Built once; whether it RUNS is a per-frame toggle.
        _vulkanRenderer.InitializePostPipelines(
            LoadSpirv("fullscreen.vert.spv"),
            LoadSpirv("post_extract.frag.spv"),
            LoadSpirv("post_blur.frag.spv"),
            LoadSpirv("post_blit.frag.spv"));

        LoadVulkanTextures();

        _scene.OpaqueElements.Add(new BoardGeometryComponent());
        _scene.TransparentElements.Add(new HighwayRenderComponent());
        _scene.TransparentElements.Add(new NotesRenderComponent());
        _scene.TransparentElements.Add(new NoteHitExplosionRenderComponent());
        _scene.TransparentElements.Add(new HitSparkRenderComponent());
        _scene.TransparentElements.Add(new EnhancedHitFeedbackRenderComponent());
        _scene.TransparentElements.Add(new LivePlayFeedbackRenderComponent());
        // Depth fog stays LAST in the transparent list: it must layer over
        // everything on the far highway (0.14.2 C).
        _scene.TransparentElements.Add(new DepthFogRenderComponent());
        // The Ui pass is the 3D-TEXT pass: everything here draws AFTER
        // _textBatcher.SetState(atlas, sceneMvp) and before its Flush. A text
        // component in the opaque/transparent lists stages its glyphs against a
        // null atlas (frame 1) and then the previous frame's ortho HUD matrix —
        // i.e. it renders nothing, forever, in silence. See ChordNameComponent.
        _scene.UiElements.Add(new TextOverlayComponent());
        _scene.UiElements.Add(new ChordNameComponent());
        // Drains the bend-peak labels the transparent pass deferred (it cannot
        // stage 3D text itself, for the reason above).
        _scene.UiElements.Add(new BendLabelComponent());
        _scene.HudElements.Add(new HudComponent());
        _scene.HudElements.Add(_showlightChip);

        _scene.Initialize(_vulkanRenderer, _resources);
    }

    /// <summary>
    ///     Registers every character the chart can ever draw (all lyric words,
    ///     all phrase/section names) so the atlas rebuild happens once at load
    ///     time. RebuildIfNeeded stalls the render thread (WaitDeviceIdle plus
    ///     a synchronous UI-thread atlas repaint), so letting the per-frame
    ///     visible-window scan discover a character mid-playback reads as a
    ///     frame hitch exactly when a lyric scrolls into view. Returns false
    ///     while the atlas does not exist yet so the caller can retry later;
    ///     the per-frame scan in RenderFrame stays as the safety net for text
    ///     edited during playback.
    /// </summary>
    public bool PreRegisterChartText(IList<LyricPhrase> lyricPhrases, IList<EventPoint> eventPoints,
        IList<ToneChange> tones)
    {
        var atlas = _resources.FontAtlas;
        if (atlas == null) return false;

        for (var i = 0; i < lyricPhrases.Count; i++)
        {
            var lyrics = lyricPhrases[i].Lyrics;
            for (var j = 0; j < lyrics.Count; j++) atlas.RegisterCharacters(lyrics[j].Text);
        }

        for (var i = 0; i < eventPoints.Count; i++)
        {
            atlas.RegisterCharacters(eventPoints[i].Phrase);
            atlas.RegisterCharacters(eventPoints[i].Section);
        }

        // Tone names, like the 2D twin (ChartTextPreRegistration). HudComponent
        // .RenderActiveTone is the only surface that draws them and nothing on
        // the 3D side ever registered them, so a preset named "Röhren Crunch"
        // lost its non-seed glyphs silently — TryGetGlyph simply skips a
        // character the atlas has not rasterized.
        for (var i = 0; i < tones.Count; i++)
            atlas.RegisterCharacters(tones[i].ToneName);

        return true;
    }

    /// <summary>Single-view frame — the editor preview and a solo gameplay run.
    /// Delegates to the viewport path with one full-frame view, so there is
    /// exactly ONE frame implementation.</summary>
    public bool RenderFrame(int width, int height, float frameTime, FrameSnapshot snapshot)
    {
        var solo = _soloViewports[0];
        solo.Snapshot = snapshot;
        solo.Rect = new HighwayViewportRect(0, 0, Math.Max(1, width), Math.Max(1, height));
        return RenderFrame(width, height, frameTime, _soloViewports, 1);
    }

    /// <summary>
    ///     Multi-view frame — the couch co-op SPLIT HIGHWAY. One
    ///     <c>BeginFrame</c>/<c>EndFrame</c> pair, one depth buffer, one present;
    ///     <paramref name="viewportCount" /> scene recordings, each scissored to
    ///     its own player's rect and driven by its OWN snapshot and camera.
    ///     <para>
    ///         Everything expensive stays shared by construction: pipelines, the
    ///         chart textures, the glyph atlas and the scene component graph are
    ///         engine-owned and touched once per view, not duplicated per player.
    ///         Only what genuinely differs per player — the chart slice and the
    ///         anchor-follow camera easing — lives on the
    ///         <see cref="HighwayViewport" />.
    ///     </para>
    ///     <para>
    ///         Frame-GLOBAL state (post effects, the resize check, the glyph
    ///         atlas rebuild) is resolved once, ahead of the frame, from all
    ///         views together — a per-view <c>SetPostEffects</c> would flag a
    ///         swapchain rebuild every frame if the two players disagreed, and a
    ///         per-view atlas signature would thrash the gate between two
    ///         alternating values and re-probe every glyph twice a frame.
    ///     </para>
    ///     <paramref name="viewportCount" /> lets a caller reuse an oversized
    ///     array (no per-frame allocation on the render thread).
    /// </summary>
    public bool RenderFrame(int width, int height, float frameTime,
        IReadOnlyList<HighwayViewport> viewports, int viewportCount = -1)
    {
        ArgumentNullException.ThrowIfNull(viewports);
        if (!_vulkanRenderer.IsPipelinesInitialized) return false;

        var count = viewportCount < 0 ? viewports.Count : Math.Min(viewportCount, viewports.Count);
        if (count <= 0) return false;

        // Frame-global toggles come from the FIRST view with a snapshot. They
        // are properties of the render target, not of a player.
        FrameSnapshot? lead = null;
        for (var i = 0; i < count && lead == null; i++) lead = viewports[i].Snapshot;
        if (lead == null) return false;

        // Post effects (0.15.6): recorded BEFORE the resize check — an on/off
        // flip flags SwapchainNeedsRecreation and rides the same deferred
        // rebuild below.
        _vulkanRenderer.SetPostEffects(lead.EnableBloom, lead.EnableFilmicTonemap);

        if (_vulkanRenderer.CurrentWidth != width || _vulkanRenderer.CurrentHeight != height ||
            _vulkanRenderer.SwapchainNeedsRecreation)
        {
            _vulkanRenderer.Resize((uint)width, (uint)height);
        }

        // Scan every view's snapshot for new characters before beginning the
        // frame. Gated by a cheap content signature (the 2D ChartRenderer's
        // _lastRegisterSig approach): when the visible text
        // set is unchanged frame-to-frame — the overwhelmingly common case —
        // there can be no new character, so the per-char atlas probes are
        // skipped entirely. What gets registered is unchanged. The signature
        // folds ALL views so two players cannot flip it back and forth.
        if (_resources.FontAtlas != null)
        {
            var sig = ComputeRegisterSignature(viewports, count);
            if (sig != _lastRegisterSig)
            {
                for (var v = 0; v < count; v++)
                {
                    var snapshot = viewports[v].Snapshot;
                    if (snapshot == null) continue;

                    for (var i = 0; i < snapshot.LyricWordCount; i++)
                        _resources.FontAtlas.RegisterCharacters(snapshot.LyricWords[i].Text);

                    _resources.FontAtlas.RegisterCharacters(snapshot.DebugText);

                    for (var i = 0; i < snapshot.EventPointCount; i++)
                    {
                        _resources.FontAtlas.RegisterCharacters(snapshot.EventPoints[i].Phrase);
                        _resources.FontAtlas.RegisterCharacters(snapshot.EventPoints[i].Section);
                    }

                    // The HUD's tone badge. It is the RESOLVED name, not a
                    // chart row: with in-game tones on it is the rig's applied
                    // preset, which no pre-registration pass can see.
                    _resources.FontAtlas.RegisterCharacters(snapshot.ActiveToneName);
                    // The editor's show-light chip: localized, so it can carry
                    // glyphs no chart text has (ja-JP).
                    _resources.FontAtlas.RegisterCharacters(snapshot.ShowlightChipText);
                }

                _lastRegisterSig = sig;
            }

            // Stays OUTSIDE the signature gate: PreRegisterChartText (load-
            // time whole-chart registration) also queues glyphs and relies on
            // this per-frame flush.
            _resources.FontAtlas.RebuildIfNeeded();
        }

        // The camera easing is per view and must advance whether or not the
        // frame is accepted below, so IsCameraAnimating is the OR over the
        // views the caller handed us (the idle gate reads it after the call).
        var anyAnimating = false;

        if (!_vulkanRenderer.BeginFrame()) return false;

        // Everything from here to EndFrame runs inside a begun frame. EndFrame MUST run
        // even if recording throws: BeginFrame reset this frame's in-flight fence and
        // only EndFrame submits the work that signals it again. Skipping it left the
        // fence permanently unsignalled, so every subsequent BeginFrame timed out and
        // returned false — which the render driver reads as a benign "declined" frame.
        // The result was a silent, permanent wedge: a spinning loop, no output, and no
        // error, forever. Submitting a partially-recorded command buffer is legal (it
        // simply draws less) and is vastly preferable to losing the fence.
        try
        {
            var isSplit = count > 1;
            for (var v = 0; v < count; v++)
            {
                var view = viewports[v];
                var snapshot = view.Snapshot;
                if (snapshot == null) continue;

                var rect = view.Rect;
                var vw = Math.Max(1, rect.Width);
                var vh = Math.Max(1, rect.Height);

                // The single-view path never touches the dynamic viewport
                // state, so it stays byte-for-byte the pre-split frame:
                // BeginFrame already set the full render extent.
                if (isSplit || rect.X != 0 || rect.Y != 0 || rect.Width != width || rect.Height != height)
                    _vulkanRenderer.SetViewportRect(rect.X, rect.Y, vw, vh);

                _currentTimeMs = (float)snapshot.CurrentTimeMs;
                var (_, _, mvp, camY, camZ) = CalculateCameraMvp(view.Camera, frameTime,
                    snapshot.CurrentTimeMs, snapshot.Anchors, snapshot.AnchorCount, vw, vh,
                    snapshot.Preview3DCameraFill, snapshot.LeftyFlip);
                anyAnimating |= view.Camera.IsAnimating;

                // Lefty (v0.34): the scene MVP mirrors X, so in-world text needs
                // the glyph-local counter-flip to stay readable (double mirror =
                // identity for glyph orientation AND run order). The ortho HUD
                // pass has its own matrix and is untouched. Per view: two
                // players may hold the guitar differently.
                _textBatcher.MirrorLocalX = snapshot.LeftyFlip;
                _currentMvp = mvp;
                _currentCamY = camY;
                _currentCamZ = camZ;
                var boardWidth = FretboardConstants.GetLinearFretPosition(24) + 1.0f;
                var camPos = new Vector3(view.Camera.CamX, _currentCamY, _currentCamZ);

                RenderFrameBody(vw, vh, snapshot, boardWidth, camPos);
            }
        }
        finally
        {
            IsCameraAnimating = anyAnimating;
            _gpuStopwatch.Restart();
            _vulkanRenderer.EndFrame();
            GpuSubmitTimeMs = _gpuStopwatch.Elapsed.TotalMilliseconds;
        }

        return true;
    }

    /// <summary>The recording half of ONE view inside a begun frame. Split out
    /// so the frame loop can guarantee EndFrame in a finally — see the remarks
    /// on the viewport <c>RenderFrame</c> overload — and so a split frame can
    /// run it once per player. <paramref name="width" />/<paramref name="height" />
    /// are the VIEW's extents, not the frame's: every screen-space matrix here
    /// (crowd background, vignette, HUD ortho) must be viewport-local or the two
    /// players' overlays would be laid out against the whole window and land in
    /// each other's halves. Do not call this directly.</summary>
    private void RenderFrameBody(
        int width, int height, FrameSnapshot snapshot, float boardWidth, Vector3 camPos)
    {
        var orthoMvp = Matrix4x4.CreateOrthographicOffCenter(0, width, 0, height, -1, 1);

        // The highway's viewing geometry, resolved ONCE per frame from the two
        // user knobs and shared by every renderer through the context. These
        // were compile-time constants; they are clamped here so a corrupt config
        // can never produce a zero scale (infinite horizon) downstream.
        var timeToZScale = HighwaySpeedPolicy.TimeToZScale(snapshot.HighwayScrollSpeed);
        var fadeEndZ = HighwaySpeedPolicy.ClampDrawDistanceZ(snapshot.HighwayDrawDistanceZ);
        var fadeStartZ = HighwaySpeedPolicy.FadeStartZ(fadeEndZ);

        // Crowd-driven stage background (0.14.2 C) and the show lights' stage
        // lights: screen-space quads drawn FIRST, so the whole 3D scene
        // layers over them. Ortho z=+1 maps to depth 0 and the transparent
        // pipeline writes no depth, so the scene's own depth testing is
        // unaffected. The lights follow SONG time (the snapshot's), so they
        // freeze with the transport.
        var sky = StageLights.Resolve(snapshot, snapshot.CurrentTimeMs);
        if (snapshot.EnableCrowdBackground || sky.Visible)
        {
            _quadBatcher.BeginPass(orthoMvp, camPos, fadeStartZ, fadeEndZ,
                _currentTimeMs, false);
            if (snapshot.EnableCrowdBackground) DrawCrowdBackground(width, height, snapshot.CrowdEnergy);
            if (sky.Visible)
            {
                var far = CrowdBackgroundPalette.ProjectToScreen(_currentMvp,
                    new Vector3(camPos.X, FretboardConstants.ChartboardYPosition, fadeEndZ), width, height);
                var centreU = far is { } f ? Math.Clamp(f.X / width, 0f, 1f) : 0.5f;
                var highway = HighwaySpan.Project(_currentMvp, snapshot.Anchors, snapshot.AnchorCount,
                    snapshot.CurrentTimeMs, fadeEndZ / timeToZScale, FretboardConstants.ChartboardYPosition,
                    fadeEndZ, width, height) ?? HighwaySpan.Centred(centreU);
                _stageLights.Draw(_quadBatcher, sky, snapshot.CurrentTimeMs, width, height, highway, far?.Y);
            }

            _quadBatcher.Flush();
        }

        var context = _frameContext;
        context.Snapshot = snapshot;
        context.BoardWidth = boardWidth;
        context.TimeMs = _currentTimeMs;
        context.QuadBatcher = _quadBatcher;
        context.TextBatcher = _textBatcher;
        context.ScreenWidth = width;
        context.ScreenHeight = height;
        context.TimeToZScale = timeToZScale;
        context.FadeStartZ = fadeStartZ;
        context.FadeEndZ = fadeEndZ;
        context.HorizonMs = fadeEndZ / timeToZScale;
        // Reset BEFORE the geometry passes fill it: a frame that bails out before
        // RenderUi must not leak its bend labels into the next frame's queue.
        context.ClearDeferred3DLabels();
        // Upcoming-anchor preview (0.14.2 B): one shared lookup per frame,
        // consumed by TextOverlayComponent (dimmed fret numbers) and
        // HighwayRenderComponent (floor sweep).
        context.AnchorPreview = snapshot.EnableAnchorPreview || snapshot.EnableAnchorSweep
            ? NextAnchorWindow.Find(snapshot.Anchors, snapshot.AnchorCount, snapshot.CurrentTimeMs,
                snapshot.AnchorPreviewLeadMs)
            : AnchorPreviewInfo.None;

        // In-shader effect bits (plan 5.6/5.7), computed once per frame from
        // the snapshot toggles. Only the 3D scene passes carry them — the
        // screen-space ortho passes (crowd background, vignette) stay unlit.
        // String shimmer follows the Live Play suppression convention of the
        // other time-based effects (explosions, enhanced hit feedback).
        var effects = HighwayEffects.None;
        if (snapshot.EnableHighwayLighting) effects |= HighwayEffects.Lighting;
        if (snapshot.EnableStringShimmer && !snapshot.LivePlayActive) effects |= HighwayEffects.StringShimmer;

        _quadBatcher.BeginPass(_currentMvp, camPos, fadeStartZ, fadeEndZ,
            _currentTimeMs, true, effects);
        _scene.RenderOpaque(context);
        _quadBatcher.Flush();

        _quadBatcher.BeginPass(_currentMvp, camPos, fadeStartZ, fadeEndZ,
            _currentTimeMs, false, effects);
        _scene.RenderTransparent(context);
        _quadBatcher.Flush();

        _textBatcher.SetState(_resources.FontAtlas, _currentMvp);
        _scene.RenderUi(context);
        _textBatcher.Flush();

        // Vignette (0.14.2 C): screen-space border gradients in an ortho quad
        // pass, drawn over the scene but UNDER the HUD text so the corner
        // stats stay readable. Ortho z=+1 maps to depth 0 under this matrix
        // (LessOrEqual passes everywhere), so it never fights the 3D depths.
        if (snapshot.EnableVignette)
        {
            _quadBatcher.BeginPass(orthoMvp, camPos, fadeStartZ, fadeEndZ,
                _currentTimeMs, false);
            DrawVignette(width, height);
            _quadBatcher.Flush();
        }

        // The editor's show-light chip sits on a dark plate drawn here, in
        // its own ortho quad pass, so the HUD text pass that follows lands on it.
        if (!string.IsNullOrEmpty(snapshot.ShowlightChipText))
        {
            _quadBatcher.BeginPass(orthoMvp, camPos, fadeStartZ, fadeEndZ,
                _currentTimeMs, false);
            _showlightChip.DrawPlate(_quadBatcher, snapshot.ShowlightChipText, height);
            _quadBatcher.Flush();
        }

        _textBatcher.SetState(_resources.FontAtlas, orthoMvp);
        _scene.RenderHud(context);
        _textBatcher.Flush();
    }

    /// <summary>
    ///     Stage backdrop covering the FULL VIEWPORT: a vertical
    ///     sky gradient (energy-tinted top → horizon-dark) plus a floor half
    ///     fading into near-black at the bottom edge, and a warm horizon glow
    ///     band — all driven by the crowd energy through the pure
    ///     <see cref="CrowdBackgroundPalette" /> (unit-tested). Covering the
    ///     whole screen (not just above the horizon) is what removes the black
    ///     band above the highway, the horizon seam and the corner gap; the 3D
    ///     scene draws over it. Vertex-colour quads only — no shader change.
    /// </summary>
    private void DrawCrowdBackground(int width, int height, float energy)
    {
        var (top, horizon) = CrowdBackgroundPalette.Compute(energy);
        var normal = new Vector3(0, 0, -1);
        const float z = 1f; // → depth 0 under the ortho matrix

        // FULL-VIEWPORT coverage: two stacked quads spanning the
        // WHOLE screen (y 0..height, x 0..width) so there is never a hard
        // horizon seam or an aspect-dependent corner gap — the 3D scene (opaque,
        // depth-tested) simply draws over it. This ortho pass is Vulkan y-DOWN:
        // param-y 0 is the FRAME TOP, param-y = height the BOTTOM. So the
        // energy-tinted sky belongs at y = 0 and the near-black must recede
        // toward y = height, BELOW the horizon where the highway covers it.
        // 0.20.1: the two colour stops were swapped, which parked the near-black
        // in the VISIBLE sky and read as a dark slab floating above the horizon
        // (owner report); the tint now sits at the top and only the hidden lower
        // band goes near-black, keeping the highway/strings' contrast.
        var horizonY = height * 0.55f;

        var skyBottom = Color.FromArgb(255, (byte)(top.R / 3), (byte)(top.G / 3), (byte)(top.B / 3));
        var deepDark = Color.FromArgb(255, (byte)(top.R / 8), (byte)(top.G / 8), (byte)(top.B / 8));

        // Sky half (frame TOP → horizon): energy-tinted at the top down to a dim
        // horizon-dark.
        _quadBatcher.AddQuad(
            new Vector3(0, 0, z), new Vector3(width, 0, z),
            new Vector3(width, horizonY, z), new Vector3(0, horizonY, z),
            normal, top, top, skyBottom, skyBottom);

        // Floor half (horizon → frame BOTTOM): horizon-dark fading into a deep
        // near-black at the bottom edge — no seam, no raw clear colour anywhere.
        _quadBatcher.AddQuad(
            new Vector3(0, horizonY, z), new Vector3(width, horizonY, z),
            new Vector3(width, height, z), new Vector3(0, height, z),
            normal, skyBottom, skyBottom, deepDark, deepDark);

        // 0.15.1: the horizon glow band was removed. Spanning the full width at
        // a fixed screen height, it read as a hard horizontal "orange line"
        // across the scene (owner report) rather than an atmospheric glow. The
        // energy tint still lives in the sky gradient above.
        _ = horizon;

        // The radial stage glow that sat right behind the far end of the
        // highway is gone — it lit exactly where the notes are read. The show
        // lights are the stage lights (StageLightsRenderer).
    }

    /// <summary>
    ///     0.14.2 C: four screen-space border gradients (edges dark → clear
    ///     inward; overlapping corners naturally read darker). Vertex-color
    ///     quads through the existing solid-color shader mode — no shader
    ///     change. Deliberately subtle (~27% edge alpha).
    /// </summary>
    private void DrawVignette(int width, int height)
    {
        var dark = Color.FromArgb(70, 0, 0, 0);
        var clear = Color.FromArgb(0, 0, 0, 0);
        var insetX = width * 0.16f;
        var insetY = height * 0.16f;
        var normal = new Vector3(0, 0, -1);
        const float z = 1f; // → depth 0 under the ortho matrix above

        // Left
        _quadBatcher.AddQuad(
            new Vector3(0, height, z), new Vector3(insetX, height, z),
            new Vector3(insetX, 0, z), new Vector3(0, 0, z),
            normal, dark, clear, clear, dark);
        // Right
        _quadBatcher.AddQuad(
            new Vector3(width - insetX, height, z), new Vector3(width, height, z),
            new Vector3(width, 0, z), new Vector3(width - insetX, 0, z),
            normal, clear, dark, dark, clear);
        // Top
        _quadBatcher.AddQuad(
            new Vector3(0, height, z), new Vector3(width, height, z),
            new Vector3(width, height - insetY, z), new Vector3(0, height - insetY, z),
            normal, dark, dark, clear, clear);
        // Bottom
        _quadBatcher.AddQuad(
            new Vector3(0, insetY, z), new Vector3(width, insetY, z),
            new Vector3(width, 0, z), new Vector3(0, 0, z),
            normal, clear, clear, dark, dark);
    }

    private long _lastRegisterSig;

    /// <summary>
    ///     Reference-hash of every string the 3D text path can register this
    ///     frame (mirror of <c>ChartRenderer.ComputeRegisterSignature</c>).
    ///     Strings are immutable, so an in-place text edit (lyric syllable,
    ///     section rename) replaces the string INSTANCE in the snapshot — an
    ///     O(1) <see cref="RuntimeHelpers.GetHashCode(object)" /> per string
    ///     catches every edit while being far cheaper than per-char atlas
    ///     probes; a stale-instance false positive merely re-runs the
    ///     idempotent registration. Pure/static so it is unit-testable
    ///     (<c>Fretboard3DRegisterSignatureTests</c>).
    /// </summary>
    public static long ComputeRegisterSignature(FrameSnapshot snapshot)
    {
        unchecked
        {
            long h = 17;
            h = h * 31 + snapshot.LyricWordCount;
            for (var i = 0; i < snapshot.LyricWordCount; i++)
                h = h * 31 + RuntimeHelpers.GetHashCode(snapshot.LyricWords[i].Text);

            h = h * 31 + snapshot.EventPointCount;
            for (var i = 0; i < snapshot.EventPointCount; i++)
            {
                h = h * 31 + RuntimeHelpers.GetHashCode(snapshot.EventPoints[i].Phrase);
                h = h * 31 + RuntimeHelpers.GetHashCode(snapshot.EventPoints[i].Section);
            }

            h = h * 31 + RuntimeHelpers.GetHashCode(snapshot.ActiveToneName);
            h = h * 31 + RuntimeHelpers.GetHashCode(snapshot.ShowlightChipText);
            h = h * 31 + RuntimeHelpers.GetHashCode(snapshot.DebugText);
            return h;
        }
    }

    /// <summary>
    ///     Fold of <see cref="ComputeRegisterSignature(FrameSnapshot)" /> over
    ///     every view in a split frame. Load-bearing for the split highway: a
    ///     per-view gate would see two DIFFERENT signatures alternating within
    ///     one frame, so it would miss every time and re-probe both charts'
    ///     glyphs on every frame — the exact per-frame cost the gate exists to
    ///     remove. The count is folded in too, so adding or dropping a player
    ///     re-registers.
    /// </summary>
    public static long ComputeRegisterSignature(IReadOnlyList<HighwayViewport> viewports, int count)
    {
        ArgumentNullException.ThrowIfNull(viewports);
        unchecked
        {
            long h = 17;
            h = h * 31 + count;
            for (var i = 0; i < count && i < viewports.Count; i++)
            {
                var snapshot = viewports[i].Snapshot;
                h = h * 31 + (snapshot == null ? 0 : ComputeRegisterSignature(snapshot));
            }

            return h;
        }
    }

    private static byte[] LoadSpirv(string shaderFileName)
    {
        // Shaders are embedded in the DotCharter.Rendering assembly with LogicalName
        // "DotCharter.Rendering.Shaders.<filename>" (configured in DotCharter.Rendering.csproj).
        var resourceName = $"DotCharter.Rendering.Shaders.{shaderFileName}";
        using var stream = typeof(Fretboard3DEngine).Assembly.GetManifestResourceStream(resourceName)
                           ?? throw new FileNotFoundException(
                               $"Embedded shader resource not found: '{resourceName}'. " +
                               "Ensure the shader file exists under Assets/Shaders/ and is wired up as " +
                               "an <EmbeddedResource> in csproj.");
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    private void LoadVulkanTextures()
    {
        if (!_vulkanRenderer.IsPipelinesInitialized) return;

        // The chart textures are EmbeddedResources of THIS assembly, loaded the
        // same way as the SPIR-V shaders. They must not be <AvaloniaResource>s in
        // the UI assembly, opened through a hardcoded avares://DotCharter/ URI —
        // an upward dependency on DotCharter.UI's assembly name that no csproj
        // expresses, that no build step could catch, and that would fail at runtime
        // with nothing but a Debug.WriteLine (silent white textures in Release).
        // DotCharter.Rendering references DotCharter.Core and nothing else; keep
        // it that way.
        _resources.NotesTexture = LoadEmbeddedTexture("notes.png") ?? HeadlessTextureFallback();
        _resources.FingeringTexture = LoadEmbeddedTexture("fingering.png") ?? HeadlessTextureFallback();
        _resources.InlayTexture = LoadEmbeddedTexture("inlays.png") ?? HeadlessTextureFallback();

        // The atlas is built HEADLESS TOO — and that is load-bearing, not tidiness.
        //
        // Do not skip it: the atlas is rasterized with SkiaSharp (see FontAtlas.Rasterize:
        // no Avalonia type, no thread affinity, and the only GPU call is the same
        // LoadTexture the headless path already uses for its 1x1 substitutes), so no
        // headless host lacks anything it needs. Skipping it would leave a silent hole
        // in what a golden frame can prove: TextBatcher tolerates a null atlas by
        // making every text draw a NO-OP, so every 3D label would render as nothing
        // headless and every golden frame would pass regardless — invisible chord
        // names would ship under green tests.
        //
        // null, NOT FontFamily.Default.Name: that is the "$Default" sentinel,
        // which matches no installed family, so Skia fell back to its default
        // typeface and silently discarded the BOLD weight for every 3D label.
        _resources.FontAtlas = new FontAtlas(_vulkanRenderer, null, 64);
    }

    /// <summary>
    ///     Headless substitute for a chart texture that was skipped (the default)
    ///     or genuinely failed to decode: a 1×1
    ///     solid-white texture so components binding NotesTexture /
    ///     FingeringTexture / InlayTexture get a valid non-null texture and the
    ///     vertex colours pass through unmodulated (gems render as solid colored
    ///     quads instead of textured atlas sprites — deterministic, visible).
    ///     Returns null off the headless path, so the app keeps its existing
    ///     null-then-white-fallback behaviour unchanged.
    /// </summary>
    private VulkanTexture? HeadlessTextureFallback()
    {
        if (!_headless) return null;
        byte[] whitePixel = [255, 255, 255, 255];
        return _vulkanRenderer.LoadTexture(1, 1, whitePixel);
    }

    /// <summary>
    ///     Decodes one embedded chart texture into premultiplied RGBA8888 — the
    ///     exact bytes the previous Avalonia <c>Bitmap.CopyPixels</c> +
    ///     R/B-swap produced — and uploads it. SkiaSharp is used rather than
    ///     <c>Avalonia.Media.Imaging.Bitmap</c> because it needs no initialized
    ///     Avalonia platform, so the decode has no app-lifetime dependency.
    /// </summary>
    private VulkanTexture? LoadEmbeddedTexture(string fileName)
    {
        // Headless DEFAULTS to the 1x1 white substitutes: golden frames were
        // captured against them, and a texture-less gem is deterministic solid
        // vertex colour. `InitializeHeadless(useRealChartTextures: true)` opts
        // out — the sheets are embedded in this assembly and load fine here.
        if (_headlessSolidTextures) return null;

        try
        {
            var resourceName = $"DotCharter.Rendering.Assets.{fileName}";
            using var stream = typeof(Fretboard3DEngine).Assembly.GetManifestResourceStream(resourceName);
            if (stream == null)
            {
                Debug.WriteLine($"[TEXTURES] embedded resource '{resourceName}' not found.");
                return null;
            }

            using var codec = SKCodec.Create(stream);
            if (codec == null)
            {
                Debug.WriteLine($"[TEXTURES] '{fileName}' could not be decoded.");
                return null;
            }

            // Premultiplied RGBA8888 — byte-for-byte what the previous
            // Avalonia Bitmap.CopyPixels (premultiplied BGRA) plus the manual
            // R/B swap produced, so the sampled texels are unchanged.
            var info = new SKImageInfo(codec.Info.Width, codec.Info.Height,
                SKColorType.Rgba8888, SKAlphaType.Premul);
            using var bitmap = new SKBitmap(info);
            var result = codec.GetPixels(info, bitmap.GetPixels());
            if (result is not SKCodecResult.Success and not SKCodecResult.IncompleteInput)
            {
                Debug.WriteLine($"[TEXTURES] '{fileName}' decode returned {result}.");
                return null;
            }

            return _vulkanRenderer.LoadTexture((uint)info.Width, (uint)info.Height, bitmap.Bytes);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[TEXTURES] '{fileName}' failed to load: {ex.Message}");
            return null;
        }
    }

    private static (int minFret, int maxFret, Matrix4x4 mvp, float camY, float camZ) CalculateCameraMvp(
        HighwayCameraState camera, float frameTime,
        double timeMs, RenderAnchor[] anchors, int anchorCount, int width, int height, int cameraFillPercent,
        bool leftyFlip = false)
    {
        RenderAnchor? currentAnchor = null;
        RenderAnchor? nextAnchor = null;

        for (var i = anchorCount - 1; i >= 0; i--)
        {
            if (anchors[i].Time <= timeMs)
            {
                currentAnchor = anchors[i];
                break;
            }
        }

        for (var i = 0; i < anchorCount; i++)
        {
            if (anchors[i].Time > timeMs && anchors[i].Time <= timeMs + 3000)
            {
                nextAnchor = anchors[i];
                break;
            }
        }

        var minFret = currentAnchor.HasValue ? currentAnchor.Value.Fret : 1;
        var currentWidth = Math.Max(1, (int)(currentAnchor?.Width ?? 4));
        var maxFret = minFret + currentWidth - 1;

        if (nextAnchor.HasValue)
        {
            var nextWidth = Math.Max(1, (int)nextAnchor.Value.Width);
            minFret = Math.Min(minFret, nextAnchor.Value.Fret);
            maxFret = Math.Max(maxFret, nextAnchor.Value.Fret + nextWidth - 1);
        }

        if (minFret <= 0) minFret = 1;
        if (maxFret <= 0) maxFret = 4;
        if (maxFret - minFret < 3) maxFret = minFret + 3;
        maxFret = Math.Min(24, maxFret);

        var focusSpeed = 1.0f - (float)Math.Pow(1.0 - 0.7f, frameTime);
        var middleFhpPosition = (FretboardConstants.GetLinearFretPosition(maxFret) +
                                 FretboardConstants.GetLinearFretPosition(minFret - 1)) / 2f;
        var weightedPosition = FretboardConstants.GetLinearFretPosition(24) * 0.4f +
                               FretboardConstants.GetLinearFretPosition(0) * 0.6f;
        var targetCamX = middleFhpPosition * 0.9f + weightedPosition * 0.1f;

        camera.CamX = camera.CamX * (1f - focusSpeed) + targetCamX * focusSpeed;
        float targetFretSpan = maxFret - minFret + 1;
        camera.FretSpan = camera.FretSpan * (1f - focusSpeed) + targetFretSpan * focusSpeed;

        // Idle-gate seam: report whether the easing still moves, and snap the
        // asymptotic tail (sub-pixel at this epsilon) so the render loop can
        // park deterministically once the pan finishes.
        if (Math.Abs(targetCamX - camera.CamX) <= CameraSettledEpsilon &&
            Math.Abs(targetFretSpan - camera.FretSpan) <= CameraSettledEpsilon)
        {
            camera.CamX = targetCamX;
            camera.FretSpan = targetFretSpan;
            camera.IsAnimating = false;
        }
        else
        {
            camera.IsAnimating = true;
        }

        var camY = FretboardConstants.CameraBaseHeight + FretboardConstants.ChartboardYPosition +
                   (camera.FretSpan - 4f) * 0.2f;
        var camZ = FretboardConstants.CameraBaseDistance + (camera.FretSpan - 4f) * -0.2f;

        var aspectRatio = (float)((double)width / height);

        // Viewport framing: the aspect-derived scales, settled by hand.
        // Deriving
        // BOTH scales from the aspect ratio is what keeps the board stable when
        // the window height changes: resize adapts the frustum instead of
        // squishing the board (a constant vertical fill + floored X did the
        // opposite). The camera-fill knob is a pure multiplier
        // on this framing — at the default 100% the projection is
        // byte-for-byte the settled camera.
        var fill = GraphicsSettingsPolicy.ClampCameraFillPercent(cameraFillPercent) / 100f;
        var screenScaleX = Math.Min(0.5f, 1.0f / aspectRatio) * fill;
        var screenScaleY = (Math.Min(1.0f, 0.5f * aspectRatio) + 0.05f) * fill;
        // Lefty flip: a pure post-projection X mirror — the fret axis
        // reverses (Rocksmith lefty parity) while the owner-settled framing
        // stays byte-identical in magnitude. Winding-safe: both scene
        // pipelines run CullMode.None, so no front-face/pipeline change.
        if (leftyFlip) screenScaleX = -screenScaleX;

        var move = Matrix4x4.CreateTranslation(-camera.CamX, -camY, -camZ);

        var rotZ = Matrix4x4.CreateRotationZ(FretboardConstants.CameraRoll);
        var rotY = Matrix4x4.CreateRotationY(FretboardConstants.CameraYaw);
        var rotX = Matrix4x4.CreateRotationX(FretboardConstants.CameraPitch);

        float near = 0.15f, nearRight = 0.22f, nearTop = 0.22f, far = 100.0f;
        var cameraMat = new Matrix4x4(
            near / nearRight, 0, 0, 0,
            0, near / nearTop, 0, 0,
            0, 0, -(near + far) / (near - far), -1f,
            0, 0, -2 * far * near / (far - near), 0
        );

        var scaleZ = Matrix4x4.CreateScale(1, 1, -1);
        var baseCamProj = scaleZ * cameraMat;
        var screenScale = Matrix4x4.CreateScale(screenScaleX, screenScaleY, 0.1f);
        var baseMatrix = move * rotZ * rotY * rotX * baseCamProj * screenScale;

        var anchorPoint = new Vector3(camera.CamX, FretboardConstants.ChartboardYPosition, 0f);
        var clipSpace = Vector4.Transform(anchorPoint, baseMatrix);
        if (clipSpace.W != 0)
        {
            var ndcY = clipSpace.Y / clipSpace.W;
            var offsetY = -1.0f - ndcY + 0.1f;
            baseMatrix *= Matrix4x4.CreateTranslation(0, offsetY, 0);
        }

        var vulkanClip = new Matrix4x4(
            1.0f, 0.0f, 0.0f, 0.0f,
            0.0f, -1.0f, 0.0f, 0.0f,
            0.0f, 0.0f, 0.5f, 0.0f,
            0.0f, 0.0f, 0.5f, 1.0f
        );

        baseMatrix *= vulkanClip;

        return (minFret, maxFret, baseMatrix, camY, camZ);
    }
}
