using System.Globalization;
using DotCharter.Core.Domain.Project;
using DotCharter.Core.Utilities;

namespace DotCharter.Core.Linking;

/// <summary>
///     Which arrangements may share linked measures (the strict rules of linked measures): the same
///     string count, tuning, capo and cent offset; bass never with guitar; never a vocal
///     or a play-only part. Two arrangements are compatible exactly when their
///     <see cref="Signature" />s are equal (and non-null).
/// </summary>
public static class LinkCompatibility
{
    /// <summary>The compatibility class of <paramref name="arrangement" />, or null when it
    /// can never hold a link (vocals, play-only content).</summary>
    public static string? Signature(Arrangement arrangement)
    {
        ArgumentNullException.ThrowIfNull(arrangement);
        if (arrangement.ArrangementType == ArrangementType.Vocal) return null;
        if (arrangement.Provenance == ChartProvenance.PlayOnly) return null;

        var instrument = ArrangementInstrument.IsBassPart(arrangement) ? "bass" : "guitar";
        var tuning = arrangement.TuningPitches.Count == arrangement.StringCount
            ? string.Join(",", arrangement.TuningPitches.Select(t => t.Step.ToString(CultureInfo.InvariantCulture)))
            : string.Join(",", arrangement.Tuning);
        var cents = Math.Round(arrangement.CentOffset).ToString(CultureInfo.InvariantCulture);
        return string.Create(CultureInfo.InvariantCulture,
            $"{instrument}|{arrangement.StringCount}|{tuning}|capo{arrangement.Capo}|{cents}c");
    }

    public static bool CanShare(Arrangement a, Arrangement b) =>
        Signature(a) is { } signature && string.Equals(signature, Signature(b), StringComparison.Ordinal);
}
