using DotCharter.Tone.Audio.Dsp;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;
using DotCharter.Tone.Audio.Dsp.Tones;

namespace DotCharter.Presentation.Screens.Dialogs;

/// <summary>
///     Routes an edited Rocksmith tone through the live rig and puts the player's own
///     rig back afterwards. The snapshot is taken ONCE per preview session, so a
///     re-apply never bakes the preview into the restore point; applies run off the UI
///     thread (native chain mutation), and a restore supersedes any apply still queued.
/// </summary>
public sealed class TonePreviewSession(IDspChainService chain, DcRigApplier applier) : ITonePreviewTarget
{
    private readonly RsToneConverter _converter = new();
    private readonly object _gate = new();
    private DcRigPreset? _snapshot;
    private int _version;

    internal Func<Action, Task> OffThreadRunner { get; set; } = action => Task.Run(action);

    internal Task? LastTask { get; private set; }

    public bool IsAvailable => chain is not NullDspChainService;

    public bool IsActive => _snapshot is not null;

    public DcRigPreset CurrentRig() => _snapshot ?? DcRigSnapshot.FromChain(chain);

    public string ModuleName(string moduleTypeId)
        => chain.Catalog.FirstOrDefault(t => t.Id == moduleTypeId)?.DisplayName ?? moduleTypeId;

    /// <summary>Snapshots the player's rig on the first call of a session, then applies <paramref name="tone" />.</summary>
    public void Apply(RsTone tone)
    {
        if (!IsAvailable) return;
        // FromChain, not FromNodes: the player's scenes are part of the rig to put back.
        _snapshot ??= DcRigSnapshot.FromChain(chain);
        // ConvertFilled: omitted params pinned to defaults, so a reused node never inherits the previous rig's values.
        var preset = _converter.ConvertFilled(tone, out _);
        var version = Interlocked.Increment(ref _version);
        LastTask = OffThreadRunner(() =>
        {
            lock (_gate)
            {
                if (version == Volatile.Read(ref _version)) applier.Apply(preset);
            }
        });
    }

    /// <summary>Puts the snapshotted rig back; idempotent, and cancels any queued apply first.</summary>
    public bool Stop()
    {
        Interlocked.Increment(ref _version);
        if (_snapshot is not { } snapshot) return false;
        _snapshot = null;
        LastTask = OffThreadRunner(() =>
        {
            lock (_gate) applier.Apply(snapshot);
        });
        return true;
    }
}
