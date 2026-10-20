using System.Xml.Linq;
using DotCharter.Tone.UI.ViewModels.PlayMode;

namespace DotCharter.Tests.UI;

/// <summary>
///     A STILL NEEDLE MUST SAY WHY.
///
///     <para>
///         <b>The owner bug (v0.48.5).</b> "…Tuner too" — reported alongside four
///         buttons that did nothing. The tuner itself was not broken: it polls
///         (<c>AmpFxRigView</c> has reported its surface liveness since
///         2026-08-15) and <see cref="RigTunerViewModel" /> already WORDS every
///         reason it can be blank — no transport, no capture device, a co-op seat
///         the engine is not running, or simply nothing plucked yet. The rig
///         footer drew the note, the meter and the cents and **never drew
///         <c>StatusHint</c>**, so on a machine with no input armed the whole
///         strip was a dash and a dead needle with no reason beside it. That is
///         the same lie as a permanent zero score: a capability the app cannot
///         honour must not look live.
///     </para>
/// </summary>
public sealed class RigTunerStatusTests
{
    /// <summary>
    ///     With no transport at all the poll can NEVER run, so the tick — the
    ///     only other writer of <c>StatusHint</c> — can never report it. The
    ///     reason has to be there from construction.
    /// </summary>
    [Fact]
    public void WithNoTransport_TheTunerSaysSoBeforeItHasEverTicked()
    {
        var tuner = new RigTunerViewModel(audio: null, localization: FakeLocalization.English());

        Assert.False(tuner.HasReading);
        Assert.False(tuner.IsPolling);
        Assert.Equal(FakeLocalization.English()["Tuner_NoInput"], tuner.StatusHint);
    }

    /// <summary>A transport with no capture device open is the common case on
    /// the owner's machine — the engine is there, the input is not.</summary>
    [Fact]
    public void WithNoCaptureDevice_TheTickPrintsTheReasonRatherThanAnEmptyStrip()
    {
        var audio = new FakeAudioPlaybackService { InputSampleRate = 0 };
        var tuner = new RigTunerViewModel(audio, FakeLocalization.English());

        tuner.Tick();

        Assert.False(tuner.HasReading);
        Assert.Equal(FakeLocalization.English()["Tuner_NoInput"], tuner.StatusHint);
        Assert.NotEqual(string.Empty, tuner.StatusHint);
    }

    /// <summary>…and with a device but no signal it says what to DO. Blank is
    /// never one of the answers.</summary>
    [Fact]
    public void WithADeviceButSilence_TheTunerAsksForAString()
    {
        var audio = new FakeAudioPlaybackService { InputSampleRate = 48000 };
        var tuner = new RigTunerViewModel(audio, FakeLocalization.English());

        tuner.Tick();

        Assert.False(tuner.HasReading);
        Assert.Equal(FakeLocalization.English()["Tuner_Listening"], tuner.StatusHint);
    }

    /// <summary>
    ///     THE MISSING HALF. The view model has worded these states all along;
    ///     what was absent was a <c>TextBlock</c>. Pinned as markup, and paired
    ///     with the <c>HasReading</c> gate so the reason and the cents readout
    ///     can never be on screen at once.
    /// </summary>
    [Fact]
    public void TheRigFooter_DrawsTheReasonWheneverThereIsNoReading()
    {
        var status = Named("RigTunerStatus");

        // #188: the 86 px cell shows the SHORT form, and the sentence is its tooltip
        // (it used to ellipsize the sentence to "Audio engine …" with no tooltip).
        Assert.Contains("RigTuner.StatusShort", status.Attribute("Text")?.Value ?? "");
        Assert.Contains("RigTuner.StatusHint", status.Attribute(XName.Get("ToolTip.Tip"))?.Value ?? "");
        Assert.Equal("{Binding !Rig.RigTuner.HasReading}", status.Attribute("IsVisible")?.Value);
        var inputReason = Named("RigInputChannelReason");
        Assert.Contains("InputChannel.ShortHint", inputReason.Attribute("Text")?.Value ?? "");
        Assert.Contains("InputChannel.Hint", inputReason.Attribute(XName.Get("ToolTip.Tip"))?.Value ?? "");

        // …and the cents value is the other side of that switch. Found BY NAME:
        // it used to be located as a SIBLING of RigTunerMeter, and the 2026-09-12
        // footer fix moved both readouts into the shared fixed-width
        // `RigTunerReadout` cell, which made that walk miss it. The two are still
        // required to share one parent — that shared cell is the whole point, and
        // a name lookup alone would not notice them drifting apart again.
        var cents = Named("RigTunerCents");
        Assert.Contains("RigTuner.CentsText", cents.Attribute("Text")?.Value ?? "");
        Assert.Equal("{Binding Rig.RigTuner.HasReading}", cents.Attribute("IsVisible")?.Value);
        // (`Named` re-loads the document, so compare the parent by NAME — two
        // XElements from two loads are never the same instance.)
        static string ParentName(XElement e) =>
            e.Parent!.Attributes().Single(a => a.Name.LocalName == "Name").Value;
        Assert.Equal("RigTunerReadout", ParentName(cents));
        Assert.Equal("RigTunerReadout", ParentName(status));
    }

    private static XElement Named(string name)
    {
        var path = Path.Combine(TestPaths.RepoRoot(), "DotCharter.Tone.UI", "Views", "PlayMode", "AmpFxRigView.axaml");
        var found = XDocument.Load(path).Descendants().SingleOrDefault(e =>
            e.Attributes().Any(a => a.Name.LocalName == "Name" && a.Value == name));
        Assert.True(found != null, $"'{name}' is not in AmpFxRigView.axaml");
        return found!;
    }
}
