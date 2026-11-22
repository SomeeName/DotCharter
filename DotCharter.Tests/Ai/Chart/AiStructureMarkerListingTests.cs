using DotCharter.Services.Ai.Chart;

namespace DotCharter.Tests.Ai.Chart;

/// <summary>#486: the structure listing names each marker's status, solo flag, tone, event codes and arrangements, defaults left out.</summary>
public sealed class AiStructureMarkerListingTests
{
    private readonly AiChartFixture _f = new();

    [Fact]
    public void A_solo_phrase_a_tone_change_and_an_E3_event_are_named_and_plain_markers_are_not()
    {
        var lead = _f.Arrangement;
        var rhythm = ChartProjectCloner.CloneArrangement(lead);
        rhythm.Name = "Rhythm";
        _f.Project.Arrangements.Add(rhythm);
        foreach (var a in new[] { lead, rhythm })
        {
            a.EventPoints.Clear();
            a.PhraseInfos.Clear();
            a.EventPoints.Add(new EventPoint { Position = new FractionalPosition(0), Section = "intro" });
            a.EventPoints.Add(new EventPoint { Position = new FractionalPosition(4), Phrase = "solo2", Events = "E3" });
            a.EventPoints.Add(new EventPoint { Position = new FractionalPosition(8), Phrase = "plain" });
        }

        lead.PhraseInfos["solo2"] = new PhraseInfo(0, true);
        lead.ToneChanges.Add(new ToneChange { Position = new FractionalPosition(4), ToneName = "Lead" });
        // Only the lead has the verse section, marked draft.
        lead.EventPoints.Add(new EventPoint { Position = new FractionalPosition(12), Section = "verse", Status = SectionStatus.Draft });

        var context = AiStructureContext.Of(lead, _f.Beats, null, null, null, _f.Project.Arrangements);
        var text = AiStructurePrompt.Listing(_f.Facts, _f.Level, _f.Beats, context);

        Assert.Contains("\"solo2\" at bar 2 beat 1 (0:02.0) {solo, tone \"Lead\", events E3, on all}", text, StringComparison.Ordinal);
        Assert.Contains("\"verse\" at bar 4 beat 1 (0:06.0) {status draft, on Lead}", text, StringComparison.Ordinal);
        Assert.Contains("\"plain\" at bar 3 beat 1 (0:04.0) {on all}", text, StringComparison.Ordinal);
        Assert.Contains("\"intro\" at bar 1 beat 1 (0:00.0) {on all}", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_marker_with_only_defaults_in_a_one_arrangement_song_lists_nothing_extra()
    {
        var context = AiStructureContext.Of(_f.Arrangement, _f.Beats, null, null);

        Assert.All(context.Markers.Where(m => m.Id.StartsWith('p')), m => Assert.Equal("", m.Notes));
    }
}
