namespace DotCharter.Services.Export.DlcBuilder;

/// <summary>Records generated XML and refuses to recycle a bundle containing anybody else's bytes.</summary>
internal static class OwnedDlcBuilderBundle
{
    internal static void Record(string directory)
    {
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            CompanionOwnership.AdoptMovedFile(file);
    }

    internal static void RequireOwned(string directory)
    {
        if (!Directory.Exists(directory)) return;
        if (Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Any(file => !CompanionOwnership.IsUnmodified(file)))
            throw new IOException($"The XML bundle contains files whose ownership cannot be verified: '{directory}'.");
    }

    internal static void Recycle(string directory) => Recycle(directory, RecycleBin.Remove);

    internal static void Recycle(string directory, Action<string> recycle)
    {
        if (!Directory.Exists(directory)) return;
        RequireOwned(directory);
        recycle(directory);
        if (Directory.Exists(directory)) throw new IOException($"The XML bundle could not be recycled: '{directory}'.");
    }
}
