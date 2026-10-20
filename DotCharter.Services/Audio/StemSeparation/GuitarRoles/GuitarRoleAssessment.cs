namespace DotCharter.Services.Audio.StemSeparation.GuitarRoles;

/// <summary>What the stereo image of a guitar stem allows.</summary>
public enum GuitarRoleVerdict
{
    /// <summary>Not measured yet.</summary>
    Unknown,

    /// <summary>The cheap pre-check found a stereo image; the split may still find
    /// that it all sits in one place (<see cref="AllCentred" /> /
    /// <see cref="NothingCentred" />).</summary>
    Plausible,

    /// <summary>The split ran and both halves carry a real share of the guitar.</summary>
    Splittable,

    /// <summary>No guitar to speak of.</summary>
    Silent,

    /// <summary>The two channels are (nearly) the same: there is no position to split by.</summary>
    Mono,

    /// <summary>Nearly all of the guitar sits in the centre; "rhythm" would be a faint residue.</summary>
    AllCentred,

    /// <summary>Nothing sits in the centre; "lead" would be a faint residue.</summary>
    NothingCentred,

    /// <summary>The stem could not be read.</summary>
    Unreadable,
}

/// <summary>
///     The honesty rule for Guitar roles, as pure data: a split is only offered
///     when it can produce two DIFFERENT things. It never hands the user the same
///     guitar twice, or a "lead" that is a faint residue of the rhythm.
/// </summary>
/// <param name="Verdict">What the image allows.</param>
/// <param name="Stats">The whole-stem stereo statistics.</param>
/// <param name="LeadShare">Share of the stem's energy the centre mask took; null until the split ran.</param>
public sealed record GuitarRoleAssessment(GuitarRoleVerdict Verdict, StereoImageStats Stats, double? LeadShare)
{
    /// <summary>Below this level (dBFS over both channels) the stem is treated as silent.</summary>
    public const double SilentBelowDbfs = -70.0;

    /// <summary>
    ///     A side share under this is "mono": the side signal is more than ~23 dB
    ///     under the stem, which for two equal channels is a correlation above
    ///     0.99. Measured against the split: below it the rhythm half is a residue
    ///     whatever the masks do.
    /// </summary>
    public const double MonoBelowSideShare = 0.005;

    /// <summary>
    ///     Either half under this share of the stem's energy (~-11 dB) means the
    ///     split did not separate two parts; it peeled a residue off one.
    /// </summary>
    public const double MinimumRoleShare = 0.08;

    public bool CanSplit => Verdict is GuitarRoleVerdict.Plausible or GuitarRoleVerdict.Splittable;

    /// <summary>The cheap pre-check: level and correlation only, no transform.</summary>
    public static GuitarRoleAssessment FromImage(StereoImageStats stats, int channels)
    {
        if (stats.Frames == 0 || stats.RmsDbfs < SilentBelowDbfs)
            return new GuitarRoleAssessment(GuitarRoleVerdict.Silent, stats, null);
        if (channels < 2 || stats.SideShare < MonoBelowSideShare)
            return new GuitarRoleAssessment(GuitarRoleVerdict.Mono, stats, null);
        return new GuitarRoleAssessment(GuitarRoleVerdict.Plausible, stats, null);
    }

    /// <summary>The verdict once the split has measured how the masks divided the energy.</summary>
    public static GuitarRoleAssessment FromSplit(StereoImageStats stats, int channels, double leadShare)
    {
        var image = FromImage(stats, channels);
        if (!image.CanSplit) return image with { LeadShare = leadShare };
        var verdict = leadShare > 1 - MinimumRoleShare ? GuitarRoleVerdict.AllCentred
            : leadShare < MinimumRoleShare ? GuitarRoleVerdict.NothingCentred
            : GuitarRoleVerdict.Splittable;
        return new GuitarRoleAssessment(verdict, stats, leadShare);
    }
}
