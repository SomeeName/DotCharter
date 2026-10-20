using System.Collections.ObjectModel;
using DotCharter.Core.Domain.Vocals;
using DotCharter.Rendering.Text;
using DotCharter.UI.Controls;
using DotCharter.UI.Controls.Layers.Helpers;
using SkiaSharp;

namespace DotCharter.Tests.UI;

/// <summary>
///     The 2D timeline pre-registers a chart's WHOLE text into its glyph atlas,
///     so scrolling any part of it into view during playback can never trigger
///     the in-frame atlas rebuild (a 16 MB re-rasterize + two GPU idles on the UI
///     thread). The seed set carries no apostrophe, <c>!</c>, accented or CJK
///     glyph, so the fixture leans on exactly those.
/// </summary>
public sealed class ChartTextPreRegistrationTests
{
    private sealed class HeadlessChartFontAtlas : ChartFontAtlas
    {
        protected override void UploadAtlas(SKBitmap bitmap) { }
        public override void Dispose() { }
    }

    private static (ObservableCollection<LyricPhrase> Lyrics, ObservableCollection<EventPoint> Events,
        ObservableCollection<ToneChange> Tones) Chart()
    {
        var phrase = new LyricPhrase();
        phrase.Lyrics.Add(new LyricEvent { Text = "don't" });
        phrase.Lyrics.Add(new LyricEvent { Text = "stöp" });
        phrase.Lyrics.Add(new LyricEvent { Text = "夜に駆ける" });
        var lyrics = new ObservableCollection<LyricPhrase> { phrase };
        var events = new ObservableCollection<EventPoint>
        {
            new() { Section = "chörus", Phrase = "riff & bridge" },
            new() { Section = "verse", Phrase = "intro_2!" }
        };
        var tones = new ObservableCollection<ToneChange> { new() { ToneName = "Gtr_Lead!" } };
        return (lyrics, events, tones);
    }

    [Fact]
    public void AfterPreRegistration_NoWindowedStringAddsACharacter()
    {
        var atlas = new HeadlessChartFontAtlas();
        var (lyrics, events, tones) = Chart();

        ChartTextPreRegistration.Register(atlas, lyrics, events, tones);
        var registered = atlas.RegisteredCharacterCount;
        Assert.True(registered > ChartFontAtlas.SeedCharacterCount, "the fixture must carry non-seed glyphs");

        // Everything the per-frame windowed scan can feed, in the drawn form.
        foreach (var phrase in lyrics)
        {
            foreach (var word in phrase.Lyrics)
                atlas.RegisterCharacters(word.Text);
        }

        foreach (var ep in events)
        {
            atlas.RegisterCharacters(ep.Phrase);
            atlas.RegisterCharacters(ep.Section);
            atlas.RegisterCharacters(RenderStringCache.Upper(ep.Section));
        }
        foreach (var tone in tones) atlas.RegisterCharacters(tone.ToneName);

        Assert.Equal(registered, atlas.RegisteredCharacterCount);
    }

    /// <summary>The claim above is only worth something if the windowed strings
    /// WOULD have grown the set without the pass.</summary>
    [Fact]
    public void WithoutPreRegistration_TheSameWindowedStringsGrowTheSet()
    {
        var atlas = new HeadlessChartFontAtlas();
        var (lyrics, events, tones) = Chart();
        var seed = atlas.RegisteredCharacterCount;

        atlas.RegisterCharacters(lyrics[0].Lyrics[0].Text);
        Assert.True(atlas.RegisteredCharacterCount > seed, "the apostrophe is not a seed glyph");
        atlas.RegisterCharacters(RenderStringCache.Upper(events[0].Section));
        atlas.RegisterCharacters(tones[0].ToneName);
        Assert.True(atlas.RegisteredCharacterCount > seed + 1);
    }

    [Fact]
    public void TheUppercasedSection_IsRegisteredInItsDrawnForm()
    {
        var atlas = new HeadlessChartFontAtlas();
        var events = new ObservableCollection<EventPoint> { new() { Section = "chörus" } };
        ChartTextPreRegistration.Register(atlas, [], events, []);
        var after = atlas.RegisteredCharacterCount;
        atlas.RegisterCharacters("Ö");
        Assert.Equal(after, atlas.RegisteredCharacterCount);
    }
}
