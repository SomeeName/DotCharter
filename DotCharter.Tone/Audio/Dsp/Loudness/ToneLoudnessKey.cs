using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;

namespace DotCharter.Tone.Audio.Dsp.Loudness;

/// <summary>
///     The identity a tone's rendered loudness is cached under: everything about a
///     chain that can change what comes out of it — every module's type, bypass,
///     branch tag, sidechain key, float params and string params, in signal order —
///     hashed, plus <see cref="MeasurementVersion" />. Scenes and control assignments
///     are left out: they move knobs at play time, not the static chain that was
///     measured.
///     <para>
///         Float params are written round-trip (<c>"R"</c>), so two chains that differ
///         by a hair are two keys — never one cached answer for two different tones.
///     </para>
/// </summary>
public static class ToneLoudnessKey
{
    /// <summary>
    ///     Bumped when the measurement itself changes meaning: the phrase, the meter,
    ///     the render. A persisted answer from another version is then simply never
    ///     asked for again.
    /// </summary>
    public const int MeasurementVersion = 1;

    /// <summary>The cache key of <paramref name="body" /> (hex SHA-256).</summary>
    public static string For(IReadOnlyList<DcRigModule> body)
    {
        ArgumentNullException.ThrowIfNull(body);
        var sb = new StringBuilder(256);
        sb.Append("v").Append(MeasurementVersion.ToString(CultureInfo.InvariantCulture)).Append('\n');
        foreach (var m in body)
        {
            sb.Append(m.ModuleTypeId).Append('|')
                .Append(m.Bypassed ? '1' : '0').Append('|')
                .Append(m.Branch.ToString(CultureInfo.InvariantCulture)).Append('|')
                .Append(((int)m.Sidechain).ToString(CultureInfo.InvariantCulture));
            foreach (var (id, value) in m.Params.OrderBy(p => p.Key, StringComparer.Ordinal))
                sb.Append('|').Append(id).Append('=').Append(value.ToString("R", CultureInfo.InvariantCulture));
            if (m.StringParams is { Count: > 0 } strings)
            {
                foreach (var (id, value) in strings.OrderBy(p => p.Key, StringComparer.Ordinal))
                    sb.Append("|$").Append(id).Append('=').Append(value);
            }

            sb.Append('\n');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }
}
