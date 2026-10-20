using System.Text.Json;
using DotCharter.Tests.Ai.Eval;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Ai.AudioEval;

/// <summary>
///     The audio eval's corpus resolver, over a SYNTHETIC corpus — the half of the
///     lane that decides which recording a row is scored against.
///     <para>
///         <b>This is the test the pseudo-replication bug needed.</b>
///         <c>AudioEvalCorpus</c> used to resolve an entry to a folder by the first
///         200 characters of its tone JSON, so two songs that reuse a tone definition
///         — the normal case for one author, and the case this file builds — both
///         resolved to whichever folder was enumerated FIRST. Twelve charted rows in
///         the 2026-09-12 end-to-end report were about five distinct recordings, and
///         seven candidates agreed to four decimals across three "different" songs
///         because they were one song. Every aggregate the lane had published,
///         including its ceiling, was pseudo-replicated.
///     </para>
///     <para>
///         Runs in the DEFAULT gate: no audio is decoded, no stem is separated and no
///         engine is opened. The <c>.wav</c> files it writes are empty — resolution
///         only stats them.
///     </para>
/// </summary>
public sealed class AudioEvalCorpusResolutionTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "dc-audioeval-corpus-" + Guid.NewGuid().ToString("N"));

    /// <inheritdoc />
    public void Dispose() => BestEffortDelete.Directory(_root);

    [Fact]
    public void TwoSongsSharingOneToneDefinition_ResolveToTwoDifferentFolders()
    {
        var tone = SharedToneJson();
        WriteSong("alpha", "Alpha Song", "The Alphas", tone);
        WriteSong("beta", "Beta Song", "The Betas", tone);

        var load = AudioEvalCorpus.LoadDetailed(_root);

        Assert.Empty(load.Problems);
        Assert.Equal(2, load.Rows.Count);

        // The keeper. Under the tone-JSON resolver both rows returned ONE folder and
        // this count was 1, while Rows.Count was still 2 — which is precisely how the
        // lane reported n=12 on ~5 recordings.
        Assert.Equal(2, load.DistinctAudioFiles);

        var byId = load.Rows.ToDictionary(r => r.Entry.SongId, StringComparer.Ordinal);
        Assert.Equal(
            Path.Combine(_root, "alpha", "song.wav"),
            byId[ToneEvalEntry.MakeSongId("The Alphas", "Alpha Song")].SongAudioPath);
        Assert.Equal(
            Path.Combine(_root, "beta", "song.wav"),
            byId[ToneEvalEntry.MakeSongId("The Betas", "Beta Song")].SongAudioPath);
    }

    [Fact]
    public void TwoPackagesOfOneSong_AreReportedAsAmbiguousAndNeitherIsScored()
    {
        var tone = SharedToneJson();
        WriteSong("copy-a", "Same Song", "One Artist", tone);
        WriteSong("copy-b", "Same Song", "One Artist", tone);

        var load = AudioEvalCorpus.LoadDetailed(_root);

        // A reported failure row, never an exception: a run that has already separated
        // twenty stems must survive the twenty-first package being ambiguous.
        Assert.Empty(load.Rows);
        var problem = Assert.Single(load.Problems);
        Assert.Contains("AMBIGUOUS", problem.Reason, StringComparison.Ordinal);
        Assert.Contains("copy-a", problem.Reason, StringComparison.Ordinal);
        Assert.Contains("copy-b", problem.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void APackageWithNoAudioBesideIt_IsReportedRatherThanSilentlyDropped()
    {
        WritePackage("silent", "No Audio", "Nobody", SharedToneJson());

        var load = AudioEvalCorpus.LoadDetailed(_root);

        Assert.Empty(load.Rows);
        var problem = Assert.Single(load.Problems);
        Assert.Contains("no non-preview", problem.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    ///     One real tone body, reused verbatim by every song this file writes — which
    ///     is the whole point. It is Rocksmith's own default Lead tone, already a
    ///     committed fixture of this project, so it converts to a non-empty rig and the
    ///     corpus loader elects it.
    /// </summary>
    private static string SharedToneJson() => File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Audio", "Dsp", "Tones", "default_lead.json"));

    private void WriteSong(string folder, string title, string artist, string toneJson)
    {
        WritePackage(folder, title, artist, toneJson);
        // Empty on purpose: resolution never decodes, it only picks the largest
        // non-preview file. Decoding happens in EnsureExcerpt, which this test never
        // reaches — that is what keeps it in the default gate.
        File.WriteAllBytes(Path.Combine(_root, folder, "song.wav"), []);
    }

    private void WritePackage(string folder, string title, string artist, string toneJson)
    {
        var directory = Path.Combine(_root, folder);
        Directory.CreateDirectory(directory);

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("Title", title);
            writer.WriteString("ArtistName", artist);
            writer.WritePropertyName("Tones");
            writer.WriteStartArray();
            using (var tone = JsonDocument.Parse(toneJson)) tone.RootElement.WriteTo(writer);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        File.WriteAllBytes(Path.Combine(directory, folder + ".rs2dlc"), buffer.ToArray());
    }
}
