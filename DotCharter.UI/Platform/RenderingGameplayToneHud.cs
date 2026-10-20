using DotCharter.Rendering.Data;
using DotCharter.Services.Audio.Coordination;

namespace DotCharter.UI.Platform;

/// <summary>Forwards the tone coordinator's applied preset to the renderer's
/// tone-HUD channel, which the 3D snapshot builder reads each frame.</summary>
public sealed class RenderingGameplayToneHud : IGameplayToneHud
{
    public void SetRig(string? name, bool approximated) => GameplayToneHud.SetRig(name, approximated);

    public void ClearRig() => GameplayToneHud.ClearRig();
}
