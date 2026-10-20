namespace DotCharter.Services.Audio.StemSeparation.GuitarRoles;

/// <summary>
///     The names Guitar roles uses: which separated stem is THE guitar, the mixer
///     keys of the two derived stems, and where their files live.
///     <para>
///         The derived files live in a <see cref="FolderName" /> SUBFOLDER beside the
///         guitar stem, never beside it: the mixer adopts every top-level WAV of a
///         <c>stems/</c> folder a project does not reference, and
///         <c>StemFolderBackup</c> treats every top-level WAV as a model output, so a
///         derived WAV at the top level would come back as a third guitar.
///     </para>
/// </summary>
public static class GuitarRoleStems
{
    /// <summary>Mixer key of the centre half. Contains "guitar", so Play Mode's
    /// "mute my instrument" and its stem duck still treat it as the guitar.</summary>
    public const string LeadName = "guitar-lead";

    /// <summary>Mixer key of the sides half.</summary>
    public const string RhythmName = "guitar-rhythm";

    public const string FolderName = "guitar-roles";

    public const string SidecarName = "split.json";

    /// <summary>The model's guitar stem, whichever model produced it: htdemucs_6s and
    /// BS-Roformer-SW both call it "guitar".</summary>
    public static bool IsGuitarStem(string? name) =>
        name?.Trim().ToLowerInvariant() is "guitar" or "guitars";

    public static bool IsRoleStem(string? name) =>
        string.Equals(name, LeadName, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, RhythmName, StringComparison.OrdinalIgnoreCase);

    public static string FolderFor(string guitarStemPath) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(guitarStemPath)) ?? string.Empty, FolderName);
}
