namespace DotCharter.Tests.Services.Audio;

/// <summary>
///     The source files the service's lints must read: every
///     <c>JuceAudioPlaybackService*.cs</c> partial (derived, so a new partial is
///     covered the day it appears) PLUS the collaborators extracted from them.
///     An extracted class is no longer a partial, so a lint that globbed
///     only the partials would stop seeing the moved code and pass vacuously —
///     that is why every such lint reads this list instead of its own glob.
/// </summary>
internal static class PlaybackServiceSourceFiles
{
    /// <summary>Repo-relative paths of the collaborators lifted out of the partials.
    /// Add a file here in the same change that moves code out of the service.</summary>
    public static readonly string[] Collaborators =
    [
        "DotCharter.Tone/Audio/ChartSynth/ChartSynthEngineDriver.cs",
        "DotCharter.Tone/Audio/Diagnostics/AudioHealthMonitor.cs",
        "DotCharter.Tone/Audio/ExternalInsert/ExternalProcessInserts.cs",
        "DotCharter.Tone/Audio/Reamp/ReampTakes.cs",
    ];

    /// <summary>Absolute paths, partials then collaborators, each group in ordinal order.
    /// Fails when a listed collaborator is gone, so the list cannot rot silently.</summary>
    public static string[] All()
    {
        var root = TestPaths.RepoRoot();
        var partials = Directory.GetFiles(Path.Combine(root, "DotCharter.Tone", "Audio"), "JuceAudioPlaybackService*.cs")
            .OrderBy(p => p, StringComparer.Ordinal);
        var collaborators = Collaborators.Select(rel => Path.Combine(root, rel)).ToArray();
        foreach (var path in collaborators)
            Assert.True(File.Exists(path), $"{path} is listed as a JuceAudioPlaybackService collaborator but is gone; update PlaybackServiceSourceFiles.");
        return [.. partials, .. collaborators];
    }
}
