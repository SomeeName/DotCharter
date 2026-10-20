using DotCharter.Services.Package.DcSong;

namespace DotCharter.Tests.Presentation.Dialogs;

/// <summary>
///     A scripted <see cref="IDcSongExporter" />: the probe answers <see cref="Probe" /> (or waits on
///     <see cref="ProbeGate" />), and a write records its request and answers through
///     <see cref="Write" />, a function the test controls — no ffmpeg, no disk.
/// </summary>
internal sealed class FakeDcSongExporter : IDcSongExporter
{
    public DcSongExportProbe Probe { get; set; } = new(true, true, 34_560_044, []);

    /// <summary>When set, the probe waits for it instead of answering at once.</summary>
    public TaskCompletionSource<DcSongExportProbe>? ProbeGate { get; set; }

    public List<DcSongProbeRequest> ProbeRequests { get; } = [];

    public List<DcSongWriteRequest> WriteRequests { get; } = [];

    /// <summary>The write: request, progress, token → result. Defaults to a written package.</summary>
    public Func<DcSongWriteRequest, IProgress<DcSongWriteProgress>?, CancellationToken, Task<DcSongWriteResult>> Write { get; set; } =
        (request, _, _) => Task.FromResult(Written(request, 4_900_000));

    public Task<DcSongExportProbe> ProbeAsync(DcSongProbeRequest request, CancellationToken cancellationToken = default)
    {
        ProbeRequests.Add(request);
        return ProbeGate?.Task ?? Task.FromResult(Probe);
    }

    public Task<DcSongWriteResult> WriteAsync(
        DcSongWriteRequest request, IProgress<DcSongWriteProgress>? progress, CancellationToken cancellationToken)
    {
        WriteRequests.Add(request);
        return Write(request, progress, cancellationToken);
    }

    public static DcSongWriteResult Written(DcSongWriteRequest request, long bytes, params DcSongWriteWarning[] warnings) =>
        new(request.OutputPath,
            new DcSongManifest(1, 0, "DotCharter test",
                new DcSongInfo("key", "id", "Title", "Artist", "", "", "", 1000, false, []),
                new DcSongAudioInfo(DcSongAudioCodec.Opus, false, 192, 0, 1000), []),
            bytes, warnings);
}
