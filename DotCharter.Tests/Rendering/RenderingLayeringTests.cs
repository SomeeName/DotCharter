using System.Reflection;
using DotCharter.Rendering.Engines;
using SkiaSharp;

namespace DotCharter.Tests.Rendering;

/// <summary>
///     The dependency direction is absolute: <c>DotCharter.Rendering</c> sits
///     below <c>DotCharter.UI</c> and <c>DotCharter.Services</c> and may never
///     reference either. It had exactly one upward leak — the 3D chart textures
///     were opened at runtime through a hardcoded
///     <c>avares://DotCharter/Assets/Textures/...</c> URI, i.e. by the UI
///     assembly's NAME. Nothing in any csproj expressed that dependency, so no
///     build step could catch a rename or a moved asset; the failure surfaced
///     only at runtime as a <c>Debug.WriteLine</c> and silent white textures in
///     Release.
///     <para>
///         The reference direction itself moved to
///         <c>Infrastructure/AssemblyLayeringTests</c> in 0.41 and is now a
///         Theory over every layer, not a lone Fact about this one — the
///         Rendering row there (Core only) strictly subsumes the
///         <c>Rendering_references_neither_the_UI_nor_the_service_layer</c> Fact
///         that used to live here. What stays here is the ASSET half, which is
///         specific to this assembly.
///     </para>
/// </summary>
public sealed class RenderingLayeringTests
{
    private static readonly Assembly Rendering = typeof(Fretboard3DEngine).Assembly;

    [Theory]
    [InlineData("notes.png")]
    [InlineData("fingering.png")]
    [InlineData("inlays.png")]
    public void Every_chart_texture_is_an_embedded_resource_of_the_rendering_assembly(string fileName)
    {
        using var stream = Rendering.GetManifestResourceStream($"DotCharter.Rendering.Assets.{fileName}");

        Assert.NotNull(stream);
        Assert.True(stream.Length > 0, $"{fileName} is embedded but empty");
    }

    [Theory]
    [InlineData("notes.png")]
    [InlineData("fingering.png")]
    [InlineData("inlays.png")]
    public void Every_chart_texture_decodes_to_a_non_degenerate_image(string fileName)
    {
        using var stream = Rendering.GetManifestResourceStream($"DotCharter.Rendering.Assets.{fileName}");
        Assert.NotNull(stream);

        using var codec = SKCodec.Create(stream);

        Assert.NotNull(codec);
        Assert.True(codec.Info.Width > 0 && codec.Info.Height > 0,
            $"{fileName} decoded to {codec.Info.Width}x{codec.Info.Height}");
    }

    [Fact]
    public void The_shader_modules_are_embedded_the_same_way()
    {
        // Same mechanism, same failure mode if the LogicalName convention drifts.
        var shaders = Rendering.GetManifestResourceNames()
            .Where(n => n.StartsWith("DotCharter.Rendering.Shaders.", StringComparison.Ordinal) &&
                        n.EndsWith(".spv", StringComparison.Ordinal))
            .ToArray();

        Assert.NotEmpty(shaders);
    }
}
