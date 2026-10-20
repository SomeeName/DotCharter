namespace DotCharter.Tone.Audio.Dsp;

/// <summary>
/// Safe no-op <see cref="IDspChainService"/> used when the native DSP ABI is
/// absent (a build without the tone-rig entry points) and as the headless/test
/// default. Empty catalogue, no chain, every mutation reports failure — so the
/// app boots and the pedalboard UI shows its empty state instead of crashing.
/// </summary>
public sealed class NullDspChainService : IDspChainService
{
    /// <param name="player">The seat this stand-in represents, so a co-op caller
    /// that resolved player 2 and got the null service still reads back player 2.</param>
    public NullDspChainService(int player = 0) => Player = player;

    public void AttachEngine(nint engineHandle) { }
    /// <summary>Always false: this stand-in hosts no rig at all, so nothing it is
    /// handed is ever a live engine.</summary>
    public bool HasEngine => false;
    public int Player { get; }
    public IReadOnlyList<DspModuleType> Catalog => [];
    public IReadOnlyList<DspNode> GetNodes() => [];
    public event Action? NodesChanged { add { } remove { } }
    public event Action? EngineAttached { add { } remove { } }
    public DspNode? AddNode(string moduleTypeId, bool initiallyBypassed = false) => null;
    public bool RemoveNode(int nodeId) => false;
    public bool MoveNode(int nodeId, int newIndex) => false;
    public bool SetBypass(int nodeId, bool bypassed) => false;
    public bool SetControlAssignments(int nodeId, IReadOnlyList<RigControlAssignment> assignments) => false;
    public bool SetParam(int nodeId, string paramId, float value) => false;
    public float? GetParam(int nodeId, string paramId) => null;
    public bool SetNodeString(int nodeId, string paramId, string value) => false;
    public string? GetNodeString(int nodeId, string paramId) => null;
    public bool SetNodeBranch(int nodeId, int branch) => false;
    public int GetNodeBranch(int nodeId) => 0;
}
