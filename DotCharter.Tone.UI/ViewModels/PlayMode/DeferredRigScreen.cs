namespace DotCharter.Tone.UI.ViewModels.PlayMode;

/// <summary>
///     The construction gate for an <see cref="AmpFxRigViewModel" /> — the
///     Amp &amp; FX rig SCREEN one host draws over the shared chain.
///     <para>
///         <b>Why a gate and not a lazy getter.</b> Both hosts bind the screen
///         from markup (<c>DataContext="{Binding AmpFxRig}"</c> under a hidden
///         overlay panel), and a binding READS its source when it is set up —
///         which for the shell is <c>MainWindow</c>'s bind, before the first
///         frame, and for Play Mode is the view's populate inside that same
///         phase. A <c>??=</c> getter therefore built the
///         <see cref="PedalboardViewModel" /> singleton AND two rig screens on
///         every boot for an overlay nothing shows, sampled at ~50 ms of the
///         <c>bind=</c> startup phase (R2R build). The gate answers
///         <c>null</c> until something WANTS the screen — the overlay opening,
///         or the post-first-paint prewarm drain asking for it — and the host
///         raises <c>PropertyChanged</c> so the binding re-reads.
///     </para>
///     <para>
///         The prewarm still gets a real screen to lay out: each host's
///         <c>HiddenSurfaces()</c> calls its <c>EnsureRigScreen()</c> before
///         listing the overlay, and that runs at <c>Background</c> priority
///         after the first frame, which is exactly where this cost belongs.
///     </para>
/// </summary>
internal sealed class DeferredRigScreen
{
    private AmpFxRigViewModel? _screen;
    private bool _wanted;

    /// <summary>Whether a host has asked for the screen yet.</summary>
    public bool IsWanted => _wanted;

    /// <summary>The screen if it has been built; never builds one.</summary>
    public AmpFxRigViewModel? Current => _screen;

    /// <summary>
    ///     The screen if it exists, else — only once wanted — the result of
    ///     <paramref name="build" />. Before the first <see cref="Want" /> this
    ///     is <c>null</c> and <paramref name="build" /> is never called.
    /// </summary>
    public AmpFxRigViewModel? Get(Func<AmpFxRigViewModel?> build)
    {
        if (_screen is not null) return _screen;
        if (!_wanted) return null;
        return _screen = build();
    }

    /// <summary>
    ///     Marks the screen wanted and builds it. Returns <c>true</c> when THIS
    ///     call is what made a screen available, so the host can raise the
    ///     property change that makes its binding re-read; <c>false</c> when it
    ///     already existed or <paramref name="build" /> produced nothing.
    /// </summary>
    public bool Want(Func<AmpFxRigViewModel?> build)
    {
        _wanted = true;
        if (_screen is not null) return false;
        return (_screen = build()) is not null;
    }

    /// <summary>Drops the built screen (a test seam swapping the rig underneath);
    /// whether it is wanted is untouched.</summary>
    public void Reset() => _screen = null;
}
