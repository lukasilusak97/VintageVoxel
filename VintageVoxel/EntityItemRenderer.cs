using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace VintageVoxel;

/// <summary>
/// Renders all dropped <see cref="EntityItem"/> entities as small spinning
/// textured quads, reusing the world <see cref="Shader"/> and texture atlas.
///
/// WHY reuse the world shader?
///   The shader already handles light / AO / atlas UVs; passing light=1, ao=1
///   per vertex makes dropped items full-bright without any shader changes.
///
/// One draw call per entity:
///   Entity counts in a typical session are small (single digits), so the
///   per-draw overhead is negligible compared to chunk rendering.
/// </summary>
public sealed class EntityItemRenderer : IDisposable
{
    // One shared VAO whose VBO is re-filled before each draw call.
    // Contains exactly 4 vertices: a unit square in the local XY plane.
    private readonly int _vao;
    private readonly int _vbo;
    private readonly int _ebo;

    // 7 floats per vertex: x y z u v light ao  (matches the world shader layout)
    private readonly float[] _verts = new float[4 * 7];

    // Two CCW triangles from a quad — never changes.
    private static readonly uint[] QuadIndices = { 0, 1, 2, 2, 3, 0 };

    public EntityItemRenderer()
    {
        _vao = GL.GenVertexArray();
        GL.BindVertexArray(_vao);

        // Dynamic VBO — contents change every entity draw call.
        _vbo = GL.GenBuffer();
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
        GL.BufferData(BufferTarget.ArrayBuffer, _verts.Length * sizeof(float),
                      IntPtr.Zero, BufferUsageHint.DynamicDraw);

        // Static EBO — index pattern is always the same.
        _ebo = GL.GenBuffer();
        GL.BindBuffer(BufferTarget.ElementArrayBuffer, _ebo);
        GL.BufferData(BufferTarget.ElementArrayBuffer,
                      QuadIndices.Length * sizeof(uint), QuadIndices,
                      BufferUsageHint.StaticDraw);

        // Vertex attribute layout mirrors UploadChunk in Game.cs (stride = 28 bytes).
        // Location 0 — position (3 floats, offset 0)
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false,
                               7 * sizeof(float), 0);
        GL.EnableVertexAttribArray(0);

        // Location 1 — UV (2 floats, offset 12)
        GL.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false,
                               7 * sizeof(float), 3 * sizeof(float));
        GL.EnableVertexAttribArray(1);

        // Location 2 — light (1 float, offset 20)
        GL.VertexAttribPointer(2, 1, VertexAttribPointerType.Float, false,
                               7 * sizeof(float), 5 * sizeof(float));
        GL.EnableVertexAttribArray(2);

        // Location 3 — ambient occlusion (1 float, offset 24)
        GL.VertexAttribPointer(3, 1, VertexAttribPointerType.Float, false,
                               7 * sizeof(float), 6 * sizeof(float));
        GL.EnableVertexAttribArray(3);

        GL.BindVertexArray(0);
    }

    // -------------------------------------------------------------------------
    // Rendering
    // -------------------------------------------------------------------------

    /// <summary>
    /// Draws every entity in <paramref name="entities"/> using <paramref name="shader"/>.
    /// The caller must have already bound the atlas texture and set the view +
    /// projection uniforms; only the per-entity model matrix changes here.
    /// </summary>
    public void Render(IReadOnlyList<EntityItem> entities, Shader shader)
    {
        if (entities.Count == 0) return;

        GL.Disable(EnableCap.CullFace); // Quads need to be visible from both sides.

        GL.BindVertexArray(_vao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);

        foreach (var entity in entities)
        {
            // Atlas UV range for this item's tile.
            float u0 = entity.Item.TextureId * TextureAtlas.TileUvWidth;
            float u1 = u0 + TextureAtlas.TileUvWidth;
            const float v0 = 0f, v1 = 1f;

            // Unit quad centred at local origin in the XY plane (faces +Z).
            // light = 1.0, ao = 1.0 — entities are always full-bright.
            WriteVertex(0, -0.5f, -0.5f, 0f, u0, v1);
            WriteVertex(1, 0.5f, -0.5f, 0f, u1, v1);
            WriteVertex(2, 0.5f, 0.5f, 0f, u1, v0);
            WriteVertex(3, -0.5f, 0.5f, 0f, u0, v0);

            GL.BufferSubData(BufferTarget.ArrayBuffer, IntPtr.Zero,
                             _verts.Length * sizeof(float), _verts);

            // Scale → spin → translate to float world position.
            var model =
                Matrix4.CreateScale(0.35f) *
                Matrix4.CreateRotationY(entity.SpinAngle) *
                Matrix4.CreateTranslation(entity.Position.X,
                                          entity.Position.Y + EntityItem.HoverHeight,
                                          entity.Position.Z);
            shader.SetMatrix4("model", ref model);

            GL.DrawElements(PrimitiveType.Triangles, 6,
                            DrawElementsType.UnsignedInt, 0);
        }

        GL.BindVertexArray(0);
        GL.Enable(EnableCap.CullFace); // Restore for chunk rendering.
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private void WriteVertex(int index, float x, float y, float z, float u, float v)
    {
        int i = index * 7;
        _verts[i + 0] = x;
        _verts[i + 1] = y;
        _verts[i + 2] = z;
        _verts[i + 3] = u;
        _verts[i + 4] = v;
        _verts[i + 5] = 1.0f; // light
        _verts[i + 6] = 1.0f; // ao
    }

    // -------------------------------------------------------------------------
    // Lifetime
    // -------------------------------------------------------------------------

    public void Dispose()
    {
        GL.DeleteVertexArray(_vao);
        GL.DeleteBuffer(_vbo);
        GL.DeleteBuffer(_ebo);
    }
}
