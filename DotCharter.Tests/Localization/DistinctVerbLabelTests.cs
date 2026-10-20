using System.Xml.Linq;

namespace DotCharter.Tests.Localization;

/// <summary>
///     Two buttons that sit side by side and open DIFFERENT things may not carry the same words.
///     Renaming the live amp and effects chain to "Rig" everywhere (2026-09-22) caught the VST3
///     chain's key too, so Get ready and the pause menu showed two "Rig…" buttons, one opening the
///     rig and the other the VST plugin chain, with nothing to tell them apart but a caption. The
///     same goes for two verbs that act on different things (#636).
/// </summary>
public sealed class DistinctVerbLabelTests
{
    /// <summary>Key pairs shown together whose commands open different surfaces.</summary>
    private static readonly (string A, string B, string Where)[] Neighbours =
    [
        ("PlayMode_Pedalboard", "PlayMode_AmpFx", "Get ready and the pause menu (the rig beside the VST chain)"),
        ("PlayMode_Pause_Pedalboard_Caption", "PlayMode_Pause_AmpFx_Caption", "the pause menu's two captions"),
        // #636: a selection of notes AND chords shows both bulk verbs side by side
        // (SoundBulkCardView); one re-applies the note values, the other the chord values.
        ("Inspector_ApplyToAll", "Inspector_Chord_ApplyToAll", "the Inspector's bulk verbs on a mixed selection")
    ];

    [Theory]
    [InlineData("")]
    [InlineData(".de-DE")]
    [InlineData(".ja-JP")]
    public void NeighbouringVerbs_SayDifferentThings(string culture)
    {
        var values = XDocument.Load(Path.Combine(TestPaths.RepoRoot(), "DotCharter.Localization", $"Resources{culture}.resx"))
            .Root!.Elements("data")
            .ToDictionary(d => (string)d.Attribute("name")!, d => ((string?)d.Element("value") ?? "").Trim(), StringComparer.Ordinal);

        var clashes = Neighbours
            .Where(p => values.TryGetValue(p.A, out var a) && values.TryGetValue(p.B, out var b)
                        && string.Equals(a, b, StringComparison.CurrentCultureIgnoreCase))
            .Select(p => $"{p.A} and {p.B} both read \"{values[p.A]}\" in {p.Where}")
            .ToList();

        Assert.All(Neighbours, p =>
        {
            Assert.True(values.ContainsKey(p.A), $"{p.A} is gone — update this pair");
            Assert.True(values.ContainsKey(p.B), $"{p.B} is gone — update this pair");
        });
        Assert.True(clashes.Count == 0, string.Join(Environment.NewLine, clashes));
    }
}
