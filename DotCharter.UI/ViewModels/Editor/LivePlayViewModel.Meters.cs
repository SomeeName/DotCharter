// LivePlayViewModel: the gate-taking seam to the performance-stake meters (LivePlayMetersViewModel) - arm, disarm and the fail mark.

namespace DotCharter.UI.ViewModels.Editor;

/// <summary>
///     The shell's door to the performance-stake meters. The meters themselves
///     (state, HUD properties, the fail latch and its event) are
///     <see cref="LivePlayMetersViewModel" />, exposed as <see cref="Meters" />;
///     what stays here is what only this type can do: take <c>_sessionGate</c>,
///     which serializes every meter call against the worker tick that feeds the
///     meters each judged verdict, and refuse to arm without an active session.
///     <para>
///         Part of <see cref="LivePlayViewModel" />. The type's fields, its
///         whole bound surface and its constructor stay in the ROOT file, so
///         "where does this come from" is one look rather than a search.
///     </para>
/// </summary>
public partial class LivePlayViewModel
{
    /// <summary>Marks the CURRENT run as ended by the rock meter's fail-out —
    /// call BEFORE deactivating the session, or the record misses the mark.</summary>
    public void MarkRunFailed()
    {
        lock (_sessionGate)
        {
            Meters.MarkRunFailed();
        }
    }

    /// <summary>Arms performance stakes for the CURRENT session: a fresh
    /// meter anchored at the playhead. No-op without an active session.</summary>
    public void ArmRockMeter()
    {
        lock (_sessionGate) // the worker feeds the meter every judged verdict
        {
            if (!_session.IsActive)
            {
                return;
            }

            Meters.ArmRockMeter(_projectState.CurrentTimeMs);
        }
    }

    /// <summary>Takes the stakes off — mid-run (Riff Repeater) or on any run
    /// end. Idempotent.</summary>
    public void DisarmRockMeter()
    {
        lock (_sessionGate)
        {
            Meters.DisarmRockMeter();
        }
    }

    /// <summary>Arms the crowd HUD for the CURRENT session: a fresh meter at
    /// neutral, anchored at the playhead. No-op without an active session.</summary>
    public void ArmCrowdMeter()
    {
        lock (_sessionGate) // the worker feeds the meter every judged verdict
        {
            if (!_session.IsActive)
            {
                return;
            }

            Meters.ArmCrowdMeter(_projectState.CurrentTimeMs);
        }
    }

    /// <summary>Takes the crowd HUD off. Idempotent; called on every run exit.</summary>
    public void DisarmCrowdMeter()
    {
        lock (_sessionGate)
        {
            Meters.DisarmCrowdMeter();
        }
    }
}
