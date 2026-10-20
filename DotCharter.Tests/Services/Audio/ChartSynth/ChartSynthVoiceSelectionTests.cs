using DotCharter.Tone.Audio.ChartSynth;
using JuceSharp;

namespace DotCharter.Tests.Services.Audio.ChartSynth;

/// <summary>
///     The built-in voice is one request and two instruments: a bass chart is
///     played by the bass, any other chart by the guitar, and an explicit
///     placeholder stays the placeholder. The service starts on the built-in voice.
/// </summary>
public sealed class ChartSynthVoiceSelectionTests
{
    private static ChartNoteUpload Upload(params JuceChartNoteFlags[] flags) => new()
    {
        Notes = flags.Select(f => new JuceChartNote { Flags = f }).ToArray(),
        Bends = [],
    };

    [Fact]
    public void A_fresh_service_requests_the_built_in_voice_not_the_placeholder()
        => Assert.Equal(ChartSynthVoice.Guitar, ChartSynthVoiceSelection.Default);

    [Theory]
    [InlineData(ChartSynthVoice.Guitar)]
    [InlineData(ChartSynthVoice.Bass)]
    public void A_built_in_request_follows_the_chart(ChartSynthVoice requested)
    {
        Assert.Equal(ChartSynthVoice.Bass,
            ChartSynthVoiceSelection.Effective(requested, Upload(JuceChartNoteFlags.Bass, JuceChartNoteFlags.Bass)));
        Assert.Equal(ChartSynthVoice.Guitar,
            ChartSynthVoiceSelection.Effective(requested, Upload(JuceChartNoteFlags.PalmMute, JuceChartNoteFlags.None)));
        Assert.Equal(ChartSynthVoice.Guitar, ChartSynthVoiceSelection.Effective(requested, Upload()));
    }

    [Fact]
    public void Nothing_uploaded_keeps_the_request()
    {
        Assert.Equal(ChartSynthVoice.Bass, ChartSynthVoiceSelection.Effective(ChartSynthVoice.Bass, null));
        Assert.Equal(ChartSynthVoice.Guitar, ChartSynthVoiceSelection.Effective(ChartSynthVoice.Guitar, null));
    }

    [Fact]
    public void An_explicit_placeholder_is_honoured_even_for_a_bass_chart()
    {
        Assert.Equal(ChartSynthVoice.Placeholder,
            ChartSynthVoiceSelection.Effective(ChartSynthVoice.Placeholder, Upload(JuceChartNoteFlags.Bass)));
    }

    [Fact]
    public void The_builder_marks_a_bass_arrangement_so_the_bass_plays_it()
    {
        // End to end through the real builder: what makes a chart "bass" is the
        // flag ChartNoteEventBuilder puts on its notes, not a second source of truth.
        GuitarNote[] notes = [ChartNoteEventBuilderTests.Note(1, 0, 3), ChartNoteEventBuilderTests.Note(2, 1, 5)];
        var beats = ChartNoteEventBuilderTests.Beats();
        var bass = ChartNoteEventBuilder.Build(new Arrangement { ArrangementType = ArrangementType.Bass, StringCount = 4 },
            notes, [], beats, null, 120);
        var guitar = ChartNoteEventBuilder.Build(new Arrangement(), notes, [], beats, null, 120);
        Assert.True(ChartSynthVoiceSelection.IsBass(bass));
        Assert.False(ChartSynthVoiceSelection.IsBass(guitar));
        Assert.Equal(ChartSynthVoice.Bass, ChartSynthVoiceSelection.Effective(ChartSynthVoiceSelection.Default, bass));
        Assert.Equal(ChartSynthVoice.Guitar, ChartSynthVoiceSelection.Effective(ChartSynthVoiceSelection.Default, guitar));
    }
}
