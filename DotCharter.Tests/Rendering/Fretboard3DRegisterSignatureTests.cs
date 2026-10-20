using DotCharter.Rendering.Data;
using DotCharter.Rendering.Data.Render;
using DotCharter.Rendering.Engines;

namespace DotCharter.Tests.Rendering;

/// <summary>
///     0.14 P6 — the 3D port of the 2D register-signature skip. The contract:
///     an unchanged visible text set hashes identically frame-to-frame (so
///     per-char font-atlas probes are skipped), while any count change or
///     string-INSTANCE replacement (how immutable-string edits manifest in the
///     snapshot) changes the signature.
/// </summary>
public class Fretboard3DRegisterSignatureTests
{
    private static FrameSnapshot Snapshot()
    {
        var s = new FrameSnapshot
        {
            DebugText = "60 fps",
            LyricWords = new RenderLyricWord[4],
            LyricWordCount = 2,
            EventPoints = new RenderEventPoint[4],
            EventPointCount = 1
        };
        s.LyricWords[0].Text = "hello";
        s.LyricWords[1].Text = "world";
        s.EventPoints[0].Phrase = "verse";
        s.EventPoints[0].Section = "intro";
        return s;
    }

    [Fact]
    public void UnchangedSnapshot_HashesIdentically()
    {
        var s = Snapshot();
        Assert.Equal(
            Fretboard3DEngine.ComputeRegisterSignature(s),
            Fretboard3DEngine.ComputeRegisterSignature(s));
    }

    [Fact]
    public void ReplacedLyricInstance_ChangesTheSignature_EvenWithEqualContent()
    {
        var s = Snapshot();
        var before = Fretboard3DEngine.ComputeRegisterSignature(s);
        // Same characters, new instance — exactly what an in-place text edit
        // produces (and what the old count/time proxies missed in 2D).
        s.LyricWords[1].Text = new string("world".ToCharArray());
        Assert.NotEqual(before, Fretboard3DEngine.ComputeRegisterSignature(s));
    }

    [Fact]
    public void LyricCountChange_ChangesTheSignature()
    {
        var s = Snapshot();
        var before = Fretboard3DEngine.ComputeRegisterSignature(s);
        s.LyricWords[2].Text = "again";
        s.LyricWordCount = 3;
        Assert.NotEqual(before, Fretboard3DEngine.ComputeRegisterSignature(s));
    }

    [Fact]
    public void EventPhraseOrSectionInstanceChange_ChangesTheSignature()
    {
        var s = Snapshot();
        var before = Fretboard3DEngine.ComputeRegisterSignature(s);
        s.EventPoints[0].Phrase = new string("verse".ToCharArray());
        var afterPhrase = Fretboard3DEngine.ComputeRegisterSignature(s);
        Assert.NotEqual(before, afterPhrase);

        s.EventPoints[0].Section = new string("intro".ToCharArray());
        Assert.NotEqual(afterPhrase, Fretboard3DEngine.ComputeRegisterSignature(s));
    }

    [Fact]
    public void DebugTextInstanceChange_ChangesTheSignature()
    {
        var s = Snapshot();
        var before = Fretboard3DEngine.ComputeRegisterSignature(s);
        s.DebugText = new string("60 fps".ToCharArray());
        Assert.NotEqual(before, Fretboard3DEngine.ComputeRegisterSignature(s));
    }

    [Fact]
    public void EntriesBeyondTheWindowCounts_AreIgnored()
    {
        var s = Snapshot();
        var before = Fretboard3DEngine.ComputeRegisterSignature(s);
        // Pooled arrays keep stale entries past the count — they are not part
        // of the visible set and must not perturb the signature.
        s.LyricWords[3].Text = "stale";
        s.EventPoints[2].Phrase = "stale";
        Assert.Equal(before, Fretboard3DEngine.ComputeRegisterSignature(s));
    }
}
