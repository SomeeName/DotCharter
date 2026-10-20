using System.Numerics;
using Avalonia.Media;
using DotCharter.Rendering.Vulkan;

namespace DotCharter.Rendering.Batching;

/// <summary>
///     Batches highway geometry as INDEXED quads (0.15.6 / plan 5.9): every quad
///     is 4 unique vertices; the GPU expands them through the renderer's shared
///     static index buffer (0,1,2 / 0,2,3 per quad). All public entry points emit
///     whole quads — <see cref="AddVertex" /> is private on purpose so a batch can
///     never become quad-misaligned (the renderer draws <c>floats / 52 * 6</c>
///     indices and would silently drop a trailing partial quad).
/// </summary>
public class QuadBatcher
{
    private readonly float[] _vertexData = new float[150000];
    private readonly IVulkanRenderService _vulkanRenderer;
    private Vector3 _camPos;
    private bool _depthWrite;
    private float _fadeEnd;
    private float _fadeStart;
    private Matrix4x4 _mvp;
    private VulkanTexture? _texture;
    private float _timeMs;
    private HighwayEffects _effects;
    private int _vertexCount;

    public QuadBatcher(IVulkanRenderService vulkanRenderer) => _vulkanRenderer = vulkanRenderer;

    /// <summary>Floats currently staged for the next <see cref="Flush" /> —
    /// 52 per quad. Diagnostic/testable read of what a render pass emitted,
    /// mirroring <c>TimelineBatcher.SolidVertexCount</c> on the 2D side.</summary>
    public int StagedFloatCount => _vertexCount;

    /// <summary>Test seam: the raw floats staged for the next
    /// <see cref="Flush" />, 13 per vertex / 52 per quad in the layout
    /// <c>AddVertex</c> writes (x, y, z, r, g, b, a, nx, ny, nz, u, v, type).
    /// Lets a headless test assert WHERE a pass put its geometry, not just how
    /// much of it there was.</summary>
    internal ReadOnlySpan<float> StagedVertices => _vertexData.AsSpan(0, _vertexCount);

    /// <summary>Floats per vertex in <see cref="StagedVertices" />.</summary>
    internal const int FloatsPerVertex = 13;

    public void BeginPass(Matrix4x4 mvp, Vector3 camPos, float fadeStart, float fadeEnd, float timeMs, bool depthWrite,
        HighwayEffects effects = HighwayEffects.None)
    {
        _mvp = mvp;
        _camPos = camPos;
        _fadeStart = fadeStart;
        _fadeEnd = fadeEnd;
        _timeMs = timeMs;
        _depthWrite = depthWrite;
        _effects = effects;
    }

    public void SetTexture(VulkanTexture? texture)
    {
        if (_texture != texture) Flush();
        _texture = texture;
    }

    public void Flush()
    {
        if (_vertexCount == 0) return;
        _vulkanRenderer.DrawBatch(_vertexData, _vertexCount, _mvp, _camPos, _fadeStart, _fadeEnd, _timeMs, _depthWrite,
            _effects, _texture);
        _vertexCount = 0;
    }

    private void EnsureCapacity(int requiredAdded)
    {
        if (_vertexCount + requiredAdded > _vertexData.Length) Flush();
    }

    private void AddVertex(Vector3 pos, Color c, Vector3 norm, Vector2 tex, float type)
    {
        _vertexData[_vertexCount++] = pos.X;
        _vertexData[_vertexCount++] = pos.Y;
        _vertexData[_vertexCount++] = pos.Z;
        _vertexData[_vertexCount++] = c.R / 255f;
        _vertexData[_vertexCount++] = c.G / 255f;
        _vertexData[_vertexCount++] = c.B / 255f;
        _vertexData[_vertexCount++] = c.A / 255f;
        _vertexData[_vertexCount++] = norm.X;
        _vertexData[_vertexCount++] = norm.Y;
        _vertexData[_vertexCount++] = norm.Z;
        _vertexData[_vertexCount++] = tex.X;
        _vertexData[_vertexCount++] = tex.Y;
        _vertexData[_vertexCount++] = type;
    }

    // 4 vertices * 13 floats — one indexed quad.
    private const int FloatsPerQuad = 52;

    public void AddQuad(Vector3 p1, Vector3 p2, Vector3 p3, Vector3 p4, Vector3 normal, Color c, float type = 0.0f)
    {
        EnsureCapacity(FloatsPerQuad);
        AddVertex(p1, c, normal, new Vector2(0, 0), type);
        AddVertex(p2, c, normal, new Vector2(1, 0), type);
        AddVertex(p3, c, normal, new Vector2(1, 1), type);
        AddVertex(p4, c, normal, new Vector2(0, 1), type);
    }

    public void AddQuad(Vector3 p1, Vector3 p2, Vector3 p3, Vector3 p4, Vector3 normal, Color c1, Color c2, Color c3,
        Color c4, float type = 0.0f)
    {
        EnsureCapacity(FloatsPerQuad);
        AddVertex(p1, c1, normal, new Vector2(0, 0), type);
        AddVertex(p2, c2, normal, new Vector2(1, 0), type);
        AddVertex(p3, c3, normal, new Vector2(1, 1), type);
        AddVertex(p4, c4, normal, new Vector2(0, 1), type);
    }

    /// <summary>Quad with explicit per-corner texture coordinates (for callers
    /// whose UV orientation differs from the standard corner mapping — board
    /// inlays, fingering sprites).</summary>
    public void AddQuadUv(Vector3 p1, Vector3 p2, Vector3 p3, Vector3 p4, Vector3 normal, Color c,
        Vector2 uv1, Vector2 uv2, Vector2 uv3, Vector2 uv4, float type)
    {
        EnsureCapacity(FloatsPerQuad);
        AddVertex(p1, c, normal, uv1, type);
        AddVertex(p2, c, normal, uv2, type);
        AddVertex(p3, c, normal, uv3, type);
        AddVertex(p4, c, normal, uv4, type);
    }

    public void AddSafeFlatQuad(float x0, float x1, float z0, float z1, float y, Color c, float type = 1.0f) => AddSafeFlatQuad(x0, x1, z0, z1, y, c, c, type);

    public void AddSafeFlatQuad(float x0, float x1, float z0, float z1, float y, Color color0, Color color1,
        float type = 1.0f)
    {
        EnsureCapacity(FloatsPerQuad);
        var p1 = new Vector3(x0, y, z0);
        var p2 = new Vector3(x1, y, z0);
        var p3 = new Vector3(x1, y, z1);
        var p4 = new Vector3(x0, y, z1);

        AddVertex(p1, color0, Vector3.UnitY, new Vector2(0, 0), type);
        AddVertex(p2, color0, Vector3.UnitY, new Vector2(1, 0), type);
        AddVertex(p3, color1, Vector3.UnitY, new Vector2(1, 1), type);
        AddVertex(p4, color1, Vector3.UnitY, new Vector2(0, 1), type);
    }

    public void AddTexturedQuad_Matrix(Matrix4x4 m, float halfWidth, Color c, float tx0, float tx1, float ty0,
        float ty1, float type)
    {
        EnsureCapacity(FloatsPerQuad);
        var v1 = Vector3.Transform(new Vector3(-halfWidth, halfWidth, 0), m);
        var v2 = Vector3.Transform(new Vector3(halfWidth, halfWidth, 0), m);
        var v3 = Vector3.Transform(new Vector3(halfWidth, -halfWidth, 0), m);
        var v4 = Vector3.Transform(new Vector3(-halfWidth, -halfWidth, 0), m);

        AddVertex(v1, c, Vector3.UnitZ, new Vector2(tx0, ty0), type);
        AddVertex(v2, c, Vector3.UnitZ, new Vector2(tx1, ty0), type);
        AddVertex(v3, c, Vector3.UnitZ, new Vector2(tx1, ty1), type);
        AddVertex(v4, c, Vector3.UnitZ, new Vector2(tx0, ty1), type);
    }
}
