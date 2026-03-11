using OpenTK.Graphics.OpenGL4;

namespace VintageVoxel.Rendering;

/// <summary>Immutable handle to a mesh uploaded to the GPU (VAO + VBO + EBO).</summary>
public readonly struct GpuMesh
{
    public int Vao { get; init; }
    public int Vbo { get; init; }
    public int Ebo { get; init; }
    public int IndexCount { get; init; }
}

/// <summary>
/// Centralizes VAO/VBO/EBO creation and tracks all allocations for safe cleanup.
/// All meshes allocated through this class are tracked; call <see cref="Free"/> to
/// release individual meshes early, or <see cref="Dispose"/> to release everything remaining.
/// </summary>
public sealed class GpuResourceManager : IDisposable
{
    // Tracks every mesh that has not yet been explicitly freed.
    private readonly List<GpuMesh> _tracked = new();

    // -------------------------------------------------------------------------
    // Allocation
    // -------------------------------------------------------------------------

    /// <summary>
    /// Uploads static vertex and index data to the GPU and returns a tracked handle.
    /// </summary>
    /// <param name="vertices">Interleaved vertex data.</param>
    /// <param name="indices">Triangle index list.</param>
    /// <param name="stride">Floats per vertex — determines the attribute layout (7 = world, 4 = HUD).</param>
    public GpuMesh UploadMesh(float[] vertices, uint[] indices, int stride)
    {
        int vao = GL.GenVertexArray();
        GL.BindVertexArray(vao);

        int vbo = GL.GenBuffer();
        GL.BindBuffer(BufferTarget.ArrayBuffer, vbo);
        GL.BufferData(BufferTarget.ArrayBuffer,
                      vertices.Length * sizeof(float), vertices,
                      BufferUsageHint.StaticDraw);

        int ebo = GL.GenBuffer();
        GL.BindBuffer(BufferTarget.ElementArrayBuffer, ebo);
        GL.BufferData(BufferTarget.ElementArrayBuffer,
                      indices.Length * sizeof(uint), indices,
                      BufferUsageHint.StaticDraw);

        SetupAttribs(stride);
        GL.BindVertexArray(0);

        var mesh = new GpuMesh { Vao = vao, Vbo = vbo, Ebo = ebo, IndexCount = indices.Length };
        _tracked.Add(mesh);
        return mesh;
    }

    /// <summary>
    /// Allocates GPU buffers for a dynamic-draw mesh whose vertex data is re-uploaded
    /// each frame via <c>GL.BufferSubData</c>.  The index buffer is uploaded once as
    /// static data.
    /// </summary>
    /// <param name="vertexFloatCapacity">Maximum number of floats the vertex buffer must hold.</param>
    /// <param name="staticIndices">Index data uploaded once and never changed.</param>
    /// <param name="stride">Floats per vertex — determines the attribute layout.</param>
    public GpuMesh AllocateDynamicMesh(int vertexFloatCapacity, uint[] staticIndices, int stride)
    {
        int vao = GL.GenVertexArray();
        GL.BindVertexArray(vao);

        int vbo = GL.GenBuffer();
        GL.BindBuffer(BufferTarget.ArrayBuffer, vbo);
        GL.BufferData(BufferTarget.ArrayBuffer,
                      vertexFloatCapacity * sizeof(float), IntPtr.Zero,
                      BufferUsageHint.DynamicDraw);

        int ebo = GL.GenBuffer();
        GL.BindBuffer(BufferTarget.ElementArrayBuffer, ebo);
        GL.BufferData(BufferTarget.ElementArrayBuffer,
                      staticIndices.Length * sizeof(uint), staticIndices,
                      BufferUsageHint.StaticDraw);

        SetupAttribs(stride);
        GL.BindVertexArray(0);

        var mesh = new GpuMesh { Vao = vao, Vbo = vbo, Ebo = ebo, IndexCount = staticIndices.Length };
        _tracked.Add(mesh);
        return mesh;
    }

    // -------------------------------------------------------------------------
    // Lifecycle
    // -------------------------------------------------------------------------

    /// <summary>Releases the GPU objects for <paramref name="mesh"/> immediately.</summary>
    public void Free(GpuMesh mesh)
    {
        _tracked.Remove(mesh);
        GL.DeleteVertexArray(mesh.Vao);
        GL.DeleteBuffer(mesh.Vbo);
        GL.DeleteBuffer(mesh.Ebo);
    }

    /// <summary>Frees all tracked meshes that have not yet been explicitly freed.</summary>
    public void Dispose()
    {
        foreach (var mesh in _tracked)
        {
            GL.DeleteVertexArray(mesh.Vao);
            GL.DeleteBuffer(mesh.Vbo);
            GL.DeleteBuffer(mesh.Ebo);
        }
        _tracked.Clear();
    }

    // -------------------------------------------------------------------------
    // Vertex attribute setup
    // -------------------------------------------------------------------------

    private static void SetupAttribs(int stride)
    {
        if (stride == 8)
        {
            // World / chunk vertex layout: pos(3) + uv(2) + sunLight(1) + blockLight(1) + ao(1)
            int s = 8 * sizeof(float);
            GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, s, 0);
            GL.EnableVertexAttribArray(0);
            GL.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, s, 3 * sizeof(float));
            GL.EnableVertexAttribArray(1);
            GL.VertexAttribPointer(2, 1, VertexAttribPointerType.Float, false, s, 5 * sizeof(float));
            GL.EnableVertexAttribArray(2);
            GL.VertexAttribPointer(3, 1, VertexAttribPointerType.Float, false, s, 6 * sizeof(float));
            GL.EnableVertexAttribArray(3);
            GL.VertexAttribPointer(4, 1, VertexAttribPointerType.Float, false, s, 7 * sizeof(float));
            GL.EnableVertexAttribArray(4);
        }
        else if (stride == 7)
        {
            // Entity / model vertex layout: pos(3) + uv(2) + light(1) + ao(1)
            // Maps: attr2 = sunLight (combined), attr3 = blockLight (0), attr4 = ao
            int s = 7 * sizeof(float);
            GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, s, 0);
            GL.EnableVertexAttribArray(0);
            GL.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, s, 3 * sizeof(float));
            GL.EnableVertexAttribArray(1);
            GL.VertexAttribPointer(2, 1, VertexAttribPointerType.Float, false, s, 5 * sizeof(float));
            GL.EnableVertexAttribArray(2);
            // blockLight is not in the buffer — use a standalone attrib default of 0.
            GL.DisableVertexAttribArray(3);
            GL.VertexAttrib1(3, 0.0f);
            GL.VertexAttribPointer(4, 1, VertexAttribPointerType.Float, false, s, 6 * sizeof(float));
            GL.EnableVertexAttribArray(4);
        }
        else if (stride == 4)
        {
            // HUD vertex layout: pos(2) + uv(2)
            GL.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 4 * sizeof(float), 0);
            GL.EnableVertexAttribArray(0);
            GL.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, 4 * sizeof(float), 2 * sizeof(float));
            GL.EnableVertexAttribArray(1);
        }
        else
        {
            GL.BindVertexArray(0);
            throw new ArgumentException($"Unsupported vertex stride: {stride}. Expected 4 (HUD), 7 (entity/model), or 8 (world chunk).");
        }
    }
}
