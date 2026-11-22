namespace DotCharter.Services.Export.Rocksmith;

/// <summary>A refused test-package destination: callers localize the message key and log the detailed reason.</summary>
public sealed class RocksmithTestPackageOwnershipException : IOException
{
    public const string MessageKey = "Dialog_Export_TestPackageRefused";

    public RocksmithTestPackageOwnershipException(string message, Exception? innerException = null)
        : base(message, innerException) { }
}
