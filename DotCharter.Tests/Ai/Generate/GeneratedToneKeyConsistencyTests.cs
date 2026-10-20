using System.Xml.Linq;
using DotCharter.Services.Ai.Generate;
using DotCharter.Services.Export.DlcBuilder;
using DotCharter.Services.Parsing.Xml.Song;
using DotCharter.Tone.Audio.Dsp.Tones;

namespace DotCharter.Tests.Ai.Generate;

/// <summary>
///     <b>One NAME has to survive four hops.</b> A generated tone's
///     <see cref="RsTone.Key" /> becomes the <c>ToneChange.ToneName</c> it was
///     generated for, becomes the arrangement's <c>&lt;tonebase&gt;</c> /
///     <c>&lt;tonea&gt;</c>–<c>&lt;toned&gt;</c> header entries and its
///     <c>&lt;tone name="…"&gt;</c> change events in the RS XML, and becomes the
///     <c>Key</c> of a tone BODY in the <c>.rs2dlc</c>.
///     <para>
///         Break any one of those and nothing throws: DLC Builder imports the
///         project, lists the tones, and marks them <b>"(Unused)"</b> because the
///         arrangement XML never names them — which is exactly what the owner saw
///         on the first end-to-end file (2026-09-16), where the XML bundle was a
///         stale copy rather than one written from the project that now held the
///         tone changes. A tone nothing switches to is a tone that does not exist.
///     </para>
///     Pure: no provider, no engine, no audio.
/// </summary>
public sealed class GeneratedToneKeyConsistencyTests
{
    private static readonly Rs2DlcProjectBuilder.Rs2DlcBuildOptions Options =
        new("song.wav", "cover.png", 32_000, "Tester");

    private static (ChartProject Project, Arrangement Arrangement, IReadOnlyList<GeneratedTone> Tones) Generated(
        params string[] keys)
    {
        var beats = GenerateFakes.Beats(bars: 16);
        var project = new ChartProject { Chart = { SongName = "My Song", Artist = "Tester" } };
        foreach (var beat in beats) project.Beats.Add(beat);

        var arrangement = new Arrangement { Name = "Lead" };
        arrangement.Levels.Add(new ChartLevel { DifficultyIndex = 0 });
        for (var i = 0; i < 6; i++) arrangement.TuningPitches.Add(new TuningOffset(0));

        // The placed changes carry the charter's OWN names, which the generated
        // keys then replace — the point of the hop, and the thing a by-name match
        // would get wrong.
        for (var i = 0; i < keys.Length; i++)
        {
            var change = GenerateFakes.At(4_000 + (i * 6_000), beats);
            change.ToneName = "whatever the charter typed " + i;
            arrangement.ToneChanges.Add(change);
        }

        project.Arrangements.Add(arrangement);

        var windows = ToneWindowPlanner.Plan(arrangement.ToneChanges, beats, 32_000);
        var generated = windows.Select((w, i) => Tone(w, keys[i])).ToList();
        return (project, arrangement, generated);
    }

    private static GeneratedTone Tone(ToneWindow window, string key)
    {
        var (amp, cab) = GenerateFakes.RealGuitarGear();
        var tone = new RsTone(
            key,
            // A DISPLAY name that is not the key, because the chain keys on the KEY.
            key + " (a pretty name the model chose)",
            RsToneAuthoring.DefaultVolumeDb, [RsToneAuthoring.CleanDescriptorUIName],
            new RsAmp(amp, RsGearCatalog.Shared.GetCategory(amp), new Dictionary<string, float>(StringComparer.Ordinal)),
            new RsCab(cab, RsGearCatalog.Shared.GetCategory(cab), new Dictionary<string, float>(StringComparer.Ordinal)),
            [], [], []);

        return new GeneratedTone(window, tone, null, null);
    }

    private static XElement WriteXml(ChartProject project, Arrangement arrangement)
    {
        var single = new ChartProject { Chart = project.Chart };
        foreach (var beat in project.Beats) single.Beats.Add(beat);
        single.Arrangements.Add(arrangement);
        return new RsXmlSongWriter().Write(single, 32_000).Root!;
    }

    // ---- the four hops -------------------------------------------------

    [Fact]
    public void Every_placed_change_is_renamed_to_the_key_of_the_tone_generated_for_it()
    {
        var (project, arrangement, generated) = Generated("Verse", "Chorus", "Solo");

        RsToneProjectWriter.Write(project, arrangement, generated);

        Assert.Equal(
            generated.Select(g => g.Tone!.Key),
            arrangement.ToneChanges.Select(c => c.ToneName));
    }

    [Fact]
    public void The_XML_names_one_tone_change_per_placed_change_and_declares_every_one()
    {
        var (project, arrangement, generated) = Generated("Verse", "Chorus", "Solo");

        RsToneProjectWriter.Write(project, arrangement, generated);
        var song = WriteXml(project, arrangement);

        var events = song.Element("tones")!.Elements("tone")
            .Select(e => e.Attribute("name")!.Value).ToList();
        Assert.Equal(["Verse", "Chorus", "Solo"], events);

        // A change event naming a tone no header slot declares is the fault this
        // arrangement's own writer comment warns about.
        var declared = new[] { "tonebase", "tonea", "toneb", "tonec", "toned" }
            .Select(n => song.Element(n)?.Value)
            .Where(v => !string.IsNullOrEmpty(v))
            .ToList();

        Assert.Equal("Verse", song.Element("tonebase")!.Value);
        foreach (var name in events)
            Assert.Contains(name, declared);
    }

    [Fact]
    public void Every_name_the_XML_uses_resolves_to_a_body_in_the_rs2dlc()
    {
        var (project, arrangement, generated) = Generated("Verse", "Chorus", "Solo");

        RsToneProjectWriter.Write(project, arrangement, generated);
        var song = WriteXml(project, arrangement);

        var dlc = Rs2DlcProjectBuilder.Build(project, Options);
        var bodies = dlc.Tones
            .Select(t => RsToneJsonParser.TryParse(
                System.Text.Json.JsonSerializer.Serialize(t), out var parsed) ? parsed!.Key : "")
            .ToList();

        Assert.Equal(["Verse", "Chorus", "Solo"], bodies);

        var used = song.Element("tones")!.Elements("tone")
            .Select(e => e.Attribute("name")!.Value)
            .Append(song.Element("tonebase")!.Value)
            .Distinct(StringComparer.Ordinal);

        foreach (var name in used)
            Assert.Contains(name, bodies);
    }

    /// <summary>The arrangement's `.rs2dlc` slots are the same names the XML header
    /// declares — the two halves DLC Builder reads to decide a tone is USED.</summary>
    [Fact]
    public void The_rs2dlc_slots_agree_with_the_XML_header()
    {
        var (project, arrangement, generated) = Generated("Verse", "Chorus", "Solo");

        RsToneProjectWriter.Write(project, arrangement, generated);
        var song = WriteXml(project, arrangement);
        var dlc = Rs2DlcProjectBuilder.Build(project, Options);
        var fields = (Rs2DlcInstrumentalDto)dlc.Arrangements[0].Fields[0];

        Assert.Equal(song.Element("tonebase")!.Value, fields.BaseTone);
        Assert.Equal(
            new[] { "tonea", "toneb", "tonec", "toned" }
                .Select(n => song.Element(n)?.Value)
                .Where(v => !string.IsNullOrEmpty(v)),
            fields.Tones);
    }

    /// <summary>A display NAME that differs from the key must never leak into the
    /// chain — Rocksmith switches on the key, and a `&lt;tone name&gt;` carrying the
    /// pretty name resolves to nothing.</summary>
    [Fact]
    public void The_display_name_never_stands_in_for_the_key()
    {
        var (project, arrangement, generated) = Generated("Verse", "Chorus");

        RsToneProjectWriter.Write(project, arrangement, generated);
        var song = WriteXml(project, arrangement);

        var text = song.ToString();
        Assert.DoesNotContain("a pretty name the model chose", text, StringComparison.Ordinal);

        // …and the body still CARRIES it, so the charter sees it in DLC Builder.
        Assert.Contains(project.ToneDefinitions,
            d => d.Name.Contains("a pretty name the model chose", StringComparison.Ordinal));
    }
}
