namespace DotCharter.Core.Domain.Project;

/// <summary>
///     The shape of <see cref="ChartProject.ProjectId" />: 32 lowercase hex digits
///     (a <see cref="Guid" /> in its <c>"N"</c> format). Anything else read off disk
///     is not an id — it is treated as absent and a fresh one is derived.
///     <para>
///         The shape is load-bearing beyond tidiness: the AI chat history names a
///         DIRECTORY after the id (<c>ai-chats/&lt;id&gt;/</c>), so an id that could
///         carry a separator or <c>..</c> would be a path traversal from one edited
///         <c>.dcproj</c> attribute.
///     </para>
/// </summary>
public static class ProjectIdentity
{
    /// <summary>The length of a well-formed id.</summary>
    public const int Length = 32;

    /// <summary>A brand-new id (a random GUID, <c>"N"</c> format).</summary>
    public static string NewId() => Guid.NewGuid().ToString("N");

    /// <summary>True for exactly 32 characters of <c>0-9a-f</c>.</summary>
    public static bool IsWellFormed(string? id)
    {
        if (id is null || id.Length != Length) return false;
        foreach (var c in id)
        {
            if (!char.IsAsciiHexDigitLower(c)) return false;
        }

        return true;
    }
}
