using Avalonia;
using Avalonia.Controls;

namespace DotCharter.Tests.Infrastructure;

/// <summary>
///     Reads the Fire type ladder out of the live resource dictionary, so a
///     grammar test asserts "this row wears <c>FireSizeRow</c>" rather than
///     "this row is 13.5 px".
///     <para>
///         <b>A hard-coded number over a derived value is the failure mode this
///         suite keeps having</b> (<c>DotCharter.Tests/AGENTS.md</c>). Eleven
///         grammar assertions pinned the ladder as literals, so re-toning the
///         type for Graphite — every sub-11 px step raised, tracking cut from
///         2.3 to 0.6 — turned them all red at once while the markup was still
///         perfectly correct. Reading the token keeps the CLAIM ("the group
///         title wears the card-title step") and drops the brittleness.
///     </para>
/// </summary>
internal static class FireTypeLadder
{
    /// <summary>A size step, e.g. <c>FireSizeRow</c>.</summary>
    internal static double Size(string tokenKey) => Read(tokenKey);

    /// <summary>A tracking step, e.g. <c>FireTrackEyebrow</c>.</summary>
    internal static double Track(string tokenKey) => Read(tokenKey);

    private static double Read(string tokenKey)
    {
        var app = Application.Current
                  ?? throw new InvalidOperationException(
                      $"no Application — read {tokenKey} inside a headless dispatch");

        if (app.TryFindResource(tokenKey, out var value) && value is double number) return number;

        throw new InvalidOperationException(
            $"'{tokenKey}' is not a double in the merged dictionaries — the ladder moved or the key is misspelt");
    }
}
