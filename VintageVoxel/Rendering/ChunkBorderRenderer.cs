using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace VintageVoxel;

/// <summary>
/// Renders wireframe boxes (GL_LINES) at the boundaries of every loaded chunk.
///
/// Each chunk occupies a <see cref="Chunk.Size"/> × <see cref="Chunk.Size"/>
/// × <see cref="Chunk.Size"/> world-unit box starting at
/// (chunkX * Size, 0, chunkZ * Size).  12 edges per chunk are drawn in red.
///
/// Call <see cref="UpdateGeometry"/> whenever the set of loaded chunks changes,
/// then call <see cref="Render"/> every frame (with chunk borders enabled).
/// </summary>
public sealed class ChunkBorderRenderer : IDisposable
{
    private readonly Shader _shader;
    private readonly int _vao;
    private readonly int _vbo;

    // Number of line-endpoint vertices currently stored in the VBO.
    private int _vertexCount;

    public ChunkBorderRenderer()
    {
        _shader = new Shader("Shaders/line.vert", "Shaders/line.frag");

        _vao = GL.GenVertexArray();
        GL.BindVertexArray(_vao);

        _vbo = GL.GenBuffer();
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);

        // Attribute 0: 3-float XYZ position, stride = 12 bytes.
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 3 * sizeof(float), 0);
        GL.EnableVertexAttribArray(0);

        GL.BindVertexArray(0);
    }

    /// <summary>
    /// Rebuilds the line VBO from the given chunk keys.
    /// Call whenever <see cref="World.Chunks"/> changes.
    /// </summary>
    public void UpdateGeometry(IEnumerable<Vector3i> chunkKeys)
    {
        // 12 edges × 2 endpoints = 24 vertices per chunk; each vertex = 3 floats.
        var verts = new List<float>();

        foreach (var key in chunkKeys)
        {
            float x0 = key.X * Chunk.Size;
            float y0 = key.Y * Chunk.Size;
            float z0 = key.Z * Chunk.Size;
            float x1 = x0 + Chunk.Size;
            float y1 = y0 + Chunk.Size;
            float z1 = z0 + Chunk.Size;

            // Bottom face (y = 0)
            Emit(verts, x0, y0, z0, x1, y0, z0);
            Emit(verts, x1, y0, z0, x1, y0, z1);
            Emit(verts, x1, y0, z1, x0, y0, z1);
            Emit(verts, x0, y0, z1, x0, y0, z0);

            // Top face (y = Chunk.Size)
            Emit(verts, x0, y1, z0, x1, y1, z0);
            Emit(verts, x1, y1, z0, x1, y1, z1);
            Emit(verts, x1, y1, z1, x0, y1, z1);
            Emit(verts, x0, y1, z1, x0, y1, z0);

            // Four vertical pillars
            Emit(verts, x0, y0, z0, x0, y1, z0);
            Emit(verts, x1, y0, z0, x1, y1, z0);
            Emit(verts, x1, y0, z1, x1, y1, z1);
            Emit(verts, x0, y0, z1, x0, y1, z1);
        }

        float[] data = verts.ToArray();
        _vertexCount = data.Length / 3; // 3 floats per vertex endpoint

        GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
        GL.BufferData(BufferTarget.ArrayBuffer, data.Length * sizeof(float),
                      data, BufferUsageHint.DynamicDraw);
    }

    private static void Emit(List<float> v,
                              float x0, float y0, float z0,
                              float x1, float y1, float z1)
    {
        v.Add(x0); v.Add(y0); v.Add(z0);
        v.Add(x1); v.Add(y1); v.Add(z1);
    }

    /// <summary>
    /// Draws all chunk-border lines using the current view/projection matrices.
    /// Temporarily disables depth test so borders are always visible.
    /// </summary>
    public void Render(ref Matrix4 view, ref Matrix4 projection)
    {
        if (_vertexCount == 0) return;

        // Draw on top of geometry so borders are readable even inside terrain.
        GL.Disable(EnableCap.DepthTest);

        _shader.Use();
        _shader.SetMatrix4("view", ref view);
        _shader.SetMatrix4("projection", ref projection);
        _shader.SetVector3("uColor", new Vector3(1f, 0f, 0f)); // red

        GL.BindVertexArray(_vao);
        GL.DrawArrays(PrimitiveType.Lines, 0, _vertexCount);
        GL.BindVertexArray(0);

        GL.Enable(EnableCap.DepthTest);
    }

    public void Dispose()
    {
        GL.DeleteVertexArray(_vao);
        GL.DeleteBuffer(_vbo);
        _shader.Dispose();
    }
}
