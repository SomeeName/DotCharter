namespace DotCharter.Services.Export;

/// <summary>
/// Every value a package build otherwise draws at random or from the clock:
///     the arrangements' persistent and master ids, the SNG conversion time, the
///     aggregate graph's uuids and llids, and the sound banks' Wwise ids. With all of
///     them pinned a build is a pure function of the chart, the audio and the options,
///     which is what lets a test rebuild a reference package and compare every byte.
///     <para>
///         Test-only. A shipped package must keep fresh ids: two packages sharing a
///         persistent id or a bank id collide in the game.
///     </para>
/// </summary>
internal sealed record RocksmithBuildIdentity
{
    /// <summary>The SNG <c>LastConversionDateTime</c> stamp (minute precision).</summary>
    public required DateTime ConversionTime { get; init; }

    /// <summary>One per built arrangement, in build order (instrumentals, then vocals).</summary>
    public required IReadOnlyList<RsArrangementIdentity> Arrangements { get; init; }

    /// <summary>One per aggregate-graph item, in the order the graph writes them; the
    /// llid is used only by the items that carry one.</summary>
    public required IReadOnlyList<RsGraphItemIdentity> GraphItems { get; init; }

    public required SoundBankIds MainBank { get; init; }

    public required SoundBankIds PreviewBank { get; init; }

    /// <summary>The counter IV of every encrypted SNG; null draws a fresh one per SNG,
    /// as a shipped build always does.</summary>
    public byte[]? SngIv { get; init; }
}

internal readonly record struct RsArrangementIdentity(Guid PersistentId, int MasterId);

internal readonly record struct RsGraphItemIdentity(Guid Uuid, Guid? Llid);

/// <summary>The ids a generated Wwise bank carries. <see cref="FileId" /> also names
/// the wem entry.</summary>
internal readonly record struct SoundBankIds(uint BankId, int SoundId, int FileId, uint ActionId, uint ParentBusId);
