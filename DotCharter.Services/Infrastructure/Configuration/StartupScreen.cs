namespace DotCharter.Services.Infrastructure.Configuration;

/// <summary>
///     What the shell opens into at startup. Chosen on the first-run onboarding
///     wizard's role step ("I'm a Charter" → <see cref="Editor"/>, "I'm a
///     Player" → <see cref="PlayMode"/>) and changeable later under Settings ▸
///     General. Persisted as <c>AppConfig.StartupScreen</c>; the older boolean
///     <c>AppConfig.StartInPlayMode</c> is an alias over the same field so a
///     pre-0.49 config file (and the Play Mode menu's own "show at startup"
///     toggle) keep working.
/// </summary>
public enum StartupScreen
{
    /// <summary>The Play Mode front door (the default, and today's behaviour).</summary>
    PlayMode,

    /// <summary>The chart editor workspace.</summary>
    Editor
}
