namespace DotCharter.Presentation.Shared;

/// <summary>
///     A disk size as the settings rows print it: whole KB and MB, one decimal
///     for GB, in the reader's culture.
/// </summary>
public static class ByteSizeText
{
    private const long KiB = 1024;
    private const long MiB = KiB * 1024;
    private const long GiB = MiB * 1024;

    public static string Format(long bytes)
    {
        if (bytes >= GiB)
        {
            return $"{bytes / (double)GiB:0.0} GB";
        }

        if (bytes >= MiB)
        {
            return $"{bytes / (double)MiB:0} MB";
        }

        return $"{bytes / (double)KiB:0} KB";
    }
}
