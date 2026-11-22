using DotCharter.Analysis.Tuning;
using DotCharter.Services.Audio.Tuning;
using DotCharter.Services.Editing.Tempo.RecordingAlignment;

namespace DotCharter.Tests.Infrastructure;

/// <summary>A reference-pitch service that hears nothing (zero confidence), so no offer is ever made.</summary>
public sealed class FakeReferencePitchService : IReferencePitchService
{
    public Task<ReferencePitchCheck> MeasureAsync(bool bass, double authoredCentOffset, CancellationToken cancellationToken) =>
        Task.FromResult(ReferencePitchCheck.From(new ReferencePitchEstimate(0, 0, 500, 60), authoredCentOffset,
            RecordingAlignmentSource.Mix));
}
