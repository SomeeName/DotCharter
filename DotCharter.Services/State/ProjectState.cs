using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using CommunityToolkit.Mvvm.ComponentModel;
using DotCharter.Localization;
using DotCharter.Services.Editing;

namespace DotCharter.Services.State;

public class ProjectState : ObservableObject
{
    public ChartDocumentState Document { get; } = new();
    public EditorUiState EditorUi { get; } = new();
    public PlaybackState Playback { get; } = new();
    public UserSettingsState UserSettings { get; } = new();

    /// <summary>A bare state (tests, tools): the rail starts empty.</summary>
    public ProjectState()
    {
    }

    /// <summary>The app's state: the rail opens on the localized "drop a file" hint.</summary>
    public ProjectState(ILocalizationService localization) =>
        EditorUi.MainDisplayMessage = localization["Status_DragDrop"];

    public Arrangement? ActiveArrangement
    {
        get => Document.ActiveArrangement;
        set
        {
            if (!SetStateProperty(Document.ActiveArrangement, value, v => Document.ActiveArrangement = v)) return;

            OnPropertyChanged(nameof(EventPoints));
            // The else branch is load-bearing: setting the arrangement to null
            // (or to one with no levels) would leave ActiveLevel pointing at
            // the PREVIOUS arrangement's level, and both ChartDocumentState's
            // PlacedNotes/Chords/HandShapes/Anchors and IEditingScope resolve off
            // ActiveLevel — so every edit after such a transition landed in the
            // arrangement the user had just navigated away from.
            // …and the level it adopts is the HARDEST one, not Levels[0]. On a
            // chart with dynamic difficulty, level 0 is the thinnest beginner
            // reduction — so opening or switching to an imported RS/psarc
            // arrangement dropped the editor onto a chart with most of its notes
            // missing, which reads as "the import lost my notes". Every other
            // place that has to pick THE level of a leveled arrangement already
            // picks the top: Play Mode and Live Play take Levels[^1], the
            // generator reads OrderBy(DifficultyIndex).Last() as its source and
            // deliberately lands the editor on N-1 afterwards, and the matcher's
            // no-phrase fallback takes Levels.Count - 1. MaxBy rather than [^1]
            // so a collection that is not in index order still answers correctly.
            ActiveLevel = value?.Levels.MaxBy(level => level.DifficultyIndex);

            // The string-edit mask is addressed by DIGIT KEYS (nine of them) but
            // DRAWN on the active arrangement's lanes, so it has to be narrowed
            // to the arrangement being edited: a Ctrl+6 mask set on a 6-string
            // lead survived the switch to a 4-string bass with bit 5 still lit
            // and no lane header to draw it on, and an invisible mask makes every
            // per-string verb a silent no-op. Tuning.Length is the lane count the
            // renderer, the hit tester and the lane headers all lay out from
            // (StringDigitTargeting.StringCountOf); a missing or empty tuning is
            // "unknown", never "narrow to the default six" — dropping a bit
            // requires POSITIVE knowledge that the lane is gone.
            if (value?.Tuning is { Length: > 0 } tuning)
                StringEditMask = StringEditMask.NarrowTo(tuning.Length);
        }
    }

    /// <summary>
    ///     Re-announces <see cref="ActiveArrangement" /> without changing it, so
    ///     everything bound THROUGH it re-reads.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <c>Arrangement</c> is a plain domain object — no
    ///         <c>INotifyPropertyChanged</c>, by design, because it is the model
    ///         the parsers and writers share. So a binding to
    ///         <c>ActiveArrangement.Name</c>, or to the arrangement itself through
    ///         <c>ArrangementTuningConverter</c>, has nothing to listen to when
    ///         the Arrangement Settings dialog renames or retunes it in place:
    ///         the toolbar's Tracks button, the Tracks popover's picker face and
    ///         its per-row tuning cells, and the Tools popover's drop-tune label
    ///         all kept the values the arrangement had when it was last SWITCHED
    ///         to.
    ///     </para>
    ///     <para>
    ///         One re-raise here reaches every one of them:
    ///         <c>ChartEditorViewModel</c> forwards this property through, and
    ///         <c>MultiTrackOverlayViewModel</c> already listens for it. Call it
    ///         from whatever mutated the arrangement's metadata — the Save, and
    ///         the undo/redo of that Save, both land on
    ///         <c>IChartEditorSurface.RepublishActiveArrangement</c>.
    ///     </para>
    /// </remarks>
    public void NotifyActiveArrangementChanged() => OnPropertyChanged(nameof(ActiveArrangement));

    private ChartLevel? _gameplayLevel;

    /// <summary>
    ///     During a dynamic-difficulty run, the level the 3D highway draws: per phrase, the sounds of
    /// the level being judged (<c>DynamicDifficultyComposite</c>); null otherwise. Published
    ///     whole by Live Play and read by the render thread every frame, so it is swapped, never
    ///     mutated, and it is not an observable property.
    /// </summary>
    public ChartLevel? GameplayLevel
    {
        get => Volatile.Read(ref _gameplayLevel);
        set => Volatile.Write(ref _gameplayLevel, value);
    }

    public ChartLevel? ActiveLevel
    {
        get => Document.ActiveLevel;
        set
        {
            if (!SetStateProperty(Document.ActiveLevel, value, v => Document.ActiveLevel = v)) return;

            OnPropertyChanged(nameof(PlacedNotes));
            OnPropertyChanged(nameof(PlacedChords));
            OnPropertyChanged(nameof(HandShapes));
            OnPropertyChanged(nameof(Anchors));
        }
    }

    public ChartProject? ActiveProject
    {
        get => Document.ActiveProject;
        set
        {
            if (!SetStateProperty(Document.ActiveProject, value, v => Document.ActiveProject = v)) return;
            OnPropertyChanged(nameof(Beats));
        }
    }

    public string CurrentAudioFileName
    {
        get => Document.CurrentAudioFileName;
        set => SetStateProperty(Document.CurrentAudioFileName, value, v => Document.CurrentAudioFileName = v);
    }

    public string CurrentLyricDisplay
    {
        get => EditorUi.CurrentLyricDisplay;
        set => SetStateProperty(EditorUi.CurrentLyricDisplay, value, v => EditorUi.CurrentLyricDisplay = v);
    }

    public string CurrentPhraseDisplay
    {
        get => EditorUi.CurrentPhraseDisplay;
        set => SetStateProperty(EditorUi.CurrentPhraseDisplay, value, v => EditorUi.CurrentPhraseDisplay = v);
    }

    public string CurrentProjectPath
    {
        get => Document.CurrentProjectPath;
        set => SetStateProperty(Document.CurrentProjectPath, value, v => Document.CurrentProjectPath = v);
    }

    /// <summary>See <see cref="ChartDocumentState.CurrentSongIdentity"/>.</summary>
    public string CurrentSongIdentity
    {
        get => Document.CurrentSongIdentity;
        set => SetStateProperty(Document.CurrentSongIdentity, value, v => Document.CurrentSongIdentity = v);
    }

    public string CurrentSectionDisplay
    {
        get => EditorUi.CurrentSectionDisplay;
        set => SetStateProperty(EditorUi.CurrentSectionDisplay, value, v => EditorUi.CurrentSectionDisplay = v);
    }

    public double CurrentTimeMs
    {
        get => Playback.CurrentTimeMs;
        // Hot path — fires at the audio position-update rate (250 Hz).
        // The generic SetStateProperty<T> would allocate a closure +
        // delegate (`v => Playback.CurrentTimeMs = v`) every call, on
        // the order of 100 KB/s for this single property — significant
        // contributor to the no-GC region budget exhaustion that
        // produced periodic deferred-collect dropouts. Hand-rolled
        // version below is allocation-free in steady state.
        set
        {
            // ReSharper disable once CompareOfFloatsByEqualityOperator
            if (Playback.CurrentTimeMs == value) return;
            Playback.CurrentTimeMs = value;
            OnPropertyChanged(CurrentTimeMsChangedArgs);
        }
    }

    private static readonly PropertyChangedEventArgs CurrentTimeMsChangedArgs
        = new(nameof(CurrentTimeMs));

    public bool EnableNoteHitExplosions
    {
        get => UserSettings.EnableNoteHitExplosions;
        set => SetStateProperty(UserSettings.EnableNoteHitExplosions, value,
            v => UserSettings.EnableNoteHitExplosions = v);
    }

    public bool EnableHighwayNearFade
    {
        get => UserSettings.EnableHighwayNearFade;
        set => SetStateProperty(UserSettings.EnableHighwayNearFade, value,
            v => UserSettings.EnableHighwayNearFade = v);
    }

    public bool EnableShowlightBeams
    {
        get => UserSettings.EnableShowlightBeams;
        set => SetStateProperty(UserSettings.EnableShowlightBeams, value,
            v => UserSettings.EnableShowlightBeams = v);
    }

    public bool EnableShowlightFog
    {
        get => UserSettings.EnableShowlightFog;
        set => SetStateProperty(UserSettings.EnableShowlightFog, value,
            v => UserSettings.EnableShowlightFog = v);
    }

    // ------ 0.14.2 Settings → Graphics (readability aids + effects) ---------
    // Thin TwoWay-bindable wrappers over UserSettings; persistence lives in
    // AppConfig (mirrored at startup + on Settings save).

    public bool EnableAnchorPreview
    {
        get => UserSettings.EnableAnchorPreview;
        set => SetStateProperty(UserSettings.EnableAnchorPreview, value,
            v => UserSettings.EnableAnchorPreview = v);
    }

    public bool EnableAnchorSweep
    {
        get => UserSettings.EnableAnchorSweep;
        set => SetStateProperty(UserSettings.EnableAnchorSweep, value,
            v => UserSettings.EnableAnchorSweep = v);
    }

    public int AnchorPreviewLeadMs
    {
        get => UserSettings.AnchorPreviewLeadMs;
        set => SetStateProperty(UserSettings.AnchorPreviewLeadMs, value,
            v => UserSettings.AnchorPreviewLeadMs = v);
    }

    /// <summary>Effective highway scroll speed (Rocksmith units — LOWER IS
    /// FASTER). Observable so a mid-run change repaints.</summary>
    public double HighwayScrollSpeed
    {
        get => UserSettings.HighwayScrollSpeed;
        set => SetStateProperty(UserSettings.HighwayScrollSpeed, value,
            v => UserSettings.HighwayScrollSpeed = v);
    }

    /// <summary>How far down the board the player can see, in world units.</summary>
    public double HighwayDrawDistanceZ
    {
        get => UserSettings.HighwayDrawDistanceZ;
        set => SetStateProperty(UserSettings.HighwayDrawDistanceZ, value,
            v => UserSettings.HighwayDrawDistanceZ = v);
    }

    /// <summary>3D preview camera fill (percent of the tuned default; higher =
    /// board fills more / camera closer). Observable so a live slider drag
    /// reframes the highway immediately (the render driver wakes on it).</summary>
    public int Preview3DCameraFill
    {
        get => UserSettings.Preview3DCameraFill;
        set => SetStateProperty(UserSettings.Preview3DCameraFill, value,
            v => UserSettings.Preview3DCameraFill = v);
    }

    public bool EnableBeatPulse
    {
        get => UserSettings.EnableBeatPulse;
        set => SetStateProperty(UserSettings.EnableBeatPulse, value,
            v => UserSettings.EnableBeatPulse = v);
    }

    public bool EnableDepthFog
    {
        get => UserSettings.EnableDepthFog;
        set => SetStateProperty(UserSettings.EnableDepthFog, value,
            v => UserSettings.EnableDepthFog = v);
    }

    public bool EnableVignette
    {
        get => UserSettings.EnableVignette;
        set => SetStateProperty(UserSettings.EnableVignette, value,
            v => UserSettings.EnableVignette = v);
    }

    public bool EnableEnhancedHitFeedback
    {
        get => UserSettings.EnableEnhancedHitFeedback;
        set => SetStateProperty(UserSettings.EnableEnhancedHitFeedback, value,
            v => UserSettings.EnableEnhancedHitFeedback = v);
    }

    public bool EnableCrowdBackground
    {
        get => UserSettings.EnableCrowdBackground;
        set => SetStateProperty(UserSettings.EnableCrowdBackground, value,
            v => UserSettings.EnableCrowdBackground = v);
    }

    public bool EnableHighwayLighting
    {
        get => UserSettings.EnableHighwayLighting;
        set => SetStateProperty(UserSettings.EnableHighwayLighting, value,
            v => UserSettings.EnableHighwayLighting = v);
    }

    public bool EnableBloom
    {
        get => UserSettings.EnableBloom;
        set => SetStateProperty(UserSettings.EnableBloom, value,
            v => UserSettings.EnableBloom = v);
    }

    public bool EnableStringShimmer
    {
        get => UserSettings.EnableStringShimmer;
        set => SetStateProperty(UserSettings.EnableStringShimmer, value,
            v => UserSettings.EnableStringShimmer = v);
    }

    public bool EnableHitSparks
    {
        get => UserSettings.EnableHitSparks;
        set => SetStateProperty(UserSettings.EnableHitSparks, value,
            v => UserSettings.EnableHitSparks = v);
    }

    /// <summary>Left-handed mode (v0.34, profile-level — see
    /// <see cref="UserSettingsState.LeftyFlip"/>).</summary>
    public bool LeftyFlip
    {
        get => UserSettings.LeftyFlip;
        set
        {
            SetStateProperty(UserSettings.LeftyFlip, value, v => UserSettings.LeftyFlip = v);
            OnPropertyChanged(nameof(FlipStrings));
        }
    }

    /// <summary>
    ///     THE string-orientation answer, and the only one anything should ask.
    ///     <para>
    ///         <c>IChartViewport.FlipStrings</c> is this same XOR and the 2D/3D
    ///         renderers lay their lanes out with it; exposing it here is what
    ///         lets a surface OUTSIDE a viewport — the inspector's chord box, its
    ///         per-string rows — follow the toggle instead of quietly disagreeing.
    ///         They did disagree: the chord box drew the high E on top while the
    ///         highway drew the low E there, and no toggle moved it.
    ///     </para>
    /// </summary>
    public bool FlipStrings => LeftyFlip ^ InvertStrings;

    /// <summary>Inverted string layout (tab convention — see
    /// <see cref="UserSettingsState.InvertStrings"/>). Vertical string flip in
    /// both views; an AppConfig display setting mirrored here.</summary>
    public bool InvertStrings
    {
        get => UserSettings.InvertStrings;
        set
        {
            SetStateProperty(UserSettings.InvertStrings, value, v => UserSettings.InvertStrings = v);
            OnPropertyChanged(nameof(FlipStrings));
        }
    }

    public bool EnableFilmicTonemap
    {
        get => UserSettings.EnableFilmicTonemap;
        set => SetStateProperty(UserSettings.EnableFilmicTonemap, value,
            v => UserSettings.EnableFilmicTonemap = v);
    }

    public bool Is3DPreviewVisible
    {
        get => UserSettings.Is3DPreviewVisible;
        set => SetStateProperty(UserSettings.Is3DPreviewVisible, value, v => UserSettings.Is3DPreviewVisible = v);
    }

    public KeybindConfig Keybinds
    {
        get => UserSettings.Keybinds;
        set => SetStateProperty(UserSettings.Keybinds, value, v => UserSettings.Keybinds = v);
    }

    /// <summary>
    ///     The status rail's one line. Assigning it is an
    ///     <see cref="StatusSeverity.Info" /> write — the ~225 pre-severity
    ///     callers keep compiling and keep meaning what they meant; a failure or
    ///     completion path names its level through
    ///     <see cref="SetMessage(string, StatusSeverity)" />.
    /// </summary>
    public string MainDisplayMessage
    {
        get => EditorUi.MainDisplayMessage;
        set => SetMessage(value);
    }

    /// <summary>How <see cref="MainDisplayMessage" /> should read. Always
    /// written WITH the message (see <see cref="SetMessage(string, StatusSeverity)" />),
    /// never on its own — a level with no text, or text left under a stale
    /// level, is exactly the ambiguity this exists to remove.</summary>
    public StatusSeverity MainDisplaySeverity => EditorUi.MainDisplaySeverity;

    public double PixelsPerMs
    {
        get => UserSettings.PixelsPerMs;
        set => SetStateProperty(UserSettings.PixelsPerMs, value, v => UserSettings.PixelsPerMs = v);
    }

    /// <summary>
    ///     The fret the next PLACED FHP anchor opens on — the wheel over the FHP
    ///     band arms it, the preview ghost draws it, and
    ///     <c>FretAnchorInteractionTool.AddAnchorAt</c> takes it verbatim
    ///     ("an explicit preview fret is the user having already said which fret
    ///     they want, so it always wins" — it beats `AnchorFretSuggester`, which
    ///     IS capo-aware).
    ///     <para>
    ///         Floored at <c>capo + 1</c>
    ///         (<see cref="Editing.Anchors.AnchorFretSuggester.MinFret" />). Every
    ///         writer floored at a bare <c>1</c>, so on a capo'd chart wheeling
    ///         the ghost down and clicking placed an anchor on or below the capo —
    ///         inspector rule <c>L02</c> (<c>CapoAnchorRangeRule</c>), which
    ///         `ArrangementAutoFixService` then raises silently. The keyboard
    ///         nudge beside it (`AppKeybindRegistrar.NudgeAnchor`) has always read
    ///         the capo at press time; this is the pointer half.
    ///     </para>
    ///     <para>
    ///         <c>0</c> is preserved as the "nothing armed, let the suggester
    ///         decide" sentinel every reader tests with <c>&gt; 0</c>.
    ///     </para>
    /// </summary>
    public int PreviewAnchorFret
    {
        get => EditorUi.PreviewAnchorFret;
        set => SetStateProperty(EditorUi.PreviewAnchorFret, ClampAnchorFret(value),
            v => EditorUi.PreviewAnchorFret = v);
    }

    private int ClampAnchorFret(int value)
    {
        if (value <= 0) return 0;
        var capo = Math.Max(0, ActiveArrangement?.Capo ?? 0);
        return Math.Clamp(value, Editing.Anchors.AnchorFretSuggester.MinFret(capo), 24);
    }

    /// <summary>
    ///     The fret the next CREATED sound gets — the wheel's preview ghost, the
    ///     pen's typed digit, the pointer's right-click placement and the
    ///     inspector's queue readout all read this one value.
    ///     <para>
    ///         Snapped to the frets the ACTIVE arrangement's neck actually has
    ///         (<see cref="SoundingPitch.SnapToNeck" />, legal set
    ///         <c>{0} ∪ [capo, 24]</c>). Every writer clamped to a plain
    ///         <c>0..24</c>, so on a capo-5 chart wheeling the ghost to fret 3 and
    ///         pressing a string key CREATED an <c>L01</c>
    ///         (<c>NoteFretBelowCapo</c>) — the malformed sound the app's own
    ///         validator flags and <c>FixCapoFrets</c> silently raises on the next
    ///         auto-fix. Snapping HERE rather than at
    ///         <c>ApplyNotePlacementCommand</c> keeps the ghost and the note the
    ///         same fret: a preview that advertises a fret the placement will not
    ///         use is its own bug. Capo 0 is a strict no-op.
    ///     </para>
    /// </summary>
    public int PreviewNoteFret
    {
        get => EditorUi.PreviewNoteFret;
        set => SetStateProperty(EditorUi.PreviewNoteFret,
            SoundingPitch.SnapToNeck(value, Math.Max(0, ActiveArrangement?.Capo ?? 0)),
            v => EditorUi.PreviewNoteFret = v);
    }

    /// <summary>
    /// Which strings the per-string keyboard verbs target (fret ±/set, the
    /// technique toggles, string ±). EMPTY = every string, so this is a strict
    /// no-op until the user sets a mask with <c>Ctrl+1..9</c>. Drawn on the 2D
    /// lane headers (<c>LaneLayer</c>) so it is never invisible modal state.
    /// See <see cref="Editing.StringEditMask" />.
    /// </summary>
    public StringEditMask StringEditMask
    {
        get => EditorUi.StringEditMask;
        set => SetStateProperty(EditorUi.StringEditMask, value, v => EditorUi.StringEditMask = v);
    }

    /// <summary>Flip one string in/out of <see cref="StringEditMask" />;
    /// clearing the last one returns to "all strings".</summary>
    public void ToggleStringEditMask(int stringIndex) => StringEditMask = StringEditMask.Toggle(stringIndex);

    /// <summary>Drop the mask back to "every string".</summary>
    public void ClearStringEditMask() => StringEditMask = StringEditMask.Empty;

    /// <summary>
    /// The editor's A/B loop region — see <see cref="EditorUiState.LoopRegion" />.
    /// Written only through <c>IEditorLoopService</c> (the one driver) and read
    /// by the toolbar and the 2D timeline, which DRAWS it: a loop that silently
    /// re-seeks the transport must never be invisible modal state.
    /// </summary>
    public Audio.LivePlay.LivePlayLoop? LoopRegion
    {
        get => EditorUi.LoopRegion;
        set
        {
            if (!SetStateProperty(EditorUi.LoopRegion, value, v => EditorUi.LoopRegion = v)) return;
            OnPropertyChanged(nameof(HasLoopRegion));
        }
    }

    /// <summary>Whether <see cref="LoopRegion" /> actually loops the transport.</summary>
    public bool LoopEnabled
    {
        get => EditorUi.LoopEnabled;
        set => SetStateProperty(EditorUi.LoopEnabled, value, v => EditorUi.LoopEnabled = v);
    }

    /// <summary>Loop start punched in, loop end not yet.</summary>
    public double? LoopPendingStartMs
    {
        get => EditorUi.LoopPendingStartMs;
        set => SetStateProperty(EditorUi.LoopPendingStartMs, value, v => EditorUi.LoopPendingStartMs = v);
    }

    /// <summary>The region is a one-shot audition, not a repeating loop.</summary>
    public bool LoopIsAudition
    {
        get => EditorUi.LoopIsAudition;
        set => SetStateProperty(EditorUi.LoopIsAudition, value, v => EditorUi.LoopIsAudition = v);
    }

    /// <summary>True when there is a usable loop region to toggle or draw.</summary>
    public bool HasLoopRegion => EditorUi.LoopRegion.HasValue;

    /// <summary>
    /// Follow mode for the 2D timeline viewport (0.16 viewport decoupling).
    /// ON (default): the viewport is locked to the playhead — identical to the
    /// historical behaviour. Turning it OFF freezes the view exactly where it
    /// is right now (the anchor is seeded from <see cref="CurrentTimeMs"/>) so
    /// the unlock never jumps. Deliberately not persisted — Follow starts ON.
    /// </summary>
    public bool TimelineFollowsPlayhead
    {
        get => EditorUi.TimelineFollowsPlayhead;
        set
        {
            if (EditorUi.TimelineFollowsPlayhead == value) return;
            if (!value) EditorUi.TimelineViewAnchorMs = CurrentTimeMs;
            EditorUi.TimelineFollowsPlayhead = value;
            OnPropertyChanged(TimelineFollowsPlayheadChangedArgs);
        }
    }

    private static readonly PropertyChangedEventArgs TimelineFollowsPlayheadChangedArgs
        = new(nameof(TimelineFollowsPlayhead));

    /// <summary>
    /// The pan position of the decoupled 2D timeline viewport: the chart time
    /// (ms) shown at the fixed anchor column (ChartEditorConstants.PlayheadX).
    /// Hot during wheel/middle-drag pans (~60 Hz) — hand-rolled allocation-free
    /// setter like <see cref="CurrentTimeMs"/>.
    /// </summary>
    public double TimelineViewAnchorMs
    {
        get => EditorUi.TimelineViewAnchorMs;
        set
        {
            // ReSharper disable once CompareOfFloatsByEqualityOperator
            if (EditorUi.TimelineViewAnchorMs == value) return;
            EditorUi.TimelineViewAnchorMs = value;
            OnPropertyChanged(TimelineViewAnchorMsChangedArgs);
        }
    }

    private static readonly PropertyChangedEventArgs TimelineViewAnchorMsChangedArgs
        = new(nameof(TimelineViewAnchorMs));

    /// <summary>
    /// The anchor time the main 2D renderer drew on its last frame, or
    /// <see cref="double.NaN"/> when no live frame exists (paused / not yet
    /// rendered). Written once per frame by ChartRenderer; read only through
    /// <see cref="TimelineViewAnchorEffectiveMs"/>.
    /// </summary>
    public double TimelineRenderedAnchorMs
    {
        get => EditorUi.TimelineRenderedAnchorMs;
        set => EditorUi.TimelineRenderedAnchorMs = value;
    }

    /// <summary>
    /// THE shared time-to-screen anchor for the 2D timeline: the time at the
    /// fixed anchor column. Renderer, hit-testers and every input tool must
    /// map through this one value so a decoupled (panned) viewport hit-tests
    /// exactly what it draws. Raises no change event of its own — observe
    /// <see cref="CurrentTimeMs"/> / <see cref="TimelineViewAnchorMs"/> /
    /// <see cref="TimelineFollowsPlayhead"/>.
    /// <para>
    /// While FOLLOWING it returns the anchor the renderer actually drew
    /// (<see cref="TimelineRenderedAnchorMs"/>) rather than the coalesced
    /// playhead: the renderer resolves its time from the LIVE audio clock and
    /// adds the visual lead, so the coalesced value trails it by up to ~100 ms
    /// — 100 px at 1 px/ms, roughly four note heads — for the whole duration of
    /// playback. The fallback applies only when no live frame exists, i.e. when
    /// the two agree anyway.
    /// </para>
    /// </summary>
    public double TimelineViewAnchorEffectiveMs
    {
        get
        {
            if (!EditorUi.TimelineFollowsPlayhead) return EditorUi.TimelineViewAnchorMs;
            var rendered = EditorUi.TimelineRenderedAnchorMs;
            return double.IsNaN(rendered) ? Playback.CurrentTimeMs : rendered;
        }
    }

    // Latest highway/timeline pointer time, kept up-to-date by ToolCoordinator.
    // Used as the target for "paste at mouse cursor" and similar features.
    public double LastPointerTimeMs
    {
        get => EditorUi.LastPointerTimeMs;
        set => EditorUi.LastPointerTimeMs = value;
    }

    public bool ShowWaveform
    {
        get => UserSettings.ShowWaveform;
        set => SetStateProperty(UserSettings.ShowWaveform, value, v => UserSettings.ShowWaveform = v);
    }

    /// <summary>View ▸ Show suggested hand shapes: the timeline draws
    /// <c>HandShapeSuggester</c>'s proposals as dashed bars.</summary>
    public bool ShowSuggestedHandShapes
    {
        get => UserSettings.ShowSuggestedHandShapes;
        set => SetStateProperty(UserSettings.ShowSuggestedHandShapes, value, v => UserSettings.ShowSuggestedHandShapes = v);
    }

    public double TotalAudioLengthMs
    {
        get => Document.TotalAudioLengthMs;
        set
        {
            if (SetStateProperty(Document.TotalAudioLengthMs, value, v => Document.TotalAudioLengthMs = v))
                OnPropertyChanged(nameof(EffectiveTimelineLengthMs));
        }
    }

    /// <summary>
    ///     THE navigable timeline length: the attached audio when there is one,
    ///     else the tempo grid's end. A chart-only project (GP import into the
    ///     no-audio flow, an empty project) has beats but
    ///     <see cref="TotalAudioLengthMs" /> == 0, and every seek/pan/zoom that
    ///     gated or clamped on the audio length left such a project with NO
    ///     time navigation at all — <c>ExecuteSeek</c> clamped every target to
    ///     0, which also meant selection navigation (inspector queue,
    ///     next/prev-note) selected offscreen sounds without ever bringing them
    ///     into view. Deliberately the BEAT end, not a scan over notes: this is
    ///     read per wheel notch and per seek, and the tempo grid is O(1); the
    ///     silent-transport plan (<c>SilentAudioTrack.DeriveProjectLengthMs</c>)
    ///     keeps its note-aware scan for the one-off transport length.
    ///     Raises no change event of its own beyond the audio length's —
    ///     observe <see cref="TotalAudioLengthMs" /> / the beat collection.
    /// </summary>
    public double EffectiveTimelineLengthMs
    {
        get
        {
            var audio = Document.TotalAudioLengthMs;
            if (audio > 0) return audio;
            var beats = Document.Beats;
            return beats.Count > 0 ? beats[^1].Time : 0;
        }
    }

    public WaveformSamples? WaveformData
    {
        get => Document.WaveformData;
        set => SetStateProperty(Document.WaveformData, value, v => Document.WaveformData = v);
    }

    public ObservableCollection<BeatMarker> Beats => Document.Beats;
    public ObservableCollection<EventPoint> EventPoints => Document.EventPoints;
    public ObservableCollection<GuitarNote> PlacedNotes => Document.PlacedNotes;
    public ObservableCollection<Chord> PlacedChords => Document.PlacedChords;
    public ObservableCollection<HandShape> HandShapes => Document.HandShapes;
    public ObservableCollection<FretAnchor> Anchors => Document.Anchors;

    /// <summary>
    ///     Writes the status line and how it should read, in one step.
    ///     <para>
    ///         Severity is raised BEFORE the text: a listener that refreshes on
    ///         the message notification then already sees the level that belongs
    ///         to it, and never paints new text in the previous message's colour
    ///         for one frame. A write that changes neither raises nothing.
    ///     </para>
    /// </summary>
    public void SetMessage(string message, StatusSeverity severity = StatusSeverity.Info) =>
        SetMessageCore(message, severity, null);

    /// <summary>
    ///     A message with one verb beside it on the rail ("Show"), e.g. a lossy Editor on Fire
    /// export that opens the list of what it lost. The action belongs to THIS
    ///     message: any later write, with or without an action of its own, replaces it.
    /// </summary>
    public void SetMessage(string message, StatusSeverity severity, StatusMessageAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        SetMessageCore(message, severity, action);
    }

    /// <summary>The verb offered beside <see cref="MainDisplayMessage" />, or null.</summary>
    public StatusMessageAction? MainDisplayAction { get; private set; }

    private void SetMessageCore(string message, StatusSeverity severity, StatusMessageAction? action)
    {
        var messageChanged = !string.Equals(EditorUi.MainDisplayMessage, message, StringComparison.Ordinal);
        var severityChanged = EditorUi.MainDisplaySeverity != severity;
        var actionChanged = !ReferenceEquals(MainDisplayAction, action);
        if (!messageChanged && !severityChanged && !actionChanged) return;

        EditorUi.MainDisplayMessage = message;
        EditorUi.MainDisplaySeverity = severity;
        MainDisplayAction = action;
        if (severityChanged) OnPropertyChanged(nameof(MainDisplaySeverity));
        if (actionChanged) OnPropertyChanged(nameof(MainDisplayAction));
        if (messageChanged) OnPropertyChanged(nameof(MainDisplayMessage));
    }

    private bool SetStateProperty<T>(
        T currentValue,
        T newValue,
        Action<T> assign,
        [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(currentValue, newValue)) return false;

        assign(newValue);
        OnPropertyChanged(propertyName);
        return true;
    }
}
