namespace DotCharter.Tone.Audio.Engine;

/// <summary>
///     Decides when the driver's NEGOTIATED buffer size is worth telling the app
///     about, so <c>IAudioPlaybackService.DeviceBufferFramesChanged</c> fires once
///     per real change instead of once per device touch.
///     <para>
///         <b>The event it feeds was declared and never raised</b> — the
///         declaration in <c>JuceAudioPlaybackService</c> carried a
///         <c>#pragma warning disable CS0067</c>, i.e. the compiler had already
///         said so. Two surfaces subscribe: Settings ▸ Audio's buffer dropdown
///         (<c>AudioSettingsViewModel.OnDeviceBufferFramesChanged</c>, whose
///         comment names the Focusrite-ASIO case where the driver refuses the
///         requested size) and the status bar's buffer readout. Both therefore
///         showed the value the app ASKED for while the device ran at another
///         one, which is precisely what that comment says must never happen.
///     </para>
///     <para>
///         Pure and clock-free: the reporter holds one int, so
///         <see cref="TryObserve" /> is unit-tested without a device.
///     </para>
/// </summary>
public sealed class DeviceBufferReporter
{
    private int _lastReported;

    /// <summary>The size last reported, or 0 before anything was.</summary>
    public int LastReported => Volatile.Read(ref _lastReported);

    /// <summary>
    ///     True when <paramref name="frames" /> is a real, NEW size and the caller
    ///     should raise the event with it.
    ///     <para>
    ///         Zero (and anything negative) is "the device is not open" — between
    ///         engines, after a failed bring-up, on a dead ASIO handle — and must
    ///         never be reported: the subscribers snap it through
    ///         <c>NormalizeBufferSize</c>, which answers the config DEFAULT for a
    ///         non-positive input, so a transient 0 would silently rewrite the
    ///         user's buffer row.
    ///     </para>
    /// </summary>
    public bool TryObserve(int frames)
    {
        if (frames <= 0) return false;
        return Interlocked.Exchange(ref _lastReported, frames) != frames;
    }

    /// <summary>Forget the last reported size, so the next real one is reported
    /// again. Called when the engine goes away — a new device's buffer is news
    /// even when it happens to match the old one's.</summary>
    public void Forget() => Volatile.Write(ref _lastReported, 0);
}
