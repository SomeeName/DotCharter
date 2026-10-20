namespace DotCharter.Tone.Audio.Dsp.Tones;

/// <summary>
///     Where an edited Rocksmith tone is heard while it is being designed. The contract
///     every target keeps: the first <see cref="Apply" /> of a session takes whatever the
///     target must put back, every later one only replaces the tone (latest wins), and
///     <see cref="Stop" /> puts the original back — on preview off AND on close. The
///     in-house clone rig and the game itself (tonelab) are the two targets.
/// </summary>
public interface ITonePreviewTarget
{
    /// <summary>Whether this target can take a tone right now.</summary>
    bool IsAvailable { get; }

    /// <summary>A session is running: something was applied and has not been restored.</summary>
    bool IsActive { get; }

    void Apply(RsTone tone);

    /// <summary>Puts back what the session replaced; idempotent; true when there was a session to end.</summary>
    bool Stop();
}
