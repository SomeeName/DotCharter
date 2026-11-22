using DotCharter.Services.Export.Audio;
using DotCharter.Services.Import.Audio;

namespace DotCharter.Tests.Services.Export;

public sealed class ExportAudioFormatsTests
{
    [Theory]
    [InlineData(".wem", "WEM")]
    [InlineData(".wav", "WAV")]
    [InlineData(".ogg", "OGG")]
    [InlineData(".flac", "FLAC")]
    [InlineData(".mp3", "MP3")]
    [InlineData(".m4a", "M4A")]
    [InlineData(".aac", "AAC")]
    [InlineData(".aif", "AIF")]
    [InlineData(".aiff", "AIFF")]
    [InlineData(".aifc", "AIFC")]
    [InlineData(".bwf", "BWF")]
    [InlineData(".oga", "OGA")]
    [InlineData(".wma", "WMA")]
    [InlineData(".opus", "OPUS")]
    public void Known_decoder_inputs_are_offered_in_the_picker_and_display_hint(string extension, string label)
    {
        Assert.Contains(extension, ExportAudioFormats.Extensions);
        Assert.Contains("*" + extension, ExportAudioFormats.PickerPatterns);
        Assert.Contains(label, ExportAudioFormats.DisplayText.Split(" / "));
    }

    [Fact]
    public void Every_advertised_import_format_is_also_offered_for_export()
    {
        Assert.All(AudioFileTypes.Extensions, extension => Assert.Contains(extension, ExportAudioFormats.Extensions));
        Assert.Equal(ExportAudioFormats.Extensions.Count,
            ExportAudioFormats.Extensions.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }
}
