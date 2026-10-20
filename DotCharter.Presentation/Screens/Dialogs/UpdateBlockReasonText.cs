using DotCharter.Localization;
using DotCharter.Services.Update;

namespace DotCharter.Presentation.Screens.Dialogs;

/// <summary>Maps an <see cref="UpdateApplyBlockReason" /> to its localized explanation; empty for none.</summary>
internal static class UpdateBlockReasonText
{
    public static string Describe(ILocalizationService localization, UpdateApplyBlockReason reason) => reason switch
    {
        UpdateApplyBlockReason.None => string.Empty,
        UpdateApplyBlockReason.InstallDirNotWritable => localization["Update_InstallDirNotWritable"],
        UpdateApplyBlockReason.UnknownInstallLayout => localization["Update_UnknownInstallLayout"],
        _ => localization["Update_NotReleaseInstall"]
    };
}
