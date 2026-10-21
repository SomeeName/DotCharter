namespace DotCharter.UI.Constants;

public static class ChartEditorConstants
{
    // Hit Testing Bounds — DERIVED from the render metrics, never restated
    // (P2-6). The old literals were smaller than the drawn note (a 20x16 box
    // under a ~27 px gem; an 8 px strip under a 15 px sustain tube), so the
    // outer ring of every gem read as empty highway and the 6 px near-miss
    // backstop was doing the hit test's job. A gem's screen footprint is the
    // outer stroke circle: 2 * (BaseNoteRadius + NoteStrokeThickness / 2).
    public const double NoteHeadWidth =
        2.0 * (RenderMetrics.BaseNoteRadius + RenderMetrics.NoteStrokeThickness / 2.0);
    public const double NoteHeadHeight = NoteHeadWidth;
    public const double NoteHeadOffsetX = -NoteHeadWidth / 2.0;
    public const double NoteHeadOffsetY = -NoteHeadHeight / 2.0;

    // A chord note draws the same gem; the chord's panel column keeps its own
    // wider X tolerance below.
    public const double ChordHeadWidth = NoteHeadWidth;
    public const double ChordHeadHeight = NoteHeadHeight;
    public const double ChordHeadOffsetX = -ChordHeadWidth / 2.0;
    public const double ChordHeadOffsetY = -ChordHeadHeight / 2.0;
    public const double ChordXHitTolerance = 16.0;

    // The sustain tube draws y-SustainYOffset .. y+SustainYOffset
    // (HighwaySustainRenderer): the hit strip is exactly that.
    public const double SustainOffsetY = -RenderMetrics.SustainYOffset;
    public const double SustainHeight = RenderMetrics.SustainDrawHeight;

    /// <summary>
    ///     How far outside a note/chord HEAD a left-click still counts as
    ///     hitting it, once the exact hit test has found nothing.
    ///     <para>
    ///         Without it a near miss costs the whole selection: the click falls
    ///         through to "empty highway", which arms the marquee and then
    ///         CLEARS every selection. The log signature is
    ///         "Left-click empty highway at X; cleared selections" followed
    ///         within half a second by a successful
    ///         `HitChord=True` at the SAME snapped position. The chord was
    ///         there both times; the first click just
    ///         grazed a 24x16 head, and the punishment was losing work.
    ///     </para>
    ///     <para>
    ///         Applied ONLY as a second pass, so a click that lands inside a head
    ///         still resolves exactly as before, and among near candidates the
    ///         CLOSEST head centre wins — never "the first one found", which
    ///         would make the result depend on chart order.
    ///     </para>
    /// </summary>
    public const double NearMissHitTolerance = 6.0;

    public const double VisibleRangeBufferMs = 5000.0;

    public const double HandShapeMinHitWidth = 4.0;
    public const double HandShapeBarHeight = 16.0;

    // Navigation Intervals
    public const double SeekIntervalNormalMs = 100.0;
    public const double SeekIntervalCtrlMs = 50.0;
    public const double SeekIntervalShiftMs = 500.0;
    public const double SeekNextPrevBufferMs = 5.0;

    // Transport Speed Config.
    //
    // Named by SIZE, not by modifier: a name like ...Ctrl or ...Shift (after the
    // DEFAULT MODIFIER of the keybind it is meant for) would let
    // AppKeybindRegistrar match SpeedUpFast (Ctrl +) to
    // ...Ctrl and SpeedUpFine (Shift +) to ...Shift — so the row labelled "coarse"
    // would nudge by 0.01 and the one labelled "fine" jump by 0.25, in all four
    // directions. A constant named after a chord invites exactly that.
    //
    // Nothing here may be finer than TransportViewModel.PlaybackSpeed's stored
    // resolution (two decimals) — a step below it rounds away and the keybind
    // does nothing at all, which is what 0.01 did against the old 0.05 grid.
    public const double PlaybackSpeedIncrementNormal = 0.05;
    public const double PlaybackSpeedIncrementFine = 0.01;
    public const double PlaybackSpeedIncrementCoarse = 0.25;
    public const double PlaybackSpeedMin = 0.25;
    public const double PlaybackSpeedMax = 2.0;

    // Viewport & Scroll
    public const double ZoomInFactor = 1.15;
    public const double ZoomOutFactor = 0.85;
    public const double ScrollSeekMs = 500.0;
    public const double PlayheadX = 450.0;

    // 3D Preview
    public const double Preview3DLookBehindMs = 1000.0;

    /// <summary>Floor for how far ahead the snapshot projects. The ACTUAL
    /// look-ahead is the larger of this and the highway's visible horizon
    /// (HighwaySpeedPolicy.HorizonMs, which the user controls and which reaches
    /// 12 s) — a fixed window would stop feeding notes the board can still
    /// show.</summary>
    public const double Preview3DLookAheadMs = 4000.0;

    /// <summary>Slack beyond the horizon so a note is projected slightly before
    /// it needs to be drawn, rather than exactly on the frame it appears.</summary>
    public const double Preview3DLookAheadMarginMs = 500.0;

    // Rendering Metrics
    public static class RenderMetrics
    {
        // Global / Renderer
        public const float DefaultFontSize = 14.0f;
        // The bottom band stacks TWO independent lanes above the time ruler
        // (which owns the bottom ~20px: ticks at H-20, labels at H-18):
        //   handshape bar  at laneBottom + HandShapeBarYOffset (height HandShapeActiveBarHeight)
        //   FHP / anchors  at laneBottom + FhpYOffset          (height AnchorTailHeight)
        // They must not share ONE Y: at the same offset the 18px FHP draws on top of
        // the 12px handshape bar — overlapping visuals AND overlapping hit
        // regions that make handshapes uneditable.
        // HighwayBottomMargin is sized so the lower FHP lane still clears the
        // ruler; keep the three in sync if any height changes.
        public const float HandShapeBarYOffset = 2.0f;
        public const float FhpYOffset = HandShapeBarYOffset + HandShapeActiveBarHeight + 4.0f;
        public const float FpsLabelXOffset = 60.0f;
        public const float FpsLabelYOffset = 10.0f;
        public const float SdfFeathering = 3.0f;

        // NoteHeadRenderer & Notes
        public const float BaseNoteRadius = 12.0f;
        public const float NoteStrokeThickness = 2.0f;
        public const float SelectedNoteStrokeThickness = 3.0f;
        public const float BendStartYOffset = 7.5f;
        public const float BendTailHeight = 15.0f;
        public const float MaxBendSteps = 6.0f;
        public const float HarmonicOuterSize = 16.0f;
        public const float HopoHeightScale = 0.4f;
        public const float HopoWidthScale = 0.5f;
        public const float MuteIconScale = 0.7f;
        public const float BendTextScale = 10f / 14f;
        public const float DefaultTextScale = 12f / 14f;

        // Chords
        public const float ChordPanelWidth = 22.0f;
        public const float ChordPanelOffset = 11.0f;

        // SustainRenderer
        public const float SustainYOffset = 7.5f;
        public const float SustainDrawHeight = 15.0f;
        public const float SustainNoteRadius = 13.0f;
        public const float VibratoStepPx = 3.0f;
        public const float VibratoHeight = 7.5f;
        public const float VibratoFrequency = 0.4f;
        public const float TremoloStepPx = 6.0f;
        public const float TremoloHeight = 4.0f;

        // EventLayer
        // NOTE: Row Y-positions (ToneY / SectionY / PhraseY / LyricY) moved
        // to RibbonLayout — they are now computed per-track from the
        // visible rows instead of being fixed constants. `BadgeHeight` lives
        // there too (as `RibbonLayout.BarHeight`): a bar's height and
        // the lane pitch it has to fit inside are ONE decision, and split
        // across two files the ratio drifts.
        public const float EventLineDashLength = 6.0f;
        public const float EventLineGapLength = 4.0f;
        public const float EventLineThickness = 1.5f;
        public const float PlayheadThickness = 2.0f;
        public const float ActiveLabelFixedX = 4.0f;
        /// <summary>Slack in SCREEN PIXELS around the viewport for the event
        /// layer's badges, which draw well to the left/right of their own time.
        /// Not named "…Ms" although it is compared against screen X — the
        /// trap being that its neighbour <see cref="CullingBufferMs" /> really
        /// is milliseconds, so tuning "the ms buffer" would silently move a pixel
        /// margin.</summary>
        public const float EventCullBufferPx = 100.0f;

        // BackgroundLayer
        public const double ZoomThresholdDetailed = 0.05;

        /// <summary>Slack in MILLISECONDS around the visible time range —
        /// compared against times, never against screen X. See
        /// <see cref="EventCullBufferPx" /> for the pixel-space counterpart.</summary>
        public const double CullingBufferMs = 100.0;
        public const float GuideLineSpacing = 24.0f;
        public const int MaxGuideLines = 4;
        public const float MeasureLineWidth = 2.0f;
        public const float StandardLineWidth = 1.0f;
        public const float DashLength = 4.0f;
        public const float GapLength = 4.0f;
        public const int MinimumGridDivisorForSubdivisions = 1;
        // Sub-pixel gridline thinning: a beat/measure line closer
        // than this to the last drawn one is skipped — below ~2px the
        // lines fuse into a solid wash while still costing an AA quad each.
        public const float MinGridLineSpacingPx = 2.0f;
        // Grid hierarchy fade ramp (0.14.3 B): lines spaced between
        // MinGridLineSpacingPx and this fade out linearly instead of popping
        // at the skip threshold, so measures > beats > subdivisions keeps
        // reading while zooming out. Extends the 2px-skip logic above.
        public const float GridLineFadeStartPx = 7.0f;
        // Width ranks for the three grid line classes (measure keeps
        // MeasureLineWidth): a beat sits visibly between measure and
        // subdivision when the 0.14.3 polish pass is enabled.
        public const float BeatLineWidth = 1.25f;
        public const float SubdivisionLineWidth = 0.8f;

        // Note LOD thresholds (0.19 B1), in pixels of timeline per note
        // column (a note or a whole chord = one column). Full gems are
        // ~24px wide; below ~18px/column the fret labels overlap into mush,
        // below ~6px/column the gems themselves do.
        public const float NoteLodCompactMaxPxPerColumn = 18.0f;
        public const float NoteLodMarksMaxPxPerColumn = 6.0f;
        // Hysteresis band around both thresholds, as a fraction of the
        // threshold: a frame keeps its previous level until px/column moves
        // this far past the line. Scrolling alone changes the visible column
        // count by one (a note enters right before or after one leaves left),
        // i.e. by 1/N; at the thresholds on a 900 px view that is 0.7 % (Marks)
        // and 2 % (Compact), and 9 % on a 200 px one — all well inside 15 %.
        public const float NoteLodHysteresis = 0.15f;
        // Mark geometry for the Marks LOD (minimap-style density marks).
        public const float NoteLodMarkWidth = 3.0f;
        public const float NoteLodMarkHeight = 9.0f;
        public const float NoteLodSustainStripHeight = 2.0f;

        // CursorLayer
        public const float CursorDashLength = 6.0f;
        public const float CursorGapLength = 4.0f;
        public const float CursorThickness = 1.5f;

        // HandShapeLayer
        public const double AnchorTailMaxMs = 5000.0;
        public const float AnchorTailHeight = 18.0f;
        public const float AnchorLineThickness = 2.0f;
        public const float HandShapeActiveBarHeight = 12.0f;
        public const float HandShapeSelectedThickness = 2.5f;
        public const float HandShapeNormalThickness = 1.5f;
        // The name printed inside a bar: 10 px on the 14 px atlas, so the
        // glyphs (descenders included) stay inside the 12 px bar.
        public const float HandShapeNameTextScale = 10f / 14f;

        // WaveformLayer
        public const double WaveformSampleMs = 20.0;
        public const float WaveformHeightScale = 0.8f;
        public const float WaveformMaxStroke = 15.0f;
    }
}
