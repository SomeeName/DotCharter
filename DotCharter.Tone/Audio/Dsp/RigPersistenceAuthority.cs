namespace DotCharter.Tone.Audio.Dsp;

internal enum RigLiveRig { Empty, Unchanged, Changed }
internal enum RigReadAuthority { Live, Unreadable, Incomplete }
internal enum RigPersistenceEvent
{
    Edit, ObserveUnchanged, ObserveEmpty, PublishSucceeded, PublishFailed, Flush, Persist,
    Rebuild, RestoreSucceeded, RestoreUnpublished, RestoreFailedOps, StoreUnreadable,
    ExplicitPresetReplacement, ProfileSeatSwitch, Shutdown
}

[Flags]
internal enum RigPersistenceAction
{
    None = 0, FlushPending = 1, Snapshot = 2, RetryRead = 4, Publish = 8, Restore = 16, PreserveRecovery = 32
}

/// <summary>
/// Pure authority reducer. A known persisted signature certifies only an unchanged
/// live rig; an unpublished write invalidates that certificate even when the player
/// later returns to the old signature. Incomplete application protects the full saved
/// rig until an explicit replacement also preserves it in the same store publication.
/// </summary>
internal readonly record struct RigPersistenceAuthority(
    bool HasPersistedSignature, RigLiveRig LiveRig, bool HasPendingPublication, RigReadAuthority ReadAuthority)
{
    internal (RigPersistenceAuthority State, RigPersistenceAction Action) Decide(RigPersistenceEvent input)
    {
        var state = this;
        var action = RigPersistenceAction.None;
        switch (input)
        {
            case RigPersistenceEvent.Edit:
                state = this with { LiveRig = RigLiveRig.Changed };
                break;
            case RigPersistenceEvent.ObserveUnchanged:
                state = this with { LiveRig = HasPersistedSignature ? RigLiveRig.Unchanged : RigLiveRig.Changed };
                break;
            case RigPersistenceEvent.ObserveEmpty:
                state = this with { LiveRig = RigLiveRig.Empty };
                break;
            case RigPersistenceEvent.PublishSucceeded:
                return PublicationResult(succeeded: true);
            case RigPersistenceEvent.PublishFailed:
                return PublicationResult(succeeded: false);
            case RigPersistenceEvent.Flush:
            case RigPersistenceEvent.Shutdown:
                action = RigPersistenceAction.FlushPending;
                if (ReadAuthority == RigReadAuthority.Live) action |= RigPersistenceAction.Snapshot;
                if (ReadAuthority == RigReadAuthority.Unreadable) action |= RigPersistenceAction.RetryRead;
                break;
            case RigPersistenceEvent.Persist:
                action = PersistenceAction();
                break;
            case RigPersistenceEvent.Rebuild:
                state = this with { LiveRig = RigLiveRig.Empty };
                action = RigPersistenceAction.Restore;
                break;
            case RigPersistenceEvent.RestoreSucceeded:
                state = new(true, RigLiveRig.Unchanged, false, RigReadAuthority.Live);
                break;
            case RigPersistenceEvent.RestoreUnpublished:
                state = new(false, RigLiveRig.Changed, true, RigReadAuthority.Live);
                break;
            case RigPersistenceEvent.RestoreFailedOps:
                state = this with { HasPersistedSignature = false, ReadAuthority = RigReadAuthority.Incomplete };
                break;
            case RigPersistenceEvent.StoreUnreadable:
                state = this with { HasPersistedSignature = false, ReadAuthority = RigReadAuthority.Unreadable };
                break;
            case RigPersistenceEvent.ExplicitPresetReplacement:
                return ReplacePreset();
            case RigPersistenceEvent.ProfileSeatSwitch:
                state = new(false, RigLiveRig.Empty, false, RigReadAuthority.Unreadable);
                action = RigPersistenceAction.Restore;
                break;
        }
        return (state, action);
    }

    private bool CanPublish => ReadAuthority == RigReadAuthority.Live && LiveRig != RigLiveRig.Empty;

    private (RigPersistenceAuthority State, RigPersistenceAction Action) PublicationResult(bool succeeded)
    {
        if (!CanPublish) return (this, RigPersistenceAction.None);
        return (this with
        {
            HasPersistedSignature = succeeded,
            LiveRig = succeeded ? RigLiveRig.Unchanged : RigLiveRig.Changed,
            HasPendingPublication = !succeeded
        }, RigPersistenceAction.None);
    }

    private RigPersistenceAction PersistenceAction()
        => CanPublish && (!HasPersistedSignature || LiveRig == RigLiveRig.Changed || HasPendingPublication)
            ? RigPersistenceAction.Publish : RigPersistenceAction.None;

    private (RigPersistenceAuthority State, RigPersistenceAction Action) ReplacePreset()
    {
        if (ReadAuthority == RigReadAuthority.Unreadable || LiveRig == RigLiveRig.Empty)
            return (this, RigPersistenceAction.None);
        return (this with { HasPersistedSignature = false, LiveRig = RigLiveRig.Changed, ReadAuthority = RigReadAuthority.Live },
            ReadAuthority == RigReadAuthority.Incomplete ? RigPersistenceAction.PreserveRecovery : RigPersistenceAction.None);
    }
}
