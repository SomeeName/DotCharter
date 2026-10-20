namespace DotCharter.Services.Update;

/// <summary>How a version is printed to the user: three parts unless a revision is set.</summary>
public static class UpdateVersionText
{
    public static string Format(Version version) =>
        version.Revision <= 0 && version.Build >= 0 ? version.ToString(3) : version.ToString();
}
