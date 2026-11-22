namespace DotCharter.Core.Domain.Vocals;

/// <summary>Why an automatically placed syllable needs a human timing review.</summary>
[Flags]
public enum LyricReviewReason
{
    None = 0,
    LowScore = 1,
    Interpolated = 2,
    CapHit = 4,
    Compressed = 8,
    Gap = 16,
    Repaired = 32
}
