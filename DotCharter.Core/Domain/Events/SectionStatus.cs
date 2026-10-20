namespace DotCharter.Core.Domain.Events;

/// <summary>
///     Charting workflow status of a SECTION marker (v0.29 section tracker).
///     Lives on the section's <see cref="EventPoint" />; <see cref="None" />
///     is the default and is NOT serialized, so charts without statuses stay
///     byte-identical and pre-v0.29 files read back unchanged. The rscp
///     attribute (<c>ss</c>) stores the lowercase NAME (not the ordinal), so
///     the enum can grow without renumbering; unknown values parse as
///     <see cref="None" /> — never a load failure.
/// </summary>
public enum SectionStatus
{
    None = 0,
    Todo,
    Draft,
    Review,
    Done
}
