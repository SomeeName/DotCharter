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
    private int _active;

    internal Func<Action, Task> OffThreadRunner { get; set; } = action => Task.Run(action);

    internal Task? LastTask { get; private set; }

    public bool IsAvailable => chain is not NullDspChainService;

    public bool IsActive => Volatile.Read(ref _active) != 0;

    public DcRigPreset CurrentRig() => Volatile.Read(ref _snapshot) ?? DcRigSnapshot.FromChain(chain);

    public string ModuleName(string moduleTypeId)
        => chain.Catalog.FirstOrDefault(t => t.Id == moduleTypeId)?.DisplayName ?? moduleTypeId;

    /// <summary>Snapshots the player's rig on the first call of a session, then applies <paramref name="tone" />.</summary>
    public void Apply(RsTone tone)
    {
        if (!IsAvailable) return;
        Volatile.Write(ref _active, 1);
        // ConvertFilled: omitted params pinned to defaults, so a reused node never inherits the previous rig's values.
        var preset = _converter.ConvertFilled(tone, out _);
        var version = Interlocked.Increment(ref _version);
        LastTask = OffThreadRunner(() =>
        {
            lock (_gate)
            {
                if (version != Volatile.Read(ref _version)) return;
                // Snapshot and all native mutations share the worker gate. Keep the
                // original board through an off/on while its restore is pending.
                _snapshot ??= DcRigSnapshot.FromChain(chain);
                applier.Apply(preset);
            }
        });
    }

    /// <summary>Puts the snapshotted rig back; idempotent, and cancels any queued apply first.</summary>
    public bool Stop()
    {
        if (Interlocked.Exchange(ref _active, 0) == 0) return false;
        var version = Interlocked.Increment(ref _version);
        LastTask = OffThreadRunner(() =>
        {
            lock (_gate)
            {
                if (version != Volatile.Read(ref _version)) return;
                if (_snapshot is { } snapshot) applier.Apply(snapshot);
                if (version == Volatile.Read(ref _version)) _snapshot = null;
            }
        });
        return true;
    }
}
