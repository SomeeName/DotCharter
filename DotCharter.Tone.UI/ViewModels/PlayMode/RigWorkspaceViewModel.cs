// RigWorkspaceViewModel: the height of the rig's WORKSPACE — the one host below the routing canvas that holds the module editor and the tone drawer.

using DotCharter.Tone.UI.ViewModels.Shell;

namespace DotCharter.Tone.UI.ViewModels.PlayMode;

/// <summary>
///     The pixel height of everything below the routing canvas, persisted as
///     <c>AppConfig.RigWorkspaceHeight</c>. Owned by <see cref="PedalboardViewModel" />
///     (exposed as <see cref="PedalboardViewModel.Workspace" />) because both rig
///     hosts are the same control over that one singleton.
/// </summary>
public sealed class RigWorkspaceViewModel : ViewModelBase
{
    /// <summary>
    ///     The workspace's floor. 260 px is a drawer head plus one row of tone
    ///     cards, and it is also the module editor's floor — below this the
    ///     browser is not a browser and the editor is a title bar.
    /// </summary>
    public const double MinHeight = 260;

    /// <summary>The ceiling of the workspace — the STAGE above the routing map
    /// (AmpliTube-style layout), which should grow with a tall
    /// screen. The map row's own <c>MinHeight</c> is what keeps a drag from pushing
    /// the map off the bottom; this only bounds a hand-edited config.</summary>
    public const double MaxHeight = 1600;

    /// <summary>
    ///     The workspace's height before the divider has ever been dragged.
    ///     <para>
    ///         CONTENT-INDEPENDENT ON PURPOSE. A <c>2.2*</c> row for the module
    ///         editor and a pixel height for the drawer would mean that
    ///         the space below the canvas — and therefore
    ///         the canvas — changes size the moment a block is selected. One
    ///         number for the whole region is what makes the canvas fixed. 480
    ///         is the value the drawer shipped with and fits its rail plus two
    ///         card rows; it is not a share of the window, and must not become
    ///         one.
    ///     </para>
    /// </summary>
    public const double DefaultHeight = 540;

    private readonly IRigConfigService? _config;
    private double? _height;
    private bool _hasUserHeight;

    public RigWorkspaceViewModel(IRigConfigService? config) => _config = config;

    /// <summary>
    ///     True once the divider has actually been dragged (or a drag was
    ///     restored from config). Until then the VIEW picks the height, from the
    ///     breakpointed <c>FireRigWorkspaceHeight</c> token — a 480 px default
    ///     baked in here would be the rig's "design size in a corner" on a 4K
    ///     screen, and the editor faces cannot scale into a row that never grew.
    ///     <see cref="Height" /> still answers a usable number for a
    ///     host with no resources at all.
    /// </summary>
    public bool HasUserHeight
    {
        get
        {
            _ = Height; // forces the one-time load
            return _hasUserHeight;
        }
    }

    /// <summary>
    ///     The pixel height of everything below the routing canvas — the module
    ///     editor dock and the tone-library drawer both live in it, so there is
    ///     one divider and one number.
    ///     <para>
    ///         It lives on the RIG, not on the view, because both hosts (Play
    ///         Mode's rig screen and the chart editor's overlay) are the same
    ///         control over this one singleton — a height held in the view would
    ///         be two different heights depending on which host you opened last.
    ///         Persisted as <c>AppConfig.RigWorkspaceHeight</c>, clamped to
    ///         <see cref="MinHeight" />..<see cref="MaxHeight" />
    ///         on set AND on load: a hand-edited config must not be able to
    ///         squeeze the canvas to nothing.
    ///     </para>
    /// </summary>
    public double Height
    {
        get => _height ??= LoadPersistedHeight();
        set
        {
            var clamped = Math.Clamp(value, MinHeight, MaxHeight);
            if (_height is { } current && Math.Abs(current - clamped) < 0.5
                && _hasUserHeight)
            {
                return;
            }

            _height = clamped;
            _hasUserHeight = true;
            OnPropertyChanged(nameof(Height));
            OnPropertyChanged(nameof(HasUserHeight));
            PersistHeight(clamped);
        }
    }

    private double LoadPersistedHeight()
    {
        if (_config == null)
        {
            return DefaultHeight;
        }

        try
        {
            var cfg = _config.LoadRigConfig();
            // A null field means "never dragged" — NOT zero. Reading it as a
            // plain double would collapse the workspace on its first open.
            // RigToneBrowserHeight is the pre-W1 key and is the seed when the
            // new one is absent, so an existing install keeps its split.
            var h = cfg.RigWorkspaceHeight ?? cfg.RigToneBrowserHeight;
            if (h is not { } px)
            {
                return DefaultHeight;
            }

            _hasUserHeight = true;
            return Math.Clamp(px, MinHeight, MaxHeight);
        }
        catch
        {
            return DefaultHeight;
        }
    }

    private void PersistHeight(double value)
    {
        if (_config == null)
        {
            return;
        }

        try
        {
            // The view commits on DragCompleted, not per pointer frame, so this
            // is a handful of writes a session — and the config service
            // coalesces the FILE write anyway. The guard drops the no-op
            // serialize a re-open with an unchanged height would cause.
            if (_config.LoadRigConfig().RigWorkspaceHeight is { } current && Math.Abs(current - value) < 0.5)
            {
                return;
            }

            _config.UpdateRigConfig(c => c.RigWorkspaceHeight = value);
        }
        catch
        {
            /* a divider height is cosmetic — never let a config write break the rig */
        }
    }
}
