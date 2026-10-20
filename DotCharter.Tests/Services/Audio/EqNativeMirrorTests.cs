using System.Globalization;
using System.Text.RegularExpressions;
using DotCharter.Tone.Audio.Equalizer;
using JuceSharp;

namespace DotCharter.Tests.Services.Audio;

/// <summary>
///     The ABI v29 EQ is clamped on BOTH sides of the boundary, and the two sides
///     have to agree: a band the engine silently moved is a band whose DRAWN curve
///     and AUDIBLE curve disagree, which is the one fault the whole
///     <c>Audio/Equalizer/</c> folder exists to prevent.
///     <para>
///         <b>The guides said this could not be tested</b> — "no test can assert an
///         agreement across two languages, so both sides carry a comment naming the
///         other". That was never true here: the C++ side is a submodule of this
///         repository, so the constants and the enum ordering can simply be read.
///         A pair of comments is a convention; this is a gate.
///     </para>
///     <para>
///         Only the numbers that CROSS the boundary are asserted. The native file
///         is parsed with narrow anchored patterns, so a rename on either side
///         fails loudly rather than matching something else.
///     </para>
/// </summary>
public sealed class EqNativeMirrorTests
{
    [Fact]
    public void EqLimits_MirrorsTheNativeClampRangesExactly()
    {
        var header = NativeSource(Path.Combine("src", "AudioEngine.h"));

        Assert.Equal(EqLimits.MinFrequencyHz, NativeFloat(header, "kEqMinFreqHz"));
        Assert.Equal(EqLimits.MaxFrequencyHz, NativeFloat(header, "kEqMaxFreqHz"));
        Assert.Equal(EqLimits.MinQ, NativeFloat(header, "kEqMinQ"));
        Assert.Equal(EqLimits.MaxQ, NativeFloat(header, "kEqMaxQ"));
        Assert.Equal(EqLimits.MaxGainDb, NativeFloat(header, "kEqMaxGainDb"));
        Assert.Equal(EqLimits.MaxTrimDb, NativeFloat(header, "kEqMaxTrimDb"));
    }

    /// <summary>
    ///     Three places count the bands and the badge reads "n/6" because of it:
    ///     the ABI define, the wrapper's constant and <see cref="EqLimits" />.
    /// </summary>
    [Fact]
    public void TheBandCount_AgreesAcrossTheAbi_TheWrapper_AndEqLimits()
    {
        var abi = NativeSource(Path.Combine("include", "dotcharter_audio.h"));

        var match = Regex.Match(abi, @"#define\s+DCA_EQ_BAND_COUNT\s+(\d+)");
        Assert.True(match.Success, "DCA_EQ_BAND_COUNT is gone from the ABI header — has the EQ been renumbered?");

        var native = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        Assert.Equal(EqLimits.BandCount, native);
        Assert.Equal(JuceAudioEngine.EqBandCount, native);
    }

    /// <summary>
    ///     <see cref="EqBandType" /> is cast STRAIGHT across to
    ///     <c>dca_eq_band_type</c> (and <see cref="EqRoute" /> to
    ///     <c>dca_eq_route</c>) by <c>PushEqTo</c>, so a renumber on either side
    ///     silently turns every user's bell into a low pass and every drawn curve
    ///     into a lie. Both headers say "append, never renumber"; this is what
    ///     makes that a rule rather than a hope.
    /// </summary>
    [Theory]
    [InlineData(nameof(EqBandType), "dca_eq_band_type", "DCA_EQ_HIGH_PASS,DCA_EQ_LOW_SHELF,DCA_EQ_BELL,DCA_EQ_HIGH_SHELF,DCA_EQ_LOW_PASS,DCA_EQ_NOTCH")]
    [InlineData(nameof(EqRoute), "dca_eq_route", "DCA_EQ_ROUTE_SONG,DCA_EQ_ROUTE_INPUT,DCA_EQ_ROUTE_BOTH")]
    public void TheManagedEnums_HaveTheSameValuesAsTheirAbiCounterparts(
        string managedEnumName, string nativeEnumName, string nativeMembersInOrder)
    {
        var abi = NativeSource(Path.Combine("include", "dotcharter_audio.h"));
        var expected = nativeMembersInOrder.Split(',');

        var managed = managedEnumName == nameof(EqBandType) ? typeof(EqBandType) : typeof(EqRoute);
        var managedValues = Enum.GetValues(managed).Cast<int>().OrderBy(v => v).ToArray();

        Assert.Equal(expected.Length, managedValues.Length);

        for (var i = 0; i < expected.Length; i++)
        {
            // The native member must EXIST and must carry the ordinal the managed
            // side casts to. Anchored on the member name, so a reorder is caught
            // by the value rather than by the position in the file.
            var m = Regex.Match(abi, $@"\b{Regex.Escape(expected[i])}\s*=\s*(\d+)");
            Assert.True(m.Success,
                $"'{expected[i]}' is no longer declared in {nativeEnumName}; the managed {managedEnumName} casts straight across to it.");
            Assert.Equal(i, int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture));
            Assert.Equal(i, managedValues[i]);
        }
    }

    /// <summary>
    ///     Every shipped preset is already inside <see cref="EqLimits" />.
    ///     <para>
    ///         <c>EqSection.SetBand</c> clamps and <c>PushEqTo</c> clamps, but
    ///         <c>LoadPreset</c> and the constructor copy the table VERBATIM — so a
    ///         preset authored out of range would be drawn at the value its author
    ///         typed and played at the value the engine clamped it to. That is the
    ///         drawn-versus-audible split the folder exists to prevent, arriving
    ///         through the one door that does not clamp.
    ///     </para>
    /// </summary>
    [Fact]
    public void EveryShippedPreset_IsAlreadyInsideTheEngineRanges()
    {
        Assert.NotEmpty(EqPresets.Keys);

        foreach (var key in EqPresets.Keys)
        {
            var bands = EqPresets.ByKey(key);
            Assert.NotNull(bands);
            Assert.Equal(EqLimits.BandCount, bands!.Count);

            for (var i = 0; i < bands.Count; i++)
            {
                Assert.True(bands[i] == bands[i].Clamped(),
                    $"Preset '{key}' band {i} ({bands[i]}) is outside EqLimits: the plot would draw " +
                    $"{bands[i]} while the engine runs {bands[i].Clamped()}.");
            }
        }
    }

    // ----- helpers -----------------------------------------------------------

    private static double NativeFloat(string source, string constantName)
    {
        var m = Regex.Match(source, $@"\b{Regex.Escape(constantName)}\s*=\s*([0-9.]+)f?\s*;");
        Assert.True(m.Success,
            $"'{constantName}' is gone from AudioEngine.h. EqLimits mirrors it — rename both or the clamps drift.");
        return double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
    }

    private static string NativeSource(string relativeToNativeProject)
    {
        var path = Path.Combine(TestPaths.RepoRoot(), "JuceSharp", "src", "JuceSharp.Native", relativeToNativeProject);
        Assert.True(File.Exists(path),
            $"'{path}' is missing. The JuceSharp submodule has to be checked out for this repository to build " +
            "at all, so this is a broken checkout rather than an optional fixture.");
        return File.ReadAllText(path);
    }
}
