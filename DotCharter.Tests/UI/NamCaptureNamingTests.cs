using DotCharter.Tone.Online.Tone3000;
using DotCharter.Tone.UI.Controls.Dsp;

namespace DotCharter.Tests.UI;

/// <summary>
/// The neural amp head's faceplate says WHICH capture is loaded. That name comes
/// from the local library's manifest when there is one, and from the file NAME
/// when there is not — a capture picked straight off disk through the file picker
/// has no sidecar at all, and printing a raw path on an amp panel is exactly the
/// "generic Neural Amp label" the face exists to replace.
///
/// <para>Pure: no Avalonia, no filesystem. The only coupling asserted against a
/// real service type is the round trip with
/// <see cref="NamCaptureIndex.BuildFileName"/> — the two must stay inverse or
/// every downloaded capture reads as gibberish on the plate.</para>
/// </summary>
public sealed class NamCaptureNamingTests
{
    [Theory]
    // The library's own download naming: "{modelId}-{slug}-{tier}.nam".
    [InlineData("C:/models/12345-fender-deluxe-reverb-clean-lite.nam", "Fender Deluxe Reverb Clean")]
    [InlineData("C:/models/9-plexi-1959-standard.nam", "Plexi 1959")]
    // A bundled capture (no id prefix, no tier suffix).
    [InlineData("C:/models/nam-rhythm-crunch.nam", "Nam Rhythm Crunch")]
    [InlineData("C:/models/dingwall-bass-di.nam", "Dingwall Bass Di")]
    // Hand-named files: underscores and spaces work like dashes.
    [InlineData("C:/models/my_own_capture.aidax", "My Own Capture")]
    [InlineData("C:/models/Vox AC30 Top Boost.nam", "Vox AC30 Top Boost")]
    public void FromFileName_deslugs_the_model_name(string path, string expected)
        => Assert.Equal(expected, NamCaptureNaming.FromFileName(path).Title);

    /// <summary>A leading YEAR is part of the amp's name, not a model id — only a
    /// digit run immediately followed by a dash AND more content is a prefix, and
    /// the library only ever writes one. "1959-plexi" would be ambiguous, so this
    /// pins the deliberate choice: we strip it, because that IS the library's
    /// format, and a capture wanting the year keeps it anywhere else in the name.</summary>
    [Fact]
    public void FromFileName_strips_only_a_leading_digit_run()
    {
        Assert.Equal("Plexi", NamCaptureNaming.FromFileName("1959-plexi.nam").Title);
        Assert.Equal("Plexi 1959", NamCaptureNaming.FromFileName("plexi-1959.nam").Title);
        Assert.Equal("1959", NamCaptureNaming.FromFileName("1959.nam").Title); // nothing left to strip
    }

    /// <summary>An already-capitalised token is left alone: lower-casing "DI" or
    /// "AC30" would invent a spelling the capture's author never used.</summary>
    [Fact]
    public void FromFileName_leaves_acronyms_alone()
        => Assert.Equal("Ampeg SVT DI", NamCaptureNaming.FromFileName("ampeg-SVT-DI.nam").Title);

    [Theory]
    [InlineData("x.nam", "NAM")]
    [InlineData("x.aidax", "AIDA-X")]
    [InlineData("x.wav", "IR")]
    [InlineData("x.NAM", "NAM")] // the loader wants lowercase, the PLATE does not care
    public void FromFileName_reads_the_format_from_the_extension(string path, string expected)
        => Assert.Equal(expected, NamCaptureNaming.FromFileName(path).Format);

    [Theory]
    [InlineData("nam", "NAM")]
    [InlineData("aida-x", "AIDA-X")]
    [InlineData("keras", "AIDA-X")]
    [InlineData("h5", "AIDA-X")]
    [InlineData("impulse", "IR")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void FormatLabelFor_folds_the_TONE3000_spellings(string? format, string expected)
        => Assert.Equal(expected, NamCaptureNaming.FormatLabelFor(format));

    [Fact]
    public void SizeTier_is_lifted_out_of_the_title_and_labelled()
    {
        var id = NamCaptureNaming.Resolve("C:/m/77-boss-wn-feather.nam");
        Assert.Equal("Boss Wn", id.Title);          // "feather" is not part of the amp's name
        Assert.Equal("Feather", id.SizeTier);
        Assert.Equal("NAM · Feather", id.FormatLabel);
    }

    [Fact]
    public void Architecture_suffixes_stay_acronyms_in_the_chip()
    {
        Assert.Equal("LSTM", NamCaptureNaming.Resolve("a-lstm.nam").SizeTier);
        Assert.Equal("WaveNet", NamCaptureNaming.Resolve("a-wavenet.nam").SizeTier);
    }

    /// <summary>The manifest wins field by field — a half-filled sidecar must
    /// degrade one field at a time, not drop the whole identity.</summary>
    [Fact]
    public void Resolve_prefers_the_manifest_but_falls_back_per_field()
    {
        var id = NamCaptureNaming.Resolve(
            "C:/m/12345-fender-deluxe-reverb-clean-lite.nam",
            manifestTitle: "Deluxe Reverb Clean",
            manifestAuthor: "Steven Atkinson",
            manifestFormat: null,      // absent → the extension answers
            manifestSizeTier: "  ");   // blank → the file name's tier answers

        Assert.Equal("Deluxe Reverb Clean", id.Title);
        Assert.Equal("Steven Atkinson", id.Author);
        Assert.Equal("NAM", id.Format);
        Assert.Equal("Lite", id.SizeTier);
        Assert.Equal("NAM · Lite", id.FormatLabel);
        Assert.True(id.HasTitle);
        Assert.True(id.HasAuthor);
    }

    /// <summary>Author is the ONE field a bare path can never prove, so it must
    /// stay empty rather than be guessed at from the file name.</summary>
    [Fact]
    public void Author_is_empty_without_a_manifest()
        => Assert.False(NamCaptureNaming.Resolve("C:/m/some-capture.nam").HasAuthor);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Nothing_loaded_resolves_to_None(string? path)
    {
        var id = NamCaptureNaming.Resolve(path);
        Assert.Equal(NamCaptureIdentity.None, id);
        Assert.False(id.HasTitle);
        Assert.Equal(string.Empty, id.FormatLabel);
    }

    /// <summary>A faceplate that throws during layout is worse than one that says
    /// nothing — every degenerate path must resolve, never throw.</summary>
    [Theory]
    [InlineData(".nam")]           // extension only, no stem
    [InlineData("C:/m/")]          // directory
    [InlineData("---.nam")]        // separators only
    [InlineData("\u0000bad.nam")]  // a character the platform dislikes
    public void Degenerate_paths_never_throw(string path)
    {
        var id = NamCaptureNaming.Resolve(path);
        Assert.NotNull(id.Title);
        Assert.NotNull(id.FormatLabel);
    }

    /// <summary>The load-bearing pairing: the plate de-slugs exactly what the
    /// library's downloader slugged. If <c>BuildFileName</c> ever changes shape,
    /// this is what says the faceplate stopped reading it.</summary>
    [Theory]
    [InlineData(4210L, "Marshall JCM800 Lead", "lite", "nam", "Marshall JCM800 Lead")]
    [InlineData(7L, "Fender Twin Reverb", "standard", "nam", "Fender Twin Reverb")]
    [InlineData(99L, "AC30 Top Boost", "feather", "aidax", "AC30 Top Boost")]
    [InlineData(0L, "AC30 Top Boost", "feather", "aidax", "AC30 Top Boost")]
    public void RoundTrips_through_the_librarys_own_download_naming(
        long modelId, string title, string tier, string format, string expectedPlate)
    {
        var fileName = NamCaptureIndex.BuildFileName(modelId, title, tier, format);
        var id = NamCaptureNaming.FromFileName(fileName);

        // The slug lower-cases, so compare case-insensitively: what matters is
        // that the WORDS survive and the id/tier decorations do not.
        Assert.Equal(expectedPlate, id.Title, ignoreCase: true);
        Assert.Equal(tier, id.SizeTier, ignoreCase: true);
    }
}
