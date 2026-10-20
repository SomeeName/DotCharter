using DotCharter.Rendering.Data;
using DotCharter.Rendering.Engines;

namespace DotCharter.Tests.Rendering;

/// <summary>
///     The 3D HUD's tone badge (`HudComponent.RenderActiveTone`) is the only
///     surface that draws <see cref="FrameSnapshot.ActiveToneName" />, and
///     nothing on the 3D side ever registered its characters into the font
///     atlas.
///     <para>
///         The atlas is seeded with ASCII <c>' '..'~'</c> only, and
///         <c>TextBatcher</c> SKIPS a character <c>TryGetGlyph</c> does not
///         know — silently, with no fallback glyph. So a user-supplied preset
///         name ("Röhren Crunch", a TONE3000 download, a Japanese rig name) lost
///         exactly the characters that make it that name. The 2D twin registers
///         tones explicitly (`ChartRenderer` + `ChartTextPreRegistration`, whose
///         own comment records that the badges "were never registered"); the 3D
///         port of that pass was written without them.
///     </para>
///     <para>
///         Asserted through <c>Fretboard3DEngine.ComputeRegisterSignature</c>,
///         which is the GATE: the per-frame registration block runs only when the
///         signature moves, so a name the signature cannot see is a name whose
///         glyphs are never queued — an in-run tone switch would have been
///         invisible even if the load-time pass had covered it. (The registration
///         itself needs a real atlas, i.e. a GPU; the gate does not.)
///     </para>
/// </summary>
public sealed class HudToneGlyphRegistrationTests
{
    [Fact]
    public void Changing_the_active_tone_changes_the_register_signature()
    {
        var snapshot = Snapshot();
        var before = Fretboard3DEngine.ComputeRegisterSignature(snapshot);

        snapshot.ActiveToneName = "Röhren Crunch";

        Assert.NotEqual(before, Fretboard3DEngine.ComputeRegisterSignature(snapshot));
    }

    [Fact]
    public void A_replaced_instance_of_the_same_tone_name_still_moves_the_signature()
    {
        // Same rule the lyric and event strings follow: an in-place edit
        // manifests as a new string INSTANCE, and the signature is a cheap
        // reference hash rather than a per-character atlas probe.
        var snapshot = Snapshot();
        snapshot.ActiveToneName = "Clean";
        var before = Fretboard3DEngine.ComputeRegisterSignature(snapshot);

        snapshot.ActiveToneName = new string("Clean".ToCharArray());

        Assert.NotEqual(before, Fretboard3DEngine.ComputeRegisterSignature(snapshot));
    }

    [Fact]
    public void An_unchanged_snapshot_still_hashes_identically()
    {
        // The gate exists to SKIP work; adding the tone must not defeat it.
        var snapshot = Snapshot();
        snapshot.ActiveToneName = "Clean";

        Assert.Equal(
            Fretboard3DEngine.ComputeRegisterSignature(snapshot),
            Fretboard3DEngine.ComputeRegisterSignature(snapshot));
    }

    private static FrameSnapshot Snapshot() => new()
    {
        DebugText = "60 fps",
        ActiveToneName = ""
    };
}
