using DotCharter.Presentation.Dialogs;

namespace DotCharter.UI.ViewModels.Shell;

/// <summary>Opens a dialog from a command: a fault is observed and logged, never
/// left to surface as an unobserved task exception.</summary>
internal static class DialogLaunch
{
    public static void Run(IAppLogger logger, Func<Task> show, string what)
    {
        try
        {
            show().Observe(logger, what);
        }
        catch (InvalidOperationException ex)
        {
            logger.Error(AppLogCategory.Application, $"The {what} dialog could not open.", ex);
        }
    }
}
