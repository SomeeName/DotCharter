namespace DotCharter.Services.Export.Rocksmith;

/// <summary>
///     The <c>MasterID_RDV</c> range a package's arrangements carry. Shared by the
///     package WRITER (which draws custom ids from it) and a plugin's origin
///     rule, so it lives with the writer.
/// </summary>
public static class RsMasterIds
{
    /// <summary>
    ///     A <c>MasterID_RDV</c> at or above this is a toolkit-generated id.
    ///     <para>
    ///         <b>This is the one robust signal, and it is robust by accident of
    ///         how the two producers allocate ids.</b> DLC Builder and the Custom
    ///         Song Toolkit draw the id from a random generator over the whole
    ///         positive int range, so a custom package's id is astronomically
    ///         unlikely to be small. Ubisoft hand-assigned ids from small blocks
    ///         (10000+, 30000+, 32700+), so a stock one is never large. The
    ///         gap between the two populations is many orders of magnitude, which
    ///         is why a single threshold works at all.
    ///     </para>
    ///     <para>
    ///         Unlike the toolkit stamp it cannot be dropped: the id has to exist
    ///         and has to be unique, so there is no "clean" value to forge that
    ///         does not risk colliding with a real stock id.
    ///     </para>
    /// </summary>
    public const int CustomMasterIdFloor = 1_000_000;
}
